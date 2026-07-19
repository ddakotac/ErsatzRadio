using System.Globalization;
using CliWrap;
using CliWrap.Buffered;
using ErsatzTV.Core;
using ErsatzTV.Core.Audiobookshelf;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Navidrome;
using ErsatzTV.Core.Domain.Filler;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Interfaces.Streaming;
using ErsatzTV.Core.Interrupts;
using ErsatzTV.Core.Interfaces.Tts;
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
    private readonly ITtsSynthesisService _ttsSynthesisService;
    private readonly ILogger<ChannelAnnouncerService> _logger;

    // scoped per hls session; state lives for the session
    private (DateTimeOffset FetchedAt, bool Enabled, string Template, InterruptStyle Style, double BedVolume,
        string TtsEndpoint, string Voice)? _cachedConfig;
    private int _lastAnnouncedMediaItemId = -1;

    public ChannelAnnouncerService(
        IDbContextFactory<TvContext> dbContextFactory,
        IConfigElementRepository configElementRepository,
        IChannelInterruptService interruptService,
        ITtsSynthesisService ttsSynthesisService,
        ILogger<ChannelAnnouncerService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _configElementRepository = configElementRepository;
        _interruptService = interruptService;
        _ttsSynthesisService = ttsSynthesisService;
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

    public async Task<Option<string>> RenderTemplateForCurrentItem(
        string channelNumber,
        string announcementTemplate,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(announcementTemplate))
        {
            return Option<string>.None;
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
                .ForChannelAndTime(channel.MirrorSourceChannelId ?? channel.Id, DateTimeOffset.Now);

            foreach (PlayoutItem item in maybeItem)
            {
                string rendered = RenderTemplate(announcementTemplate, item.MediaItem);
                if (!string.IsNullOrWhiteSpace(rendered))
                {
                    return rendered;
                }
            }
        }

        return Option<string>.None;
    }

    public async Task<Option<NowPlayingInfo>> GetNowPlayingForCurrentItem(
        string channelNumber,
        CancellationToken cancellationToken)
    {
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
                .ThenInclude(sm => sm.Artwork)
                .Include(pi => pi.MediaItem)
                .ThenInclude(mi => (mi as Episode).EpisodeMetadata)
                .ThenInclude(em => em.Artwork)
                .Include(pi => pi.MediaItem)
                .ThenInclude(mi => (mi as Episode).Season)
                .ThenInclude(s => s.SeasonMetadata)
                .ThenInclude(sm => sm.Artwork)
                .Include(pi => pi.MediaItem)
                .ThenInclude(mi => (mi as Episode).Season)
                .ThenInclude(s => s.Show)
                .ThenInclude(sh => sh.ShowMetadata)
                .ForChannelAndTime(channel.MirrorSourceChannelId ?? channel.Id, DateTimeOffset.Now);

            foreach (PlayoutItem item in maybeItem)
            {
                switch (item.MediaItem)
                {
                    case Song song:
                        foreach (SongMetadata sm in song.SongMetadata.HeadOrNone())
                        {
                            string artist = string.Join(", ", sm.Artists ?? []);
                            string title = string.IsNullOrWhiteSpace(artist)
                                ? sm.Title ?? string.Empty
                                : $"{artist} - {sm.Title}";

                            string artworkUrl = PickArtwork(sm.Artwork);

                            if (!string.IsNullOrWhiteSpace(title))
                            {
                                return new NowPlayingInfo(title, artworkUrl);
                            }
                        }

                        break;
                    case Episode episode:
                        string chapter = episode.EpisodeMetadata.HeadOrNone()
                            .Map(em => em.Title ?? string.Empty)
                            .IfNone(string.Empty);

                        string book = Optional(episode.Season?.SeasonMetadata).Flatten().HeadOrNone()
                            .Map(sm => sm.Title ?? string.Empty)
                            .IfNone(string.Empty);

                        string author = Optional(episode.Season?.Show?.ShowMetadata).Flatten().HeadOrNone()
                            .Map(sm => sm.Title ?? string.Empty)
                            .IfNone(string.Empty);

                        // "{author} - {book}: {chapter}" degrading gracefully as parts go missing
                        string display = chapter;
                        if (!string.IsNullOrWhiteSpace(book))
                        {
                            display = string.IsNullOrWhiteSpace(display) ? book : $"{book}: {display}";
                        }

                        if (!string.IsNullOrWhiteSpace(author))
                        {
                            display = string.IsNullOrWhiteSpace(display) ? author : $"{author} - {display}";
                        }

                        // episode thumbnail, else the book cover (season poster/thumbnail)
                        string episodeArt = null;
                        foreach (EpisodeMetadata em in episode.EpisodeMetadata.HeadOrNone())
                        {
                            episodeArt = PickArtwork(em.Artwork);
                        }

                        string bookArt = null;
                        foreach (SeasonMetadata seasonArtMetadata in Optional(episode.Season?.SeasonMetadata).Flatten().HeadOrNone())
                        {
                            bookArt = PickArtwork(seasonArtMetadata.Artwork);
                        }

                        if (!string.IsNullOrWhiteSpace(display))
                        {
                            return new NowPlayingInfo(display, episodeArt ?? bookArt);
                        }

                        break;
                }
            }
        }

        return Option<NowPlayingInfo>.None;
    }

    private static string PickArtwork(List<Artwork> artwork)
    {
        if (artwork is null || artwork.Count == 0)
        {
            return null;
        }

        Artwork best = artwork.FirstOrDefault(a => a.ArtworkKind == ArtworkKind.Thumbnail)
                       ?? artwork.FirstOrDefault(a => a.ArtworkKind == ArtworkKind.Poster);

        if (best is null)
        {
            return null;
        }

        // scheme-prefixed paths map to the artwork proxy routes (the generic
        // /artwork/{id} redirect mangles them; the RelativeProxyForArtwork helpers
        // return prefix-less paths meant for card-mapper contexts) - build the
        // fully-rooted url explicitly per kind
        string kindSegment = best.ArtworkKind == ArtworkKind.Thumbnail ? "thumbnails" : "posters";

        if (best.Path?.StartsWith("abs://", StringComparison.OrdinalIgnoreCase) == true)
        {
            string remainder = best.Path["abs://".Length..];
            return $"/artwork/{kindSegment}/abs/{remainder}?width=600";
        }

        if (best.Path?.StartsWith("navidrome://", StringComparison.OrdinalIgnoreCase) == true)
        {
            string remainder = best.Path["navidrome://".Length..];
            return $"/artwork/{kindSegment}/navidrome/{remainder}?size=600";
        }

        if (best.Path?.Contains("://", StringComparison.Ordinal) == true)
        {
            // other external schemes (jellyfin/emby/plex): id-redirect as best effort
            return $"/artwork/{best.Id}";
        }

        return $"/artwork/{kindSegment}/{best.Path}";
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

                // cross-map so any template reads sensibly for any media type:
                // {author}/{show} -> artist, {book}/{season} -> album
                show = artist;
                season = album;
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

                // cross-map: {artist} -> author/show, {album} -> book/season
                artist = show;
                album = season;
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

    private Task<Option<string>> GenerateTts(
        string text,
        string ttsEndpointName,
        string channelVoice,
        CancellationToken cancellationToken) =>
        _ttsSynthesisService.SynthesizeToFile(text, ttsEndpointName, channelVoice, cancellationToken);

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
