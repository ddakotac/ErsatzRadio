using System.Globalization;
using CliWrap;
using CliWrap.Buffered;
using ErsatzTV.Application.Channels;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Interfaces.Streaming;
using ErsatzTV.Core.Interrupts;
using LanguageExt;
using static LanguageExt.Prelude;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace ErsatzTV.Controllers;

// injection api for the interrupt queue. intended for automation systems
// (e.g. home assistant + wyoming tts) to push near-live audio into a radio channel:
//   POST   /api/channels/{channelNumber}/interrupts        (multipart: file, [priority], [ttlSeconds], [title])
//   POST   /api/channels/{channelNumber}/interrupts/path   (json: { path, [priority], [ttlSeconds], [title], [deleteWhenDone] })
//   GET    /api/channels/{channelNumber}/interrupts
//   DELETE /api/channels/{channelNumber}/interrupts/{id}
//   DELETE /api/channels/{channelNumber}/interrupts
//
// priority 0 = emergency: cuts the currently playing item immediately.
// priority >= 1 waits for the next item boundary. FIFO within a priority.
// airAt (iso 8601, optional) = scheduled: held until that stream/wall time; the session
// worker truncates the preceding item so playback starts exactly on time when the item
// is enqueued before the transcode covering the air time begins (rule of thumb: enqueue
// at least a few minutes early).
// items that have not started playing by ((airAt ?? enqueuedAt) + ttlSeconds) are dropped.
// style=replace (default) inserts the audio between/over scheduled content; style=duck mixes
// the audio OVER scheduled content, which continues underneath at duckPercent volume
// (default 30). duck requires a transcoding (non-copy) audio profile.
[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
[Route("api/channels/{channelNumber}/interrupts")]
public class InterruptsController : ControllerBase
{
    private const int DefaultTtlSeconds = 300;

    private readonly IMediator _mediator;
    private readonly IConfigElementRepository _configElementRepository;
    private readonly IChannelInterruptService _interruptQueue;
    private readonly IFFmpegSegmenterService _ffmpegSegmenterService;
    private readonly ILogger<InterruptsController> _logger;

    public InterruptsController(
        IMediator mediator,
        IConfigElementRepository configElementRepository,
        IChannelInterruptService interruptQueue,
        IFFmpegSegmenterService ffmpegSegmenterService,
        ILogger<InterruptsController> logger)
    {
        _mediator = mediator;
        _configElementRepository = configElementRepository;
        _interruptQueue = interruptQueue;
        _ffmpegSegmenterService = ffmpegSegmenterService;
        _logger = logger;
    }

    [HttpPost]
    [RequestSizeLimit(104_857_600)] // 100 MB
    public async Task<IActionResult> Upload(
        string channelNumber,
        [FromForm] IFormFile file,
        [FromForm] int priority = 1,
        [FromForm] int ttlSeconds = DefaultTtlSeconds,
        [FromForm] string title = null,
        [FromForm] string airAt = null,
        [FromForm] string style = null,
        [FromForm] int duckPercent = 30,
        CancellationToken cancellationToken = default)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new { error = "an audio file is required" });
        }

        Either<IActionResult, Option<DateTimeOffset>> parsedAirAt = ParseAirAt(airAt, ttlSeconds);
        foreach (IActionResult invalid in parsedAirAt.LeftAsEnumerable())
        {
            return invalid;
        }

        Either<IActionResult, InterruptStyle> parsedStyle = ParseStyle(style, duckPercent);
        foreach (IActionResult invalid in parsedStyle.LeftAsEnumerable())
        {
            return invalid;
        }

        Option<IActionResult> maybeInvalid = await ValidateChannel(channelNumber, priority, ttlSeconds, cancellationToken);
        foreach (IActionResult invalid in maybeInvalid)
        {
            return invalid;
        }

        string extension = Path.GetExtension(file.FileName);
        if (string.IsNullOrWhiteSpace(extension) || extension.Length > 8)
        {
            extension = ".audio";
        }

        var id = Guid.NewGuid();
        string localPath = Path.Combine(FileSystemLayout.InterruptsFolder, $"{id}{extension}");

        await using (FileStream fs = System.IO.File.Create(localPath))
        {
            await file.CopyToAsync(fs, cancellationToken);
        }

        Either<IActionResult, TimeSpan> probeResult = await ProbeDuration(localPath, cancellationToken);
        foreach (IActionResult invalid in probeResult.LeftAsEnumerable())
        {
            TryDelete(localPath);
            return invalid;
        }

        foreach (TimeSpan duration in probeResult.RightAsEnumerable())
        foreach (Option<DateTimeOffset> airAtValue in parsedAirAt.RightAsEnumerable())
        foreach (InterruptStyle styleValue in parsedStyle.RightAsEnumerable())
        {
            InterruptQueueItem item = Enqueue(
                id,
                channelNumber,
                localPath,
                title ?? file.FileName,
                priority,
                ttlSeconds,
                duration,
                deleteFileWhenDone: true,
                airAtValue.ToNullable(),
                styleValue,
                duckPercent);

            return Ok(ToResponse(item));
        }

        return StatusCode(500);
    }

    [HttpPost("path")]
    public async Task<IActionResult> EnqueuePath(
        string channelNumber,
        [FromBody] InterruptPathRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request?.Path))
        {
            return BadRequest(new { error = "path is required" });
        }

        if (!System.IO.File.Exists(request.Path))
        {
            return BadRequest(new { error = $"file does not exist: {request.Path}" });
        }

        int priority = request.Priority ?? 1;
        int ttlSeconds = request.TtlSeconds ?? DefaultTtlSeconds;

        Either<IActionResult, Option<DateTimeOffset>> parsedAirAt = ParseAirAt(request.AirAt, ttlSeconds);
        foreach (IActionResult invalid in parsedAirAt.LeftAsEnumerable())
        {
            return invalid;
        }

        int duckPercent = request.DuckPercent ?? 30;
        Either<IActionResult, InterruptStyle> parsedStyle = ParseStyle(request.Style, duckPercent);
        foreach (IActionResult invalid in parsedStyle.LeftAsEnumerable())
        {
            return invalid;
        }

        Option<IActionResult> maybeInvalid = await ValidateChannel(channelNumber, priority, ttlSeconds, cancellationToken);
        foreach (IActionResult invalid in maybeInvalid)
        {
            return invalid;
        }

        Either<IActionResult, TimeSpan> probeResult = await ProbeDuration(request.Path, cancellationToken);
        foreach (IActionResult invalid in probeResult.LeftAsEnumerable())
        {
            return invalid;
        }

        foreach (TimeSpan duration in probeResult.RightAsEnumerable())
        foreach (Option<DateTimeOffset> airAtValue in parsedAirAt.RightAsEnumerable())
        foreach (InterruptStyle styleValue in parsedStyle.RightAsEnumerable())
        {
            InterruptQueueItem item = Enqueue(
                Guid.NewGuid(),
                channelNumber,
                request.Path,
                request.Title ?? Path.GetFileName(request.Path),
                priority,
                ttlSeconds,
                duration,
                request.DeleteWhenDone ?? false,
                airAtValue.ToNullable(),
                styleValue,
                duckPercent);

            return Ok(ToResponse(item));
        }

        return StatusCode(500);
    }

    [HttpGet]
    public IActionResult List(string channelNumber) =>
        Ok(_interruptQueue.List(channelNumber).Map(ToResponse));

    [HttpDelete("{id:guid}")]
    public IActionResult Delete(string channelNumber, Guid id) =>
        _interruptQueue.Remove(channelNumber, id)
            ? Ok(new { removed = id })
            : NotFound();

    [HttpDelete]
    public IActionResult Clear(string channelNumber) =>
        Ok(new { removed = _interruptQueue.Clear(channelNumber) });

    private InterruptQueueItem Enqueue(
        Guid id,
        string channelNumber,
        string path,
        string title,
        int priority,
        int ttlSeconds,
        TimeSpan duration,
        bool deleteFileWhenDone,
        DateTimeOffset? airAt = null,
        InterruptStyle style = InterruptStyle.Replace,
        int duckPercent = 30)
    {
        DateTimeOffset now = DateTimeOffset.Now;

        var item = new InterruptQueueItem
        {
            Id = id,
            ChannelNumber = channelNumber,
            Path = path,
            Title = title,
            Priority = priority,
            EnqueuedAt = now,
            AirAt = airAt,
            ExpiresAt = (airAt ?? now).AddSeconds(ttlSeconds),
            Duration = duration,
            DeleteFileWhenDone = deleteFileWhenDone,
            Style = style,
            DuckBedVolume = Math.Clamp(duckPercent, 0, 100) / 100.0
        };

        _interruptQueue.Enqueue(item);

        return item;
    }

    private async Task<Option<IActionResult>> ValidateChannel(
        string channelNumber,
        int priority,
        int ttlSeconds,
        CancellationToken cancellationToken)
    {
        if (priority < 0)
        {
            return BadRequest(new { error = "priority must be >= 0" });
        }

        if (ttlSeconds <= 0)
        {
            return BadRequest(new { error = "ttlSeconds must be > 0" });
        }

        Option<ChannelViewModel> maybeChannel = await _mediator.Send(
            new GetChannelByNumber(channelNumber),
            cancellationToken);

        foreach (ChannelViewModel channel in maybeChannel)
        {
            if (channel.SongVideoMode is not ChannelSongVideoMode.AudioOnly)
            {
                return BadRequest(
                    new { error = $"channel {channelNumber} is not an audio-only channel; interrupt injection requires audio-only channels" });
            }

            return Option<IActionResult>.None;
        }

        return NotFound(new { error = $"channel {channelNumber} does not exist" });
    }

    private async Task<Either<IActionResult, TimeSpan>> ProbeDuration(
        string path,
        CancellationToken cancellationToken)
    {
        Option<string> maybeFFprobePath = await _configElementRepository.GetValue<string>(
            ConfigElementKey.FFprobePath,
            cancellationToken);

        foreach (string ffprobePath in maybeFFprobePath)
        {
            try
            {
                BufferedCommandResult result = await Cli.Wrap(ffprobePath)
                    .WithArguments(
                    [
                        "-v", "error",
                        "-show_entries", "format=duration",
                        "-of", "default=noprint_wrappers=1:nokey=1",
                        path
                    ])
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteBufferedAsync(cancellationToken);

                if (result.ExitCode == 0 &&
                    double.TryParse(
                        result.StandardOutput.Trim(),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out double seconds) &&
                    seconds > 0)
                {
                    return TimeSpan.FromSeconds(seconds);
                }

                _logger.LogWarning(
                    "ffprobe could not determine duration for interrupt file {Path}: {Error}",
                    path,
                    result.StandardError);

                return BadRequest(new { error = "unable to determine audio duration; is this a valid audio file?" });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to probe interrupt file {Path}", path);
                return BadRequest(new { error = "unable to probe audio file" });
            }
        }

        return StatusCode(500, new { error = "ffprobe path is not configured" });
    }

    private Either<IActionResult, Option<DateTimeOffset>> ParseAirAt(string airAt, int ttlSeconds)
    {
        if (string.IsNullOrWhiteSpace(airAt))
        {
            return Option<DateTimeOffset>.None;
        }

        if (!DateTimeOffset.TryParse(airAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTimeOffset parsed))
        {
            return Left<IActionResult, Option<DateTimeOffset>>(
                BadRequest(new { error = "airAt must be a valid iso 8601 timestamp" }));
        }

        if (parsed.AddSeconds(ttlSeconds) <= DateTimeOffset.Now)
        {
            return Left<IActionResult, Option<DateTimeOffset>>(
                BadRequest(new { error = "airAt + ttlSeconds is already in the past" }));
        }

        return Some(parsed);
    }

    private static Either<IActionResult, InterruptStyle> ParseStyle(string style, int duckPercent)
    {
        if (duckPercent is < 0 or > 100)
        {
            return Left<IActionResult, InterruptStyle>(
                new BadRequestObjectResult(new { error = "duckPercent must be between 0 and 100" }));
        }

        if (string.IsNullOrWhiteSpace(style) ||
            string.Equals(style, "replace", StringComparison.OrdinalIgnoreCase))
        {
            return InterruptStyle.Replace;
        }

        if (string.Equals(style, "duck", StringComparison.OrdinalIgnoreCase))
        {
            return InterruptStyle.Duck;
        }

        return Left<IActionResult, InterruptStyle>(
            new BadRequestObjectResult(new { error = "style must be 'replace' or 'duck'" }));
    }

    private object ToResponse(InterruptQueueItem item)
    {
        bool sessionActive = _ffmpegSegmenterService.IsActive(item.ChannelNumber);

        return new
        {
            id = item.Id,
            channelNumber = item.ChannelNumber,
            title = item.Title,
            priority = item.Priority,
            durationSeconds = Math.Round(item.Duration.TotalSeconds, 2),
            enqueuedAt = item.EnqueuedAt,
            airAt = item.AirAt,
            expiresAt = item.ExpiresAt,
            style = item.Style.ToString().ToLowerInvariant(),
            duckPercent = (int)Math.Round(item.DuckBedVolume * 100),
            sessionActive,
            warning = sessionActive
                ? null
                : "no active session on this channel - nobody is listening; the item will expire unless a session starts before its ttl"
        };
    }

    private void TryDelete(string path)
    {
        try
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete interrupt temp file {Path}", path);
        }
    }

    public record InterruptPathRequest(
        string Path,
        int? Priority,
        int? TtlSeconds,
        string Title,
        bool? DeleteWhenDone,
        string AirAt,
        string Style,
        int? DuckPercent);
}
