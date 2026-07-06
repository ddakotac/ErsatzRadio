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
    ///     Removes and returns the highest-priority ELIGIBLE (air time reached) non-expired
    ///     item for the channel, judged against the given stream time; expired items are
    ///     purged (and their temp files deleted).
    /// </summary>
    Option<InterruptQueueItem> TryDequeue(string channelNumber, DateTimeOffset asOf);

    /// <summary>
    ///     Earliest air time strictly after the given stream time, if any - used by the
    ///     session worker to truncate a scheduled transcode at the air boundary.
    /// </summary>
    Option<DateTimeOffset> PeekNextAirTime(string channelNumber, DateTimeOffset after);

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
