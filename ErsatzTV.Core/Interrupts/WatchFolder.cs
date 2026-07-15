namespace ErsatzTV.Core.Interrupts;

/// <summary>
///     A folder polled for newly arrived audio files, which are enqueued as interrupts
///     on the mapped channels (timely podcast delivery, breaking-news drops).
/// </summary>
public record WatchFolder(
    string Name,
    string Path,
    List<string> Channels,
    int Priority = 1,
    string Style = "replace",
    int DuckPercent = 30,
    int TtlSeconds = 3600,
    bool Enabled = true,
    string IntroText = "",
    string OutroText = "",
    string TtsEndpoint = "",
    string Voice = "",
    string WebhookUrl = "");
