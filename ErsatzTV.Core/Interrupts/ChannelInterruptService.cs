using ErsatzTV.Core.Interfaces.Streaming;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Core.Interrupts;

public class ChannelInterruptService : IChannelInterruptService
{
    private readonly Lock _sync = new();
    private readonly Dictionary<string, List<InterruptQueueItem>> _queues = new();
    private readonly Dictionary<string, Action> _forceHandlers = new();
    private readonly ILogger<ChannelInterruptService> _logger;

    public ChannelInterruptService(ILogger<ChannelInterruptService> logger) => _logger = logger;

    public void Enqueue(InterruptQueueItem item)
    {
        Option<Action> maybeForceHandler = Option<Action>.None;

        lock (_sync)
        {
            if (!_queues.TryGetValue(item.ChannelNumber, out List<InterruptQueueItem> queue))
            {
                queue = [];
                _queues.Add(item.ChannelNumber, queue);
            }

            queue.Add(item);

            _logger.LogInformation(
                "Enqueued interrupt {Title} ({Id}) for channel {Channel} with priority {Priority}, expires {ExpiresAt}",
                item.Title,
                item.Id,
                item.ChannelNumber,
                item.Priority,
                item.ExpiresAt);

            if (item.Priority == 0 && _forceHandlers.TryGetValue(item.ChannelNumber, out Action handler))
            {
                maybeForceHandler = handler;
            }
        }

        // invoke outside the lock
        foreach (Action handler in maybeForceHandler)
        {
            try
            {
                _logger.LogInformation(
                    "Force-interrupting current transcode on channel {Channel} for emergency item {Id}",
                    item.ChannelNumber,
                    item.Id);

                handler();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to invoke force-interrupt handler for channel {Channel}", item.ChannelNumber);
            }
        }
    }

    public Option<InterruptQueueItem> TryDequeue(string channelNumber)
    {
        List<InterruptQueueItem> expired = [];
        Option<InterruptQueueItem> result = Option<InterruptQueueItem>.None;

        lock (_sync)
        {
            if (_queues.TryGetValue(channelNumber, out List<InterruptQueueItem> queue))
            {
                DateTimeOffset now = DateTimeOffset.Now;
                expired.AddRange(queue.Where(i => i.ExpiresAt <= now));
                queue.RemoveAll(i => i.ExpiresAt <= now);

                Option<InterruptQueueItem> maybeNext = queue
                    .OrderBy(i => i.Priority)
                    .ThenBy(i => i.EnqueuedAt)
                    .HeadOrNone();

                foreach (InterruptQueueItem next in maybeNext)
                {
                    queue.Remove(next);
                    result = next;
                }
            }
        }

        foreach (InterruptQueueItem item in expired)
        {
            _logger.LogWarning(
                "Dropping expired interrupt {Title} ({Id}) for channel {Channel}; expired at {ExpiresAt}",
                item.Title,
                item.Id,
                item.ChannelNumber,
                item.ExpiresAt);

            CleanUpFile(item);
        }

        return result;
    }

    public List<InterruptQueueItem> List(string channelNumber)
    {
        lock (_sync)
        {
            if (_queues.TryGetValue(channelNumber, out List<InterruptQueueItem> queue))
            {
                return queue
                    .OrderBy(i => i.Priority)
                    .ThenBy(i => i.EnqueuedAt)
                    .ToList();
            }

            return [];
        }
    }

    public bool Remove(string channelNumber, Guid id)
    {
        Option<InterruptQueueItem> removed = Option<InterruptQueueItem>.None;

        lock (_sync)
        {
            if (_queues.TryGetValue(channelNumber, out List<InterruptQueueItem> queue))
            {
                foreach (InterruptQueueItem item in Optional(queue.Find(i => i.Id == id)))
                {
                    queue.Remove(item);
                    removed = item;
                }
            }
        }

        foreach (InterruptQueueItem item in removed)
        {
            CleanUpFile(item);
        }

        return removed.IsSome;
    }

    public int Clear(string channelNumber)
    {
        List<InterruptQueueItem> removed = [];

        lock (_sync)
        {
            if (_queues.TryGetValue(channelNumber, out List<InterruptQueueItem> queue))
            {
                removed.AddRange(queue);
                queue.Clear();
            }
        }

        foreach (InterruptQueueItem item in removed)
        {
            CleanUpFile(item);
        }

        return removed.Count;
    }

    public IDisposable RegisterForceInterruptHandler(string channelNumber, Action handler)
    {
        lock (_sync)
        {
            _forceHandlers[channelNumber] = handler;
        }

        return new HandlerRegistration(this, channelNumber);
    }

    public void CleanUpFile(InterruptQueueItem item)
    {
        if (!item.DeleteFileWhenDone)
        {
            return;
        }

        try
        {
            if (File.Exists(item.Path))
            {
                File.Delete(item.Path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete interrupt temp file {Path}", item.Path);
        }
    }

    private void Unregister(string channelNumber)
    {
        lock (_sync)
        {
            _forceHandlers.Remove(channelNumber);
        }
    }

    private sealed class HandlerRegistration(ChannelInterruptService queue, string channelNumber) : IDisposable
    {
        public void Dispose() => queue.Unregister(channelNumber);
    }
}
