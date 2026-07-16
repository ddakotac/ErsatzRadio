using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Abstractions;
using System.IO.Pipelines;
using System.Text;
using System.Timers;
using CliWrap;
using CliWrap.Buffered;
using ErsatzTV.Application.Channels;
using ErsatzTV.Application.Playouts;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.FFmpeg;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Core.Interfaces.Metadata;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Interfaces.Streaming;
using ErsatzTV.Core.Interrupts;
using ErsatzTV.FFmpeg;
using ErsatzTV.FFmpeg.OutputFormat;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Timer = System.Timers.Timer;

namespace ErsatzTV.Application.Streaming;

public class HlsSessionWorker : IHlsSessionWorker
{
    private static int _workAheadCount;
    private readonly OutputFormatKind _outputFormatKind;
    private readonly IHlsInitSegmentCache _hlsInitSegmentCache;
    private readonly Dictionary<long, int> _discontinuityMap = [];
    private readonly IConfigElementRepository _configElementRepository;
    private readonly IFileSystem _fileSystem;
    private readonly IGraphicsEngine _graphicsEngine;
    private readonly IHlsPlaylistFilter _hlsPlaylistFilter;
    private readonly ILocalFileSystem _localFileSystem;
    private readonly ILogger<HlsSessionWorker> _logger;
    private readonly IMediator _mediator;
    private readonly SemaphoreSlim _slim = new(1, 1);
    private readonly Lock _sync = new();
    private readonly Option<FrameRate> _targetFramerate;
    private readonly IChannelInterruptService _interruptQueue;
    private readonly IChannelAnnouncerService _announcerService;
    private CancellationTokenSource _cancellationTokenSource;
    private CancellationTokenSource _interruptCts;
    private InterruptQueueItem _pendingDuck;
    private InterruptQueueItem _duckToCleanUp;
    private string _channelNumber;
    private DateTimeOffset _channelStart;
    private int _discontinuitySequence;
    private bool _disposedValue;
    private bool _hasWrittenSegments;
    private DateTimeOffset _lastAccess;
    private DateTimeOffset _lastDelete = DateTimeOffset.MinValue;
    private IServiceScope _serviceScope;
    private HlsSessionState _state;
    private Timer _timer;
    private DateTimeOffset _transcodedUntil;
    private string _workingDirectory;
    private Option<double> _slugSeconds;

    public HlsSessionWorker(
        IServiceScopeFactory serviceScopeFactory,
        IGraphicsEngine graphicsEngine,
        OutputFormatKind outputFormatKind,
        IHlsPlaylistFilter hlsPlaylistFilter,
        IHlsInitSegmentCache hlsInitSegmentCache,
        IConfigElementRepository configElementRepository,
        IFileSystem fileSystem,
        ILocalFileSystem localFileSystem,
        ILogger<HlsSessionWorker> logger,
        Option<FrameRate> targetFramerate)
    {
        _serviceScope = serviceScopeFactory.CreateScope();
        _mediator = _serviceScope.ServiceProvider.GetRequiredService<IMediator>();
        _interruptQueue = _serviceScope.ServiceProvider.GetRequiredService<IChannelInterruptService>();
        _announcerService = _serviceScope.ServiceProvider.GetRequiredService<IChannelAnnouncerService>();
        _graphicsEngine = graphicsEngine;
        _outputFormatKind = outputFormatKind;
        _hlsInitSegmentCache = hlsInitSegmentCache;
        _hlsPlaylistFilter = hlsPlaylistFilter;
        _configElementRepository = configElementRepository;
        _fileSystem = fileSystem;
        _localFileSystem = localFileSystem;
        _logger = logger;
        _targetFramerate = targetFramerate;
    }

    public DateTimeOffset PlaylistStart { get; private set; }

    public async Task Cancel(CancellationToken cancellationToken)
    {
        _logger.LogInformation("API termination request for HLS session for channel {Channel}", _channelNumber);

        await _slim.WaitAsync(cancellationToken);
        try
        {
            await _cancellationTokenSource.CancelAsync();
        }
        finally
        {
            _slim.Release();
        }
    }

    public void Touch(Option<string> fileName)
    {
        lock (_sync)
        {
            // _logger.LogDebug("Keep alive - session worker for channel {ChannelNumber}", _channelNumber);

            _lastAccess = DateTimeOffset.Now;

            _timer?.Stop();
            _timer?.Start();
        }
    }

