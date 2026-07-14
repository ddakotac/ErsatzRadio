namespace ErsatzTV.Core.Interrupts;

/// <summary>
///     A podcast RSS feed polled for new episodes, which are downloaded and enqueued as
///     interrupts on the mapped channels - no library or Audiobookshelf in the loop.
/// </summary>
public record RssFeed(
    string Name,
    string Url,
    List<string> Channels,
    int Priority = 1,
    string Style = "replace",
    int DuckPercent = 30,
    int TtlSeconds = 3600,
    bool Enabled = true);
