using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interrupts;
using ErsatzTV.FFmpeg;

namespace ErsatzTV.Application.Streaming;

public record GetPlayoutItemProcessByChannelNumber(
    string ChannelNumber,
    StreamingMode Mode,
    DateTimeOffset Now,
    bool StartAtZero,
    bool HlsRealtime,
    DateTimeOffset ChannelStart,
    TimeSpan PtsOffset,
    Option<FrameRate> TargetFramerate,
    bool IsTroubleshooting,
    Option<int> FFmpegProfileId) : FFmpegProcessRequest(
    ChannelNumber,
    Mode,
    Now,
    StartAtZero,
    HlsRealtime,
    ChannelStart,
    PtsOffset,
    FFmpegProfileId)
{
    /// <summary>
    ///     When set and within the playout item's span, the transcode is truncated at this
    ///     instant (wall/stream time) so a scheduled interrupt can start exactly on time.
    ///     Only applies to audio-only channels.
    /// </summary>
    public Option<DateTimeOffset> TruncateAt { get; init; } = Option<DateTimeOffset>.None;

    /// <summary>
    ///     When set, this transcode is bounded to the overlay's duration and the overlay
    ///     audio is mixed over the scheduled content at reduced bed volume (duck-style
    ///     interrupt). Audio-only channels only.
    /// </summary>
    public Option<DuckOverlay> MaybeDuckOverlay { get; init; } = Option<DuckOverlay>.None;
}
