using ErsatzTV.Core.Interfaces.Streaming;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Core.Interrupts;

public class ChannelInterruptService : IChannelInterruptService
{
    private readonly Lock _sync = new();
    private readonly Dictionary<string, List<InterruptQueueItem>> _queues = new();
    private readonly Dictionary<string, Action> _forceHandlers = new();
    private readonly Dictionary<Guid, Timer> _airAtTimers = new();
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

            bool isDue = item.AirAt is null || item.AirAt <= DateTimeOffset.Now;
            if (item.Priority == 0 && isDue && _forceHandlers.TryGetValue(item.ChannelNumber, out Action handler))
            {
                maybeForceHandler = handler;
            }

            // scheduled emergencies (priority 0 + future airAt): if transcode truncation
            // hasn't already landed the boundary by the air time (item enqueued too late),
            // force-cut at the air time; the item then airs within the hls buffer of it
            if (item.Priority == 0 && item.AirAt is { } airAt && airAt > DateTimeOffset.Now)
            {
                TimeSpan delay = airAt - DateTimeOffset.Now;
                _airAtTimers[item.Id] = new Timer(
                    _ => FireScheduledForceCut(item.ChannelNumber, item.Id),
                    null,
                    delay,
                    Timeout.InfiniteTimeSpan);
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

    public Option<InterruptQueueItem> TryDequeue(string channelNumber, DateTimeOffset asOf)
    {
        List<InterruptQueueItem> expired = [];
        Option<InterruptQueueItem> result = Option<InterruptQueueItem>.None;

        lock (_sync)
        {
            if (_queues.TryGetValue(channelNumber, out List<InterruptQueueItem> queue))
            {
                expired.AddRange(queue.Where(i => i.ExpiresAt <= asOf));
                queue.RemoveAll(i => i.ExpiresAt <= asOf);

                foreach (InterruptQueueItem item in expired)
                {
                    CancelAirAtTimer(item.Id);
                }

                Option<InterruptQueueItem> maybeNext = queue
                    .Where(i => i.AirAt is null || i.AirAt <= asOf)
                    .OrderBy(i => i.Priority)
                    .ThenBy(i => i.EnqueuedAt)
                    .HeadOrNone();

                foreach (InterruptQueueItem next in maybeNext)
                {
                    queue.Remove(next);
                    CancelAirAtTimer(next.Id);
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

    public Option<DateTimeOffset> PeekNextAirTime(string channelNumber, DateTimeOffset after)
    {
        lock (_sync)
        {
            if (_queues.TryGetValue(channelNumber, out List<InterruptQueueItem> queue))
            {
                return queue
                    .Where(i => i.AirAt.HasValue && i.AirAt > after && i.ExpiresAt > after)
                    .OrderBy(i => i.AirAt)
                    .HeadOrNone()
                    .Map(i => i.AirAt.Value);
            }

            return Option<DateTimeOffset>.None;
        }
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
                    CancelAirAtTimer(item.Id);
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

                foreach (InterruptQueueItem item in removed)
                {
                    CancelAirAtTimer(item.Id);
                }
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

    private void FireScheduledForceCut(string channelNumber, Guid itemId)
    {
        Option<Action> maybeForceHandler = Option<Action>.None;

        lock (_sync)
        {
            CancelAirAtTimer(itemId);

            // no-op when the item already dequeued (truncation landed it on time)
            bool stillQueued = _queues.TryGetValue(channelNumber, out List<InterruptQueueItem> queue) &&
                               queue.Any(i => i.Id == itemId);

            if (stillQueued && _forceHandlers.TryGetValue(channelNumber, out Action handler))
            {
                maybeForceHandler = handler;
            }
        }

        foreach (Action handler in maybeForceHandler)
        {
            try
            {
                _logger.LogInformation(
                    "Force-interrupting channel {Channel} at the scheduled air time of late-enqueued item {Id}",
                    channelNumber,
                    itemId);

                handler();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to invoke scheduled force-interrupt handler for channel {Channel}",
                    channelNumber);
            }
        }
    }

    private void CancelAirAtTimer(Guid itemId)
    {
        if (_airAtTimers.Remove(itemId, out Timer timer))
        {
            timer.Dispose();
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
