using System.Globalization;
using CliWrap;
using CliWrap.Buffered;
using ErsatzTV.Application.Channels;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Core.Interfaces.Tts;
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
    private readonly ITtsSynthesisService _ttsSynthesisService;
    private readonly ILogger<InterruptsController> _logger;

    public InterruptsController(
        IMediator mediator,
        IConfigElementRepository configElementRepository,
        IChannelInterruptService interruptQueue,
        IFFmpegSegmenterService ffmpegSegmenterService,
        ITtsSynthesisService ttsSynthesisService,
        ILogger<InterruptsController> logger)
    {
        _mediator = mediator;
        _configElementRepository = configElementRepository;
        _interruptQueue = interruptQueue;
        _ffmpegSegmenterService = ffmpegSegmenterService;
        _ttsSynthesisService = ttsSynthesisService;
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

    // synthesize text through the tts endpoint registry and enqueue it as an interrupt.
    // style defaults to DUCK for tts (spoken over the schedule); pass style=replace to
    // insert instead. body: { text, ttsEndpoint?, voice?, priority?, ttlSeconds?, title?,
    // airAt?, style?, duckPercent? }
    [HttpPost("api/channels/{channelNumber}/interrupts/tts")]
    public async Task<IActionResult> EnqueueTts(
        string channelNumber,
        [FromBody] InterruptTtsRequest request,
        CancellationToken cancellationToken)
    {
        List<object> results = await EnqueueTtsForChannels([channelNumber], request, cancellationToken);
        return results.Count == 1 && results[0] is IActionResult single ? single : Ok(results[0]);
    }

    // broadcast tts to multiple channels. channels: ["1","2"] or "active" (all audio-only
    // channels with a live session). one synthesis, one file copy per channel.
    [HttpPost("api/interrupts/tts")]
    public async Task<IActionResult> BroadcastTts(
        [FromBody] InterruptTtsBroadcastRequest request,
        CancellationToken cancellationToken)
    {
        Either<IActionResult, List<string>> maybeChannels =
            await ResolveChannels(request?.Channels, cancellationToken);

        foreach (IActionResult invalid in maybeChannels.LeftAsEnumerable())
        {
            return invalid;
        }

        foreach (List<string> channels in maybeChannels.RightAsEnumerable())
        {
            List<object> results = await EnqueueTtsForChannels(channels, request, cancellationToken);
            return Ok(new { results });
        }

        return BadRequest();
    }

    // broadcast a path-based interrupt to multiple channels. deleteWhenDone is ignored
    // (multiple queue items share the file).
    [HttpPost("api/interrupts/path")]
    public async Task<IActionResult> BroadcastPath(
        [FromBody] InterruptPathBroadcastRequest request,
        CancellationToken cancellationToken)
    {
        Either<IActionResult, List<string>> maybeChannels =
            await ResolveChannels(request?.Channels, cancellationToken);

        foreach (IActionResult invalid in maybeChannels.LeftAsEnumerable())
        {
            return invalid;
        }

        foreach (List<string> channels in maybeChannels.RightAsEnumerable())
        {
            var results = new List<object>();
            foreach (string channelNumber in channels)
            {
                var singleRequest = new InterruptPathRequest(
                    request.Path,
                    request.Priority,
                    request.TtlSeconds,
                    request.Title,
                    DeleteWhenDone: false,
                    request.AirAt,
                    request.Style,
                    request.DuckPercent);

                IActionResult result = await EnqueuePath(channelNumber, singleRequest, cancellationToken);
                results.Add(ToBroadcastResult(channelNumber, result));
            }

            return Ok(new { results });
        }

        return BadRequest();
    }

    private async Task<List<object>> EnqueueTtsForChannels(
        List<string> channelNumbers,
        InterruptTtsRequest request,
        CancellationToken cancellationToken)
    {
        var results = new List<object>();

        if (string.IsNullOrWhiteSpace(request?.Text))
        {
            results.Add(BadRequest(new { error = "text is required" }));
            return results;
        }

        int priority = request.Priority ?? 1;
        int ttlSeconds = request.TtlSeconds ?? DefaultTtlSeconds;

        Either<IActionResult, Option<DateTimeOffset>> parsedAirAt = ParseAirAt(request.AirAt, ttlSeconds);
        foreach (IActionResult invalid in parsedAirAt.LeftAsEnumerable())
        {
            results.Add(invalid);
            return results;
        }

        // tts interrupts default to duck: spoken over the schedule
        int duckPercent = request.DuckPercent ?? 30;
        Either<IActionResult, InterruptStyle> parsedStyle = ParseStyle(request.Style ?? "duck", duckPercent);
        foreach (IActionResult invalid in parsedStyle.LeftAsEnumerable())
        {
            results.Add(invalid);
            return results;
        }

        Option<string> maybeAudioPath = await _ttsSynthesisService.SynthesizeToFile(
            request.Text,
            request.TtsEndpoint,
            request.Voice,
            cancellationToken);

        if (maybeAudioPath.IsNone)
        {
            results.Add(StatusCode(
                502,
                new { error = "tts synthesis failed; check the tts endpoint configuration and logs" }));

            return results;
        }

        foreach (string audioPath in maybeAudioPath)
        foreach (Option<DateTimeOffset> airAtValue in parsedAirAt.RightAsEnumerable())
        foreach (InterruptStyle styleValue in parsedStyle.RightAsEnumerable())
        {
            Either<IActionResult, TimeSpan> probeResult = await ProbeDuration(audioPath, cancellationToken);
            foreach (IActionResult probeError in probeResult.LeftAsEnumerable())
            {
                TryDeleteFile(audioPath);
                results.Add(probeError);
                return results;
            }

            foreach (TimeSpan duration in probeResult.RightAsEnumerable())
            {
                var first = true;
                foreach (string channelNumber in channelNumbers)
                {
                    Option<IActionResult> maybeInvalid =
                        await ValidateChannel(channelNumber, priority, ttlSeconds, cancellationToken);

                    bool valid = maybeInvalid.IsNone;
                    foreach (IActionResult invalid in maybeInvalid)
                    {
                        results.Add(ToBroadcastResult(channelNumber, invalid));
                    }

                    if (!valid)
                    {
                        continue;
                    }

                    // each queue item owns its own file copy - DeleteFileWhenDone would
                    // otherwise race between channels
                    string channelPath = first
                        ? audioPath
                        : CopyForChannel(audioPath);
                    first = false;

                    InterruptQueueItem item = Enqueue(
                        Guid.NewGuid(),
                        channelNumber,
                        channelPath,
                        request.Title ?? $"TTS: {Truncate(request.Text, 60)}",
                        priority,
                        ttlSeconds,
                        duration,
                        deleteFileWhenDone: true,
                        airAtValue.ToNullable(),
                        styleValue,
                        duckPercent);

                    results.Add(ToBroadcastResult(channelNumber, Ok(ToResponse(item))));
                }

                // nothing enqueued (all channels invalid): remove the orphan synthesis file
                if (first)
                {
                    TryDeleteFile(audioPath);
                }
            }
        }

        return results;
    }

    private async Task<Either<IActionResult, List<string>>> ResolveChannels(
        object channels,
        CancellationToken cancellationToken)
    {
        if (channels is System.Text.Json.JsonElement element)
        {
            if (element.ValueKind == System.Text.Json.JsonValueKind.String &&
                string.Equals(element.GetString(), "active", StringComparison.OrdinalIgnoreCase))
            {
                List<ChannelViewModel> allChannels = await _mediator.Send(new GetAllChannels(), cancellationToken);
                var active = allChannels
                    .Filter(ch => ch.SongVideoMode == ChannelSongVideoMode.AudioOnly)
                    .Filter(ch => _ffmpegSegmenterService.IsActive(ch.Number))
                    .Map(ch => ch.Number)
                    .ToList();

                if (active.Count == 0)
                {
                    return Left<IActionResult, List<string>>(
                        UnprocessableEntity(new { error = "no audio-only channels have active sessions" }));
                }

                return active;
            }

            if (element.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var list = element.EnumerateArray()
                    .Filter(e => e.ValueKind == System.Text.Json.JsonValueKind.String)
                    .Map(e => e.GetString())
                    .Filter(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct()
                    .ToList();

                if (list.Count > 0)
                {
                    return list;
                }
            }
        }

        return Left<IActionResult, List<string>>(
            BadRequest(new { error = "channels must be a non-empty array of channel numbers or the string \"active\"" }));
    }

    private static string CopyForChannel(string sourcePath)
    {
        string copyPath = Path.Combine(
            FileSystemLayout.InterruptsFolder,
            $"tts-{Guid.NewGuid()}{Path.GetExtension(sourcePath)}");

        System.IO.File.Copy(sourcePath, copyPath);
        return copyPath;
    }

    private void TryDeleteFile(string path)
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
            _logger.LogWarning(ex, "Failed to delete tts temp file {Path}", path);
        }
    }

    private static object ToBroadcastResult(string channelNumber, IActionResult result) =>
        result switch
        {
            OkObjectResult ok => new { channelNumber, ok = true, item = ok.Value },
            ObjectResult obj => new { channelNumber, ok = false, error = obj.Value },
            _ => new { channelNumber, ok = false, error = "unknown error" }
        };

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "...";

    public record InterruptTtsRequest(
        string Text,
        string TtsEndpoint,
        string Voice,
        int? Priority,
        int? TtlSeconds,
        string Title,
        string AirAt,
        string Style,
        int? DuckPercent);

    public record InterruptTtsBroadcastRequest(
        string Text,
        string TtsEndpoint,
        string Voice,
        int? Priority,
        int? TtlSeconds,
        string Title,
        string AirAt,
        string Style,
        int? DuckPercent,
        object Channels) : InterruptTtsRequest(
        Text,
        TtsEndpoint,
        Voice,
        Priority,
        TtlSeconds,
        Title,
        AirAt,
        Style,
        DuckPercent);

    public record InterruptPathBroadcastRequest(
        string Path,
        int? Priority,
        int? TtlSeconds,
        string Title,
        string AirAt,
        string Style,
        int? DuckPercent,
        object Channels);

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
