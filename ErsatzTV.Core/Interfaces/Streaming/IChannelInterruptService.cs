using ErsatzTV.Core.Interrupts;

namespace ErsatzTV.Core.Interfaces.Streaming;

public interface IChannelInterruptService
{
    /// <summary>
    ///     Adds an item to the channel's interrupt queue. Priority 0 items also
    ///     invoke the channel's registered force-interrupt handler (if any) so an
    ///     in-flight scheduled transcode is cut short.
    /// </summary>
    void Enqueue(InterruptQueueItem item);

    /// <summary>
    ///     Removes and returns the highest-priority non-expired item for the
    ///     channel; expired items are purged (and their temp files deleted).
    /// </summary>
    Option<InterruptQueueItem> TryDequeue(string channelNumber);

    List<InterruptQueueItem> List(string channelNumber);

    bool Remove(string channelNumber, Guid id);

    int Clear(string channelNumber);

    /// <summary>
    ///     Called by an HLS session worker so priority 0 items can cut the current
    ///     scheduled transcode. Dispose the returned handle to unregister.
    /// </summary>
    IDisposable RegisterForceInterruptHandler(string channelNumber, Action handler);

    /// <summary>Deletes the item's temp file if it was uploaded through the injection api.</summary>
    void CleanUpFile(InterruptQueueItem item);
}