    public async Task<Option<TrimPlaylistResult>> TrimPlaylist(
        DateTimeOffset filterBefore,
        CancellationToken cancellationToken)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            await _slim.WaitAsync(cancellationToken);
            try
            {
                Option<string[]> maybeLines = await ReadPlaylistLines(cancellationToken);
                foreach (string[] input in maybeLines)
                {
                    await RefreshInits();

                    TrimPlaylistResult trimResult = _hlsPlaylistFilter.TrimPlaylist(
                        _discontinuityMap,
                        _outputFormatKind,
                        PlaylistStart,
                        filterBefore,
                        _hlsInitSegmentCache,
                        input,
                        maybeMaxSegments: 10);
                    if (DateTimeOffset.Now > _lastDelete.AddSeconds(30))
                    {
                        DeleteOldSegments(trimResult);
                        _lastDelete = DateTimeOffset.Now;
                    }

                    return trimResult;
                }

                _logger.LogWarning("HlsSessionWorker.TrimPlaylist read empty playlist?");
            }
            finally
            {
                _slim.Release();
                sw.Stop();
                // _logger.LogDebug("TrimPlaylist took {Duration}", sw.Elapsed);
            }
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
        {
            // do nothing
            _logger.LogDebug("HlsSessionWorker.TrimPlaylist was canceled");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error trimming playlist");
        }

