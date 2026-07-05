using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interrupts;

namespace ErsatzTV.Application.Streaming;

public record GetInterruptProcessByChannelNumber(
    string ChannelNumber,
    StreamingMode Mode,
    DateTimeOffset Now,
    bool HlsRealtime,
    DateTimeOffset ChannelStartTime,
    TimeSpan PtsOffset,
    InterruptQueueItem Item) : FFmpegProcessRequest(
    ChannelNumber,
    Mode,
    Now,
    StartAtZero: true,
    HlsRealtime,
    ChannelStartTime,
    PtsOffset,
    Option<int>.None);
