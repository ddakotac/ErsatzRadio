using ErsatzTV.Core.Domain;
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
}