        return None;
    }

    public void PlayoutUpdated() => _state = HlsSessionState.PlayoutUpdated;

    public HlsSessionModel GetModel() => new(_channelNumber, _state.ToString(), _transcodedUntil, _lastAccess);

    void IDisposable.Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public async Task Run(
        string channelNumber,
        Option<TimeSpan> idleTimeout,
        CancellationToken incomingCancellationToken)
    {
        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(incomingCancellationToken);

        try
        {
            _channelNumber = channelNumber;
            _workingDirectory = Path.Combine(FileSystemLayout.TranscodeFolder, _channelNumber);

            foreach (TimeSpan timeout in idleTimeout)
            {
                lock (_sync)
                {
                    _timer = new Timer(timeout.TotalMilliseconds) { AutoReset = false };
                    _timer.Elapsed += CancelRun;
                }
            }

            CancellationToken cancellationToken = _cancellationTokenSource.Token;

            // allow priority 0 (emergency) interrupt items to cut the current scheduled transcode
            using IDisposable interruptRegistration = _interruptQueue.RegisterForceInterruptHandler(
                channelNumber,
                ForceInterrupt);

            _logger.LogInformation("Starting HLS session for channel {Channel}", channelNumber);

            if (_localFileSystem.ListFiles(_workingDirectory).Any())
            {
                _logger.LogError("Transcode folder is NOT empty!");
            }

            Touch(Option<string>.None);
            _transcodedUntil = DateTimeOffset.Now;
            PlaylistStart = _transcodedUntil;
            _channelStart = _transcodedUntil;

            Option<int> maybePlayoutId = await _mediator.Send(
                new GetPlayoutIdByChannelNumber(_channelNumber),
                cancellationToken);

            _slugSeconds = await _mediator.Send(
                new GetSlugSecondsByChannelNumber(_channelNumber),
                cancellationToken);

            // time shift on-demand playout if needed
            foreach (int playoutId in maybePlayoutId)
            {
                await _mediator.Send(
                    new TimeShiftOnDemandPlayout(playoutId, _transcodedUntil, true),
                    cancellationToken);
            }

            bool initialWorkAhead = Volatile.Read(ref _workAheadCount) < await GetWorkAheadLimit(cancellationToken);
            _state = initialWorkAhead ? HlsSessionState.SeekAndWorkAhead : HlsSessionState.SeekAndRealtime;

            if (!await Transcode(!initialWorkAhead, cancellationToken))
            {
                return;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                foreach (TimeSpan timeout in idleTimeout)
                {
                    if (DateTimeOffset.Now - _lastAccess > timeout)
                    {
                        _logger.LogInformation("Stopping idle HLS session for channel {Channel}", channelNumber);
                        return;
                    }
                }

                var transcodedBuffer = TimeSpan.FromSeconds(
                    Math.Max(0, _transcodedUntil.Subtract(DateTimeOffset.Now).TotalSeconds));

                // clean up the previous duck overlay's temp file, if any
                if (_duckToCleanUp is not null)
                {
                    InterruptWebhook.Fire(_duckToCleanUp.WebhookUrl, "completed", _duckToCleanUp, _logger);
                    _interruptQueue.CleanUpFile(_duckToCleanUp);
                    _duckToCleanUp = null;
                }

                // announce the upcoming item (enqueues a duck/replace interrupt when the
                // announcer is enabled and a new item starts at the current stream time)
                await _announcerService.AnnounceUpcomingItem(_channelNumber, _transcodedUntil, cancellationToken);

                // play any pending interrupt items before scheduled content
                bool playedInterrupt = false;
                if (_pendingDuck is null)
                {
                    foreach (InterruptQueueItem interruptItem in _interruptQueue.TryDequeue(_channelNumber, _transcodedUntil))
                    {
                        if (interruptItem.Style == InterruptStyle.Duck)
                        {
                            // consumed by the next scheduled transcode as a mixed overlay
                            _pendingDuck = interruptItem;
                        }
                        else
                        {
                            bool interruptRealtime = transcodedBuffer >= TimeSpan.FromSeconds(30);
                            if (!await TranscodeInterrupt(interruptItem, interruptRealtime, cancellationToken))
                            {
                                return;
                            }

                            playedInterrupt = true;
                        }
                    }
                }

                if (playedInterrupt)
                {
                    continue;
                }

                if (transcodedBuffer <= TimeSpan.FromMinutes(1))
                {
                    // only use realtime encoding when we're at least 30 seconds ahead
                    bool realtime = transcodedBuffer >= TimeSpan.FromSeconds(30);
                    bool subsequentWorkAhead =
                        !realtime && Volatile.Read(ref _workAheadCount) < await GetWorkAheadLimit(cancellationToken);
                    if (!await Transcode(!subsequentWorkAhead, cancellationToken))
                    {
                        return;
                    }
                }
                else
                {
                    await TrimAndDelete(cancellationToken);
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                }
            }
        }
        finally
        {
            foreach (InterruptQueueItem leftover in new[] { _pendingDuck, _duckToCleanUp })
            {
                if (leftover is not null)
                {
                    _interruptQueue.CleanUpFile(leftover);
                }
            }

            _pendingDuck = null;
            _duckToCleanUp = null;

            if (_timer is not null)
            {
                lock (_sync)
                {
                    _timer.Elapsed -= CancelRun;
                }
            }

            try
            {
                _localFileSystem.EmptyFolder(_workingDirectory);
            }
            catch
            {
                // do nothing
            }
        }

        return;

        [SuppressMessage("Usage", "VSTHRD100:Avoid async void methods")]
        async void CancelRun(object o, ElapsedEventArgs e)
        {
            try
            {
                await _cancellationTokenSource.CancelAsync();
            }
            catch (Exception)
            {
                // do nothing
            }
        }
    }

    public async Task WaitForPlaylistSegments(
        int initialSegmentCount,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Waiting for playlist segments...");

        var sw = Stopwatch.StartNew();
        try
        {
            string playlistFileName = Path.Combine(_workingDirectory, "live.m3u8");

            _logger.LogDebug("Waiting for playlist to exist");
            while (!_fileSystem.File.Exists(playlistFileName))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            }

            _logger.LogDebug("Playlist exists");

            // start the segment-wait deadline only after the playlist file appears,
            // so slow pipeline setup (e.g. h264 profile probing) doesn't consume the budget
            DateTimeOffset finish = DateTimeOffset.Now.AddSeconds(8);

            var segmentCount = 0;
            int lastSegmentCount = -1;
            while (DateTimeOffset.Now < finish && segmentCount < initialSegmentCount)
            {
                if (segmentCount != lastSegmentCount)
                {
                    lastSegmentCount = segmentCount;
                    _logger.LogDebug(
                        "Segment count {SegmentCount} of {InitialSegmentCount}",
                        segmentCount,
                        initialSegmentCount);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);

                DateTimeOffset now = DateTimeOffset.Now.AddSeconds(-30);
                Option<TrimPlaylistResult> maybeResult = await TrimPlaylist(now, cancellationToken);
                foreach (TrimPlaylistResult result in maybeResult)
                {
                    segmentCount = result.SegmentCount;
                }
            }
        }
        finally
        {
            sw.Stop();
            _logger.LogDebug("WaitForPlaylistSegments took {Duration}", sw.Elapsed);
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                if (_timer is not null)
                {
                    _timer.Dispose();
                    _timer = null;
                }

                _serviceScope.Dispose();
                _serviceScope = null;
            }

            _disposedValue = true;
        }
    }

    private HlsSessionState NextState(HlsSessionState state, PlayoutItemProcessModel processModel)
    {
        bool isComplete = processModel?.IsComplete == true;

        HlsSessionState result = state switch
        {
            // playout updates should have the channel start over, transcode method will throttle if needed
            HlsSessionState.PlayoutUpdated => HlsSessionState.SeekAndWorkAhead,

            // after seeking and NOT completing the item, seek again, transcode method will accelerate if needed
            HlsSessionState.SeekAndWorkAhead when !isComplete => HlsSessionState.SeekAndRealtime,

            // switch back to normal item after slug
            HlsSessionState.SlugAndWorkAhead => HlsSessionState.ZeroAndWorkAhead,
            HlsSessionState.SlugAndRealtime => HlsSessionState.ZeroAndRealtime,

            // after completing the item, insert a slug
            HlsSessionState.ZeroAndWorkAhead or HlsSessionState.SeekAndWorkAhead when isComplete && _slugSeconds.IsSome => HlsSessionState.SlugAndWorkAhead,
            HlsSessionState.ZeroAndRealtime or HlsSessionState.SeekAndRealtime when isComplete && _slugSeconds.IsSome => HlsSessionState.SlugAndRealtime,

            // after seeking and completing the item, start at zero
            HlsSessionState.SeekAndWorkAhead => HlsSessionState.ZeroAndWorkAhead,

            // after starting and zero and NOT completing the item, seek, transcode method will accelerate if needed
            HlsSessionState.ZeroAndWorkAhead when !isComplete => HlsSessionState.SeekAndRealtime,

            // after starting at zero and completing the item, start at zero again, transcode method will throttle if needed
            HlsSessionState.ZeroAndWorkAhead => HlsSessionState.ZeroAndWorkAhead,

            // duck overlays and scheduled-interrupt truncation produce INCOMPLETE realtime
            // transcodes; the next transcode must seek back into the item, not restart it
            HlsSessionState.SeekAndRealtime when !isComplete => HlsSessionState.SeekAndRealtime,
            HlsSessionState.ZeroAndRealtime when !isComplete => HlsSessionState.SeekAndRealtime,

            // realtime will always complete items, so start next at zero
            HlsSessionState.SeekAndRealtime => HlsSessionState.ZeroAndRealtime,

            // realtime will always complete items, so start next at zero
            HlsSessionState.ZeroAndRealtime => HlsSessionState.ZeroAndRealtime,

            // this will never happen with the enum
            _ => throw new InvalidOperationException()
        };

        _logger.LogDebug("HLS session state {Last} => {Next}", state, result);

        return result;
    }

    private async Task<bool> Transcode(bool realtime, CancellationToken cancellationToken)
    {
        try
        {
            bool wasSeekAndWorkAhead = _state is HlsSessionState.SeekAndWorkAhead;

            if (!realtime)
            {
                Interlocked.Increment(ref _workAheadCount);
                _logger.LogDebug("HLS segmenter will work ahead for channel {Channel}", _channelNumber);

                HlsSessionState nextState = _state switch
                {
                    HlsSessionState.SeekAndRealtime => HlsSessionState.SeekAndWorkAhead,
                    HlsSessionState.ZeroAndRealtime => HlsSessionState.ZeroAndWorkAhead,
                    _ => _state
                };

                if (nextState != _state)
                {
                    _logger.LogDebug("HLS session state accelerating {Last} => {Next}", _state, nextState);
                    _state = nextState;
                }
            }
            else
            {
                _logger.LogDebug(
                    "HLS segmenter will NOT work ahead for channel {Channel}",
                    _channelNumber);

                // throttle to realtime if needed
                HlsSessionState nextState = _state switch
                {
                    HlsSessionState.SeekAndWorkAhead => HlsSessionState.SeekAndRealtime,
                    HlsSessionState.ZeroAndWorkAhead => HlsSessionState.ZeroAndRealtime,
                    HlsSessionState.SlugAndWorkAhead => HlsSessionState.SlugAndRealtime,
                    _ => _state
                };

                if (nextState != _state)
                {
                    _logger.LogDebug("HLS session state throttling {Last} => {Next}", _state, nextState);
                    _state = nextState;
                }
            }

            TimeSpan ptsOffset = await GetPtsOffset(_channelNumber, cancellationToken);

            _logger.LogDebug("HLS session state: {State}", _state);

            DateTimeOffset now = wasSeekAndWorkAhead ? DateTimeOffset.Now : _transcodedUntil;
            bool startAtZero = _state is HlsSessionState.ZeroAndWorkAhead or HlsSessionState.ZeroAndRealtime
                or HlsSessionState.SlugAndWorkAhead or HlsSessionState.SlugAndRealtime;

            bool isSlug = _state is HlsSessionState.SlugAndWorkAhead or HlsSessionState.SlugAndRealtime;

            FFmpegProcessRequest request = isSlug
                ? new GetSlugProcessByChannelNumber(
                    _channelNumber,
                    StreamingMode.HttpLiveStreamingSegmenter,
                    now,
                    realtime,
                    _channelStart,
                    ptsOffset,
                    _targetFramerate,
                    _slugSeconds)
                : new GetPlayoutItemProcessByChannelNumber(
                    _channelNumber,
                    StreamingMode.HttpLiveStreamingSegmenter,
                    now,
                    startAtZero,
                    realtime,
                    _channelStart,
                    ptsOffset,
                    _targetFramerate,
                    IsTroubleshooting: false,
                    Option<int>.None)
                {
                    TruncateAt = _interruptQueue.PeekNextAirTime(_channelNumber, now),
                    MaybeDuckOverlay = _pendingDuck is not null
                        ? new DuckOverlay(_pendingDuck.Path, _pendingDuck.Duration, _pendingDuck.DuckBedVolume)
                        : Option<DuckOverlay>.None
                };

            // whatever happens to this transcode, the duck overlay is consumed by it
            InterruptQueueItem consumedDuck = _pendingDuck;
            _pendingDuck = null;

            if (consumedDuck is not null && isSlug)
            {
                _logger.LogWarning(
                    "Dropping duck interrupt {Id} on channel {Channel}; channel is in slug/offline state",
                    consumedDuck.Id,
                    _channelNumber);

                _interruptQueue.CleanUpFile(consumedDuck);
                consumedDuck = null;
            }
            else if (consumedDuck is not null)
            {
                _logger.LogInformation(
                    "Ducking interrupt {Title} ({Id}) over scheduled content on channel {Channel} at {Start} (bed volume {Volume})",
                    consumedDuck.Title,
                    consumedDuck.Id,
                    _channelNumber,
                    _transcodedUntil,
                    consumedDuck.DuckBedVolume);

                // temp file is deleted at the top of the next session loop iteration
                _duckToCleanUp = consumedDuck;

                InterruptWebhook.Fire(consumedDuck.WebhookUrl, "airing", consumedDuck, _logger);
            }

            // _logger.LogInformation("Request {@Request}", request);

            Either<BaseError, PlayoutItemProcessModel> result = await _mediator.Send(request, cancellationToken);

            // _logger.LogInformation("Result {Result}", result.ToString());

            foreach (BaseError error in result.LeftAsEnumerable())
            {
                _logger.LogWarning(
                    "Failed to create process for HLS session on channel {Channel}: {Error}",
                    _channelNumber,
                    error.ToString());

                return false;
            }

            foreach (PlayoutItemProcessModel processModel in result.RightAsEnumerable())
            {
                if (!realtime && !processModel.IsWorkingAhead)
                {
                    _logger.LogDebug("HLS session throttling (NOT working ahead) based on playout item");
                }

                await TrimAndDelete(cancellationToken);

                // increment discontinuity sequence and store with segment key (generated at)
                foreach (long segmentKey in processModel.SegmentKey)
                {
                    _discontinuitySequence++;
                    _discontinuityMap.TryAdd(segmentKey, _discontinuitySequence);
                    //_logger.LogDebug("DISCONTINUITY MAP {Map}", _discontinuityMap);
                }

                Option<Pipe> maybePipe = Option<Pipe>.None;
                var stdErrBuffer = new StringBuilder();

                Command process = processModel.Process;

                _logger.LogDebug("ffmpeg hls arguments {FFmpegArguments}", process.Arguments);

                using var interruptCts = new CancellationTokenSource();
                lock (_sync)
                {
                    _interruptCts = interruptCts;
                }

                try
                {
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken,
                        interruptCts.Token);

                    Command processWithPipe = process;
                    foreach (GraphicsEngineContext graphicsEngineContext in processModel.GraphicsEngineContext)
                    {
                        var pipe = new Pipe();
                        maybePipe = pipe;
                        processWithPipe = process.WithStandardInputPipe(PipeSource.FromStream(pipe.Reader.AsStream()));

                        // fire and forget graphics engine task
                        _ = _graphicsEngine.Run(
                            graphicsEngineContext,
                            pipe.Writer,
                            linkedCts.Token);
                    }

                    var progressParser = new FFmpegProgress();

                    CommandResult commandResult = await processWithPipe
                        .WithWorkingDirectory(_workingDirectory)
                        .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stdErrBuffer))
                        .WithStandardOutputPipe(PipeTarget.ToDelegate(progressParser.ParseLine))
                        .WithValidation(CommandResultValidation.None)
                        .ExecuteAsync(linkedCts.Token);

                    if (commandResult.ExitCode == 0)
                    {
                        _logger.LogDebug("HLS process has completed for channel {Channel}", _channelNumber);
                        _logger.LogDebug(
                            "Transcoded until: {Until} - Buffer: {Buffer} seconds - Speed {Speed}",
                            processModel.Until,
                            processModel.Until.Subtract(DateTimeOffset.Now).TotalSeconds,
                            progressParser.Speed);
                        _transcodedUntil = processModel.Until;
                        _state = NextState(_state, processModel);
                        _hasWrittenSegments = true;

                        progressParser.LogSpeed(
                            processModel.MediaItemId,
                            processModel.IsWorkingAhead,
                            _channelNumber,
                            _logger);

                        return true;
                    }
                    else
                    {
                        await linkedCts.CancelAsync();

                        // detect the non-zero exit code and transcode the ffmpeg error message instead
                        var errorMessage = stdErrBuffer.ToString();
                        if (string.IsNullOrWhiteSpace(errorMessage))
                        {
                            errorMessage = $"Unknown FFMPEG error; exit code {commandResult.ExitCode}";
                        }

                        _logger.LogError(
                            "HLS process for channel {Channel} has terminated unsuccessfully with exit code {ExitCode}: {StandardError}",
                            _channelNumber,
                            commandResult.ExitCode,
                            stdErrBuffer.ToString());

                        Either<BaseError, PlayoutItemProcessModel> maybeOfflineProcess = await _mediator.Send(
                            new GetErrorProcess(
                                _channelNumber,
                                StreamingMode.HttpLiveStreamingSegmenter,
                                realtime,
                                ptsOffset,
                                processModel.MaybeDuration,
                                processModel.Until,
                                errorMessage),
                            // ReSharper disable once PossiblyMistakenUseOfCancellationToken
                            cancellationToken);

                        foreach (PlayoutItemProcessModel errorProcessModel in maybeOfflineProcess.RightAsEnumerable())
                        {
                            Command errorProcess = errorProcessModel.Process;

                            _logger.LogDebug(
                                "ffmpeg hls error arguments {FFmpegArguments}",
                                errorProcess.Arguments);

                            commandResult = await errorProcess
                                .WithValidation(CommandResultValidation.None)
                                // ReSharper disable once PossiblyMistakenUseOfCancellationToken
                                .ExecuteBufferedAsync(Encoding.UTF8, cancellationToken);

                            if (commandResult.ExitCode == 0)
                            {
                                _transcodedUntil = processModel.Until;
                                _state = NextState(_state, null);

                                _hasWrittenSegments = true;

                                return true;
                            }
                        }

                        return false;
                    }
                }
                catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
                {
                    if (interruptCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    {
                        // an emergency (priority 0) interrupt intentionally cut this transcode;
                        // recover the actual transcoded position from the playlist so the interrupt
                        // item picks up exactly where the audio stopped

                        // the cut transcode may have written segments even if it never completed
                        // (relevant when the FIRST transcode of a session is cut)
                        if (!_hasWrittenSegments && _fileSystem.File.Exists(PlaylistFileName()))
                        {
                            _hasWrittenSegments = true;
                        }

                        TimeSpan lastPts = await GetPtsOffset(_channelNumber, CancellationToken.None);
                        _transcodedUntil = _channelStart + lastPts;
                        _state = HlsSessionState.SeekAndRealtime;

                        _logger.LogInformation(
                            "Scheduled transcode on channel {Channel} was cut for an emergency interrupt; transcoded until {Until}",
                            _channelNumber,
                            _transcodedUntil);

                        return true;
                    }

                    _logger.LogInformation("Terminating HLS session for channel {Channel}", _channelNumber);
                    return false;
                }
                finally
                {
                    lock (_sync)
                    {
                        _interruptCts = null;
                    }

                    foreach (Pipe pipe in maybePipe)
                    {
                        await pipe.Writer.CompleteAsync();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error transcoding channel {Channel} - {Message}", _channelNumber, ex.Message);

            return false;
        }
        finally
        {
            try
            {
                await _mediator.Send(
                    new UpdateOnDemandCheckpoint(_channelNumber, DateTimeOffset.Now),
                    CancellationToken.None);
            }
            catch (Exception)
            {
                // do nothing
            }

            if (!realtime)
            {
                Interlocked.Decrement(ref _workAheadCount);
            }
        }

        return false;
    }

    private void ForceInterrupt()
    {
        lock (_sync)
        {
            try
            {
                _interruptCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // transcode just finished; the queued item will be picked up on the next loop iteration
            }
        }
    }

    private async Task<bool> TranscodeInterrupt(
        InterruptQueueItem item,
        bool realtime,
        CancellationToken cancellationToken)
    {
        try
        {
            TimeSpan ptsOffset = await GetPtsOffset(_channelNumber, cancellationToken);

            var request = new GetInterruptProcessByChannelNumber(
                _channelNumber,
                StreamingMode.HttpLiveStreamingSegmenter,
                _transcodedUntil,
                realtime,
                _channelStart,
                ptsOffset,
                item);

            Either<BaseError, PlayoutItemProcessModel> result = await _mediator.Send(request, cancellationToken);

            foreach (BaseError error in result.LeftAsEnumerable())
            {
                _logger.LogWarning(
                    "Failed to create interrupt process for channel {Channel}; dropping item {Id}: {Error}",
                    _channelNumber,
                    item.Id,
                    error.ToString());

                // drop the item but keep the session alive
                return true;
            }

            foreach (PlayoutItemProcessModel processModel in result.RightAsEnumerable())
            {
                await TrimAndDelete(cancellationToken);

                foreach (long segmentKey in processModel.SegmentKey)
                {
                    _discontinuitySequence++;
                    _discontinuityMap.TryAdd(segmentKey, _discontinuitySequence);
                }

                Command process = processModel.Process;

                _logger.LogDebug("ffmpeg interrupt arguments {FFmpegArguments}", process.Arguments);

                InterruptWebhook.Fire(item.WebhookUrl, "airing", item, _logger);

                var stdErrBuffer = new StringBuilder();

                try
                {
                    CommandResult commandResult = await process
                        .WithWorkingDirectory(_workingDirectory)
                        .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stdErrBuffer))
                        .WithValidation(CommandResultValidation.None)
                        .ExecuteAsync(cancellationToken);

                    if (commandResult.ExitCode == 0)
                    {
                        _transcodedUntil = processModel.Until;

                        // scheduled content resumes mid-item, exactly like real radio
                        // covering programming with a break
                        _state = HlsSessionState.SeekAndRealtime;
                        _hasWrittenSegments = true;

                        _logger.LogInformation(
                            "Interrupt {Title} ({Id}) complete on channel {Channel}; resuming scheduled content at {Until}",
                            item.Title,
                            item.Id,
                            _channelNumber,
                            _transcodedUntil);

                        InterruptWebhook.Fire(item.WebhookUrl, "completed", item, _logger);

                        return true;
                    }

                    _logger.LogError(
                        "Interrupt ffmpeg process for channel {Channel} failed with exit code {ExitCode}: {StandardError}; dropping item {Id}",
                        _channelNumber,
                        commandResult.ExitCode,
                        stdErrBuffer.ToString(),
                        item.Id);

                    return true;
                }
                catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
                {
                    _logger.LogInformation("Terminating HLS session for channel {Channel}", _channelNumber);
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error playing interrupt on channel {Channel} - {Message}; dropping item {Id}",
                _channelNumber,
                ex.Message,
                item.Id);

            return true;
        }
        finally
        {
            _interruptQueue.CleanUpFile(item);
        }
    }

    private async Task TrimAndDelete(CancellationToken cancellationToken)
    {
        await _slim.WaitAsync(cancellationToken);
        try
        {
            Option<string[]> maybeLines = await ReadPlaylistLines(cancellationToken);
            foreach (string[] lines in maybeLines)
            {
                await RefreshInits();

                // trim playlist and insert discontinuity before appending with new ffmpeg process
                TrimPlaylistResult trimResult = _hlsPlaylistFilter.TrimPlaylistWithDiscontinuity(
                    _discontinuityMap,
                    _outputFormatKind,
                    PlaylistStart,
                    DateTimeOffset.Now.AddMinutes(-1),
                    _hlsInitSegmentCache,
                    lines);
                await WritePlaylist(trimResult.Playlist, cancellationToken);

                DeleteOldSegments(trimResult);

                PlaylistStart = trimResult.PlaylistStart;
            }
        }
        finally
        {
            _slim.Release();
        }
    }

    private void DeleteOldSegments(TrimPlaylistResult trimResult)
    {
        var generatedAtHash = new System.Collections.Generic.HashSet<long>();

        // delete old segments
        var allSegments = _fileSystem.Directory.GetFiles(_workingDirectory, "live*.ts")
            .Append(_fileSystem.Directory.GetFiles(_workingDirectory, "live*.mp4"))
            .Append(_fileSystem.Directory.GetFiles(_workingDirectory, "live*.m4s"))
            .Map(file =>
            {
                string fileName = Path.GetFileName(file);
                var sequenceNumber = long.Parse(
                    fileName.Contains('_')
                        ? fileName.Split('_')[2].Split('.')[0]
                        : fileName.Replace("live", string.Empty).Split('.')[0],
                    CultureInfo.InvariantCulture);
                if (!fileName.Contains('_') || !long.TryParse(fileName.Split('_')[1], out long generatedAt))
                {
                    generatedAt = 0;
                }
                generatedAtHash.Add(generatedAt);
                return new Segment(file, sequenceNumber, generatedAt);
            })
            .ToList();

        var allInits = _fileSystem.Directory.GetFiles(_workingDirectory, "*init.mp4")
            .Map(file => long.TryParse(Path.GetFileName(file).Split('_')[0], out long generatedAt) && !generatedAtHash.Contains(generatedAt)
                ? new Segment(file, 0, generatedAt)
                : Option<Segment>.None)
            .Somes()
            .ToList();

        var toDelete = allSegments.Filter(s => s.SequenceNumber < trimResult.Sequence).ToList();
        if (toDelete.Count > 0)
        {
            // _logger.LogDebug(
            //     "Deleting HLS segments {Min} to {Max} (less than {StartSequence})",
            //     toDelete.Map(s => s.SequenceNumber).Min(),
            //     toDelete.Map(s => s.SequenceNumber).Max(),
            //     trimResult.Sequence);
        }

        foreach (var init in allInits)
        {
            // only consider deleting inits that have no segments left on disk, no segments in ffmpeg playlist
            if (generatedAtHash.Contains(init.GeneratedAt) || init.GeneratedAt >= trimResult.GeneratedAt)
            {
                continue;
            }

            string fileName = Path.GetFileName(init.File);
            if (_hlsInitSegmentCache.IsEarliestByHash(fileName))
            {
                continue;
            }

            toDelete.Add(init);
            _hlsInitSegmentCache.DeleteSegment(fileName);
            _discontinuityMap.Remove(init.GeneratedAt);
        }

        foreach (Segment segment in toDelete)
        {
            try
            {
                _fileSystem.File.Delete(segment.File);
            }
            catch (IOException)
            {
                // work around lots of:
                //   The process cannot access the file '...' because it is being used by another process
                _logger.LogDebug("Failed to delete old segment {File}", segment.File);
            }
        }
    }

    private async Task RefreshInits()
    {
        var allSegments = _fileSystem.Directory.GetFiles(_workingDirectory, "live*.m4s")
            .Map(Path.GetFileName)
            .Map(s => s.Split("_")[1])
            .ToHashSet();

        foreach (string file in _fileSystem.Directory.GetFiles(_workingDirectory, "*init.mp4"))
        {
            string key = Path.GetFileName(file).Split("_")[0];
            if (allSegments.Contains(key))
            {
                await _hlsInitSegmentCache.AddSegment(file);
            }
        }
    }

    private async Task<TimeSpan> GetPtsOffset(string channelNumber, CancellationToken cancellationToken)
    {
        await _slim.WaitAsync(cancellationToken);
        try
        {
            TimeSpan result = TimeSpan.Zero;

            // if we haven't yet written any segments, start at zero
            if (!_hasWrittenSegments)
            {
                return result;
            }

            await RefreshInits();

            Either<BaseError, PtsTime> queryResult = await _mediator.Send(
                new GetLastPtsTime(_hlsInitSegmentCache, channelNumber),
                cancellationToken);

            foreach (BaseError error in queryResult.LeftToSeq())
            {
                _logger.LogWarning("Unable to determine last pts offset - {Error}", error.ToString());
            }

            foreach (PtsTime pts in queryResult.RightToSeq())
            {
                _logger.LogDebug("Last pts offset is {Pts}", pts.Value);
                result = pts.Value;
            }

            return result;
        }
        finally
        {
            _slim.Release();
        }
    }

    private async Task<int> GetWorkAheadLimit(CancellationToken cancellationToken) =>
        await _configElementRepository.GetValue<int>(ConfigElementKey.FFmpegWorkAheadSegmenters, cancellationToken)
            .Map(maybeCount => maybeCount.Match(identity, () => 1));

    private async Task<Option<string[]>> ReadPlaylistLines(CancellationToken cancellationToken)
    {
        string fileName = PlaylistFileName();
        if (_fileSystem.File.Exists(fileName))
        {
            return await _fileSystem.File.ReadAllLinesAsync(fileName, cancellationToken);
        }

        _logger.LogDebug("Playlist does not exist at expected location {File}", fileName);
        return None;
    }

    private async Task WritePlaylist(string playlist, CancellationToken cancellationToken)
    {
        string fileName = PlaylistFileName();
        await _fileSystem.File.WriteAllTextAsync(fileName, playlist, cancellationToken);
    }

    private string PlaylistFileName() => Path.Combine(_workingDirectory, "live.m3u8");

    private sealed record Segment(string File, long SequenceNumber, long GeneratedAt);
}
