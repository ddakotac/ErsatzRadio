using System.Globalization;
using System.Text.Json;
using CliWrap;
using CliWrap.Buffered;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Interfaces.Streaming;
using ErsatzTV.Core.Interrupts;
using LanguageExt;

namespace ErsatzTV.Services;

/// <summary>
///     Polls configured watch folders and enqueues newly arrived audio files as
///     interrupts on mapped channels ("breaking news" / timely-podcast delivery).
///     Polling (not FileSystemWatcher) because inotify does not propagate reliably
///     over NFS/SMB mounts. A file is enqueued once its size is stable across two
///     consecutive polls and its mtime is newer than the folder's watermark.
/// </summary>
public class WatchFolderService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private static readonly string[] AudioExtensions =
        [".mp3", ".m4a", ".m4b", ".aac", ".flac", ".ogg", ".opus", ".wav", ".wma"];

    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IChannelInterruptService _interruptService;
    private readonly ILogger<WatchFolderService> _logger;

    // path -> (size, seenAt) for the stability check
    private readonly Dictionary<string, (long Size, DateTimeOffset SeenAt)> _pending = new();

    public WatchFolderService(
        IServiceScopeFactory serviceScopeFactory,
        IChannelInterruptService interruptService,
        ILogger<WatchFolderService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _interruptService = interruptService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // let the app settle before the first poll
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnce(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // shutting down
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Watch folder poll failed; will retry");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task PollOnce(CancellationToken cancellationToken)
    {
        using IServiceScope scope = _serviceScopeFactory.CreateScope();
        IConfigElementRepository configRepository =
            scope.ServiceProvider.GetRequiredService<IConfigElementRepository>();

        List<WatchFolder> folders = await LoadFolders(configRepository, cancellationToken);
        if (folders.Count == 0)
        {
            return;
        }

        Option<string> maybeFFprobePath = await configRepository.GetValue<string>(
            ConfigElementKey.FFprobePath,
            cancellationToken);

        foreach (WatchFolder folder in folders.Filter(f => f.Enabled))
        {
            if (string.IsNullOrWhiteSpace(folder.Path) || !Directory.Exists(folder.Path))
            {
                continue;
            }

            DateTimeOffset watermark = await GetWatermark(configRepository, folder.Name, cancellationToken);
            var newWatermark = watermark;

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder.Path, "*", SearchOption.AllDirectories)
                    .Where(f => AudioExtensions.Contains(
                        Path.GetExtension(f),
                        StringComparer.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to enumerate watch folder {Path}", folder.Path);
                continue;
            }

            foreach (string file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                FileInfo info;
                try
                {
                    info = new FileInfo(file);
                }
                catch
                {
                    continue;
                }

                var mtime = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
                if (mtime <= watermark)
                {
                    _pending.Remove(file);
                    continue;
                }

                // stability check: unchanged size across two polls
                if (_pending.TryGetValue(file, out (long Size, DateTimeOffset SeenAt) seen) &&
                    seen.Size == info.Length)
                {
                    _pending.Remove(file);

                    bool enqueued = await EnqueueFile(folder, file, maybeFFprobePath, cancellationToken);
                    if (enqueued && mtime > newWatermark)
                    {
                        newWatermark = mtime;
                    }
                }
                else
                {
                    _pending[file] = (info.Length, DateTimeOffset.Now);
                }
            }

            if (newWatermark > watermark)
            {
                await SetWatermark(configRepository, folder.Name, newWatermark, cancellationToken);
            }
        }

        // drop stale pending entries (files that vanished or stopped changing long ago)
        foreach (string stale in _pending
                     .Filter(kvp => DateTimeOffset.Now - kvp.Value.SeenAt > TimeSpan.FromHours(6))
                     .Map(kvp => kvp.Key)
                     .ToList())
        {
            _pending.Remove(stale);
        }
    }

    private async Task<bool> EnqueueFile(
        WatchFolder folder,
        string file,
        Option<string> maybeFFprobePath,
        CancellationToken cancellationToken)
    {
        Option<TimeSpan> maybeDuration = await ProbeDuration(maybeFFprobePath, file, cancellationToken);
        if (maybeDuration.IsNone)
        {
            _logger.LogWarning(
                "Watch folder {Name}: could not probe {File}; skipping (will not retry)",
                folder.Name,
                file);

            // returning true advances the watermark past unprobeable files
            return true;
        }

        foreach (TimeSpan duration in maybeDuration)
        {
            InterruptStyle style = string.Equals(folder.Style, "duck", StringComparison.OrdinalIgnoreCase)
                ? InterruptStyle.Duck
                : InterruptStyle.Replace;

            foreach (string channelNumber in folder.Channels.Distinct())
            {
                var item = new InterruptQueueItem
                {
                    Id = Guid.NewGuid(),
                    ChannelNumber = channelNumber,
                    Path = file,
                    Title = $"{folder.Name}: {Path.GetFileNameWithoutExtension(file)}",
                    Priority = folder.Priority,
                    EnqueuedAt = DateTimeOffset.Now,
                    ExpiresAt = DateTimeOffset.Now.AddSeconds(folder.TtlSeconds),
                    Duration = duration,
                    DeleteFileWhenDone = false,
                    Style = style,
                    DuckBedVolume = Math.Clamp(folder.DuckPercent, 0, 100) / 100.0
                };

                _interruptService.Enqueue(item);

                _logger.LogInformation(
                    "Watch folder {Name}: enqueued {File} on channel {Channel} (priority {Priority}, {Style})",
                    folder.Name,
                    Path.GetFileName(file),
                    channelNumber,
                    folder.Priority,
                    style);
            }
        }

        return true;
    }

    private async Task<Option<TimeSpan>> ProbeDuration(
        Option<string> maybeFFprobePath,
        string path,
        CancellationToken cancellationToken)
    {
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
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to probe watch folder file {Path}", path);
            }
        }

        return Option<TimeSpan>.None;
    }

    private static async Task<List<WatchFolder>> LoadFolders(
        IConfigElementRepository configRepository,
        CancellationToken cancellationToken)
    {
        Option<string> maybeJson = await configRepository.GetValue<string>(
            ConfigElementKey.WatchFolders,
            cancellationToken);

        foreach (string json in maybeJson.Filter(j => !string.IsNullOrWhiteSpace(j)))
        {
            try
            {
                return JsonSerializer.Deserialize<List<WatchFolder>>(json) ?? [];
            }
            catch
            {
                return [];
            }
        }

        return [];
    }

    private static async Task<DateTimeOffset> GetWatermark(
        IConfigElementRepository configRepository,
        string folderName,
        CancellationToken cancellationToken)
    {
        Option<string> maybeValue = await configRepository.GetValue<string>(
            ConfigElementKey.WatchFolderWatermark(folderName),
            cancellationToken);

        foreach (string value in maybeValue)
        {
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed))
            {
                return parsed;
            }
        }

        // first sighting of this folder: start from now so the existing backlog
        // does not blast onto the air
        DateTimeOffset now = DateTimeOffset.Now;
        await configRepository.Upsert(
            ConfigElementKey.WatchFolderWatermark(folderName),
            now.ToString("O", CultureInfo.InvariantCulture),
            cancellationToken);

        return now;
    }

    private static Task<Unit> SetWatermark(
        IConfigElementRepository configRepository,
        string folderName,
        DateTimeOffset watermark,
        CancellationToken cancellationToken) =>
        configRepository.Upsert(
            ConfigElementKey.WatchFolderWatermark(folderName),
            watermark.ToString("O", CultureInfo.InvariantCulture),
            cancellationToken);
}
