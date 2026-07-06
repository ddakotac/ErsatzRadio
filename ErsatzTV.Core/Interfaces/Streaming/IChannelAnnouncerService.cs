namespace ErsatzTV.Core.Interfaces.Streaming;

public interface IChannelAnnouncerService
{
    /// <summary>
    ///     If the announcer is enabled for the channel and a new scheduled item starts at
    ///     the given stream time, renders the announcement text via the configured TTS
    ///     endpoint and enqueues it as an interrupt (typically duck-style over the item's
    ///     opening). Safe to call every session loop iteration; deduplicates per item.
    /// </summary>
    Task AnnounceUpcomingItem(string channelNumber, DateTimeOffset at, CancellationToken cancellationToken);
}
