using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.FFmpeg;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Core.Interrupts;
using ErsatzTV.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Application.Streaming;

public class GetInterruptProcessByChannelNumberHandler : FFmpegProcessHandler<GetInterruptProcessByChannelNumber>
{
    private readonly IFFmpegProcessService _ffmpegProcessService;
    private readonly ILogger<GetInterruptProcessByChannelNumberHandler> _logger;

    public GetInterruptProcessByChannelNumberHandler(
        IDbContextFactory<TvContext> dbContextFactory,
        IFFmpegProcessService ffmpegProcessService,
        ILogger<GetInterruptProcessByChannelNumberHandler> logger)
        : base(dbContextFactory)
    {
        _ffmpegProcessService = ffmpegProcessService;
        _logger = logger;
    }

    protected override async Task<Either<BaseError, PlayoutItemProcessModel>> GetProcess(
        TvContext dbContext,
        GetInterruptProcessByChannelNumber request,
        Channel channel,
        string ffmpegPath,
        string ffprobePath,
        CancellationToken cancellationToken)
    {
        if (channel.SongVideoMode is not ChannelSongVideoMode.AudioOnly)
        {
            return BaseError.New(
                $"Interrupt injection requires an audio-only channel; channel {channel.Number} is not audio-only");
        }

        if (!File.Exists(request.Item.Path))
        {
            return BaseError.New($"Interrupt audio file does not exist: {request.Item.Path}");
        }

        DateTimeOffset start = request.Now;
        DateTimeOffset finish = start + request.Item.Duration;

        _logger.LogInformation(
            "Injecting interrupt {Title} ({Id}) on channel {Channel} from {Start} to {Finish}",
            request.Item.Title,
            request.Item.Id,
            channel.Number,
            start,
            finish);

        PlayoutItemResult playoutItemResult = await _ffmpegProcessService.ForAudioOnlyPlayoutItem(
            ffmpegPath,
            channel,
            new MediaItemAudioVersion(null, null),
            request.Item.Path,
            start,
            finish,
            start,
            TimeSpan.Zero,
            request.PtsOffset,
            request.HlsRealtime,
            isLiveInput: false,
            Option<DuckOverlay>.None,
            cancellationToken);

        var result = new PlayoutItemProcessModel(
            playoutItemResult.Process,
            playoutItemResult.GraphicsEngineContext,
            request.Item.Duration,
            finish,
            isComplete: true,
            start.ToUnixTimeSeconds(),
            Option<int>.None,
            Optional(channel.PlayoutOffset),
            !request.HlsRealtime);

        return Right<BaseError, PlayoutItemProcessModel>(result);
    }
}
