using System.Globalization;
using CliWrap;
using CliWrap.Buffered;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Filler;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Interfaces.Streaming;
using ErsatzTV.Core.Interrupts;
using ErsatzTV.Core.Tts;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Application.Streaming;

public class ChannelAnnouncerService : IChannelAnnouncerService
{
    private static readonly TimeSpan ConfigCacheDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ItemStartTolerance = TimeSpan.FromSeconds(5);

    private readonly IDbContextFactory<TvContext> _dbContextFactory;
    private readonly IConfigElementRepository _configElementRepository;
    private readonly IChannelInterruptService _interruptService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ChannelAnnouncerService> _logger;

    // scoped per hls session; state lives for the session
    private (DateTimeOffset FetchedAt, bool Enabled, string Template, InterruptStyle Style, double BedVolume,
        string TtsEndpoint, string Voice)? _cachedConfig;
    private int _lastAnnouncedMediaItemId = -1;
    private bool _warnedNoTtsUrl;

    public ChannelAnnouncerService(
        IDbContextFactory<TvContext> dbContextFactory,
        IConfigElementRepository configElementRepository,
        IChannelInterruptService interruptService,
        IHttpClientFactory httpClientFactory,
        ILogger<ChannelAnnouncerService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _configElementRepository = configElementRepository;
        _interruptService = interruptService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task AnnounceUpcomingItem(
        string channelNumber,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        try
        {
            (bool enabled, string template, InterruptStyle style, double bedVolume, string ttsEndpoint, string voice) =
                await GetConfig(channelNumber, cancellationToken);

            if (!enabled)
            {
                return;
            }

            await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

            Option<Channel> maybeChannel = await dbContext.Channels
                .AsNoTracking()
                .SelectOneAsync(c => c.Number, c => c.Number == channelNumber, cancellationToken);

            foreach (Channel channel in maybeChannel)
            {
                Option<PlayoutItem> maybeItem = await dbContext.PlayoutItems
                    .AsNoTracking()
                    .Include(pi => pi.MediaItem)
                    .ThenInclude(mi => (mi as Song).SongMetadata)
                    .Include(pi => pi.MediaItem)
                    .ThenInclude(mi => (mi as Episode).EpisodeMetadata)
                    .Include(pi => pi.MediaItem)
                    .ThenInclude(mi => (mi as Episode).Season)
                    .ThenInclude(s => s.SeasonMetadata)
                    .Include(pi => pi.MediaItem)
                    .ThenInclude(mi => (mi as Episode).Season)
                    .ThenInclude(s => s.Show)
                    .ThenInclude(sh => sh.ShowMetadata)
                    .ForChannelAndTime(channel.MirrorSourceChannelId ?? channel.Id, at);

                foreach (PlayoutItem item in maybeItem)
                {
                    // only announce real content, and only at (or very near) its start
                    if (item.FillerKind is not FillerKind.None)
                    {
                        return;
                    }

                    var itemStart = new DateTimeOffset(item.Start, TimeSpan.Zero);
                    if ((at.ToUniversalTime() - itemStart).Duration() > ItemStartTolerance)
                    {
                        return;
                    }

                    if (item.MediaItemId == _lastAnnouncedMediaItemId)
                    {
                        return;
                    }

                    // dedup even when tts fails, to avoid hammering the endpoint
                    _lastAnnouncedMediaItemId = item.MediaItemId;

                    string text = RenderTemplate(template, item.MediaItem);
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        return;
                    }

                    Option<string> maybeAudioPath = await GenerateTts(text, ttsEndpoint, voice, cancellationToken);
                    foreach (string audioPath in maybeAudioPath)
                    {
                        Option<TimeSpan> maybeDuration = await ProbeDuration(audioPath, cancellationToken);
                        foreach (TimeSpan duration in maybeDuration)
                        {
                            var interrupt = new InterruptQueueItem
                            {
                                ChannelNumber = channelNumber,
                                Path = audioPath,
                                Title = $"Announcer: {text}",
                                Priority = 1,
                                EnqueuedAt = DateTimeOffset.Now,
                                ExpiresAt = at.AddSeconds(60),
                                Duration = duration,
                                DeleteFileWhenDone = true,
                                Style = style,
                                DuckBedVolume = bedVolume
                            };

                            _interruptService.Enqueue(interrupt);

                            _logger.LogInformation(
                                "Announcer enqueued for channel {Channel}: {Text}",
                                channelNumber,
                                text);

                            return;
                        }

                        // probe failed; clean up the tts temp file
                        TryDelete(audioPath);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Announcer failed for channel {Channel}", channelNumber);
        }
    }

    private async Task<(bool Enabled, string Template, InterruptStyle Style, double BedVolume,
        string TtsEndpoint, string Voice)> GetConfig(
        string channelNumber,
        CancellationToken cancellationToken)
    {
        if (_cachedConfig is { } cached && DateTimeOffset.Now - cached.FetchedAt < ConfigCacheDuration)
        {
            return (cached.Enabled, cached.Template, cached.Style, cached.BedVolume, cached.TtsEndpoint, cached.Voice);
        }

        bool enabled = await _configElementRepository
            .GetValue<bool>(ConfigElementKey.AnnouncerEnabled(channelNumber), cancellationToken)
            .Map(o => o.IfNone(false));

        string template = await _configElementRepository
            .GetValue<string>(ConfigElementKey.AnnouncerTemplate(channelNumber), cancellationToken)
            .Map(o => o.IfNone("Now playing: {title}"));

        InterruptStyle style = await _configElementRepository
            .GetValue<string>(ConfigElementKey.AnnouncerStyle(channelNumber), cancellationToken)
            .Map(o => o.Match(
                s => string.Equals(s, "replace", StringComparison.OrdinalIgnoreCase)
                    ? InterruptStyle.Replace
                    : InterruptStyle.Duck,
                () => InterruptStyle.Duck));

        double bedVolume = await _configElementRepository
            .GetValue<int>(ConfigElementKey.AnnouncerDuckPercent(channelNumber), cancellationToken)
            .Map(o => Math.Clamp(o.IfNone(30), 0, 100) / 100.0);

        string ttsEndpoint = await _configElementRepository
            .GetValue<string>(ConfigElementKey.AnnouncerTtsEndpoint(channelNumber), cancellationToken)
            .Map(o => o.IfNone(string.Empty));

        string voice = await _configElementRepository
            .GetValue<string>(ConfigElementKey.AnnouncerVoice(channelNumber), cancellationToken)
            .Map(o => o.IfNone(string.Empty));

        _cachedConfig = (DateTimeOffset.Now, enabled, template, style, bedVolume, ttsEndpoint, voice);

        return (enabled, template, style, bedVolume, ttsEndpoint, voice);
    }

    private static string RenderTemplate(string template, MediaItem mediaItem)
    {
        string title = string.Empty;
        string artist = string.Empty;
        string album = string.Empty;
        string show = string.Empty;
        string season = string.Empty;

        switch (mediaItem)
        {
            case Song song:
                foreach (SongMetadata sm in song.SongMetadata.HeadOrNone())
                {
                    title = sm.Title ?? string.Empty;
                    artist = string.Join(", ", sm.Artists ?? []);
                    album = sm.Album ?? string.Empty;
                }

                break;
            case Episode episode:
                foreach (EpisodeMetadata em in episode.EpisodeMetadata.HeadOrNone())
                {
                    title = em.Title ?? string.Empty;
                }

                foreach (SeasonMetadata seasonMetadata in Optional(episode.Season?.SeasonMetadata).Flatten().HeadOrNone())
                {
                    season = seasonMetadata.Title ?? string.Empty;
                }

                foreach (ShowMetadata showMetadata in Optional(episode.Season?.Show?.ShowMetadata).Flatten().HeadOrNone())
                {
                    show = showMetadata.Title ?? string.Empty;
                }

                break;
            default:
                return string.Empty;
        }

        string text = template
            .Replace("{title}", title, StringComparison.OrdinalIgnoreCase)
            .Replace("{artist}", artist, StringComparison.OrdinalIgnoreCase)
            .Replace("{album}", album, StringComparison.OrdinalIgnoreCase)
            .Replace("{show}", show, StringComparison.OrdinalIgnoreCase)
            .Replace("{season}", season, StringComparison.OrdinalIgnoreCase)
            .Replace("{author}", show, StringComparison.OrdinalIgnoreCase)
            .Replace("{book}", season, StringComparison.OrdinalIgnoreCase);

        return string.IsNullOrWhiteSpace(title) ? string.Empty : text.Trim();
    }

    private async Task<Option<string>> GenerateTts(
        string text,
        string ttsEndpointName,
        string channelVoice,
        CancellationToken cancellationToken)
    {
        Option<(string Url, string Voice)> maybeTarget =
            await ResolveTtsTarget(ttsEndpointName, channelVoice, cancellationToken);

        foreach ((string url, string voice) in maybeTarget)
        {
            try
            {
                byte[] audio;

                if (url.StartsWith("wyoming://", StringComparison.OrdinalIgnoreCase))
                {
                    var uri = new Uri(url.Replace("wyoming://", "tcp://", StringComparison.OrdinalIgnoreCase));
                    int port = uri.IsDefaultPort ? 10200 : uri.Port;

                    audio = await WyomingTtsClient.Synthesize(
                        uri.Host,
                        port,
                        text,
                        Optional(voice).Filter(v => !string.IsNullOrWhiteSpace(v)),
                        TimeSpan.FromSeconds(30),
                        cancellationToken);
                }
                else
                {
                    HttpClient client = _httpClientFactory.CreateClient();
                    client.Timeout = TimeSpan.FromSeconds(30);

                    using var content = new StringContent(text);
                    using HttpResponseMessage response = await client.PostAsync(url, content, cancellationToken);

                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogWarning(
                            "Announcer TTS endpoint returned {StatusCode}; skipping announcement",
                            (int)response.StatusCode);

                        return Option<string>.None;
                    }

                    audio = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                }

                if (audio.Length == 0)
                {
                    _logger.LogWarning("Announcer TTS endpoint returned no audio; skipping announcement");
                    return Option<string>.None;
                }

                string path = Path.Combine(FileSystemLayout.InterruptsFolder, $"announcer-{Guid.NewGuid()}.wav");
                await File.WriteAllBytesAsync(path, audio, cancellationToken);
                return path;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Announcer TTS request failed; skipping announcement");
                return Option<string>.None;
            }
        }

        if (!_warnedNoTtsUrl)
        {
            _warnedNoTtsUrl = true;
            _logger.LogWarning(
                "Announcer is enabled but no TTS endpoint is configured or the configured endpoint name was not found");
        }

        return Option<string>.None;
    }

    private async Task<Option<(string Url, string Voice)>> ResolveTtsTarget(
        string ttsEndpointName,
        string channelVoice,
        CancellationToken cancellationToken)
    {
        List<TtsEndpoint> endpoints = await LoadEndpoints(cancellationToken);

        // named endpoint from channel config
        if (!string.IsNullOrWhiteSpace(ttsEndpointName))
        {
            foreach (TtsEndpoint endpoint in Optional(
                         endpoints.Find(e => string.Equals(e.Name, ttsEndpointName, StringComparison.OrdinalIgnoreCase))))
            {
                return (endpoint.Url, FirstNonEmpty(channelVoice, endpoint.Voice));
            }

            _logger.LogWarning(
                "Announcer TTS endpoint {Name} was not found in the endpoints registry",
                ttsEndpointName);

            return Option<(string, string)>.None;
        }

        // first registered endpoint
        foreach (TtsEndpoint endpoint in endpoints.HeadOrNone())
        {
            return (endpoint.Url, FirstNonEmpty(channelVoice, endpoint.Voice));
        }

        // legacy single url
        Option<string> maybeLegacyUrl = await _configElementRepository.GetValue<string>(
            ConfigElementKey.AnnouncerTtsUrl,
            cancellationToken);

        foreach (string legacyUrl in maybeLegacyUrl.Filter(u => !string.IsNullOrWhiteSpace(u)))
        {
            return (legacyUrl, channelVoice);
        }

        return Option<(string, string)>.None;
    }

    private async Task<List<TtsEndpoint>> LoadEndpoints(CancellationToken cancellationToken)
    {
        Option<string> maybeJson = await _configElementRepository.GetValue<string>(
            ConfigElementKey.AnnouncerTtsEndpoints,
            cancellationToken);

        foreach (string json in maybeJson.Filter(j => !string.IsNullOrWhiteSpace(j)))
        {
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<List<TtsEndpoint>>(json) ?? [];
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse announcer tts endpoints; ignoring");
            }
        }

        return [];
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;

    private async Task<Option<TimeSpan>> ProbeDuration(string path, CancellationToken cancellationToken)
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
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to probe announcer audio {Path}", path);
            }
        }

        return Option<TimeSpan>.None;
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete announcer temp file {Path}", path);
        }
    }
}
