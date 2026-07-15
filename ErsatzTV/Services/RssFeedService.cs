using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using CliWrap;
using CliWrap.Buffered;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Interfaces.Streaming;
using ErsatzTV.Core.Interfaces.Tts;
using ErsatzTV.Core.Interrupts;
using LanguageExt;

namespace ErsatzTV.Services;

/// <summary>
///     Polls podcast RSS feeds directly (no Audiobookshelf in the loop) and enqueues
///     newly published episodes as interrupts on the mapped channels. Episodes with
///     pubDate newer than the per-feed watermark are downloaded to the interrupts
///     folder and enqueued with DeleteFileWhenDone (one copy per channel).
/// </summary>
public class RssFeedService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);
    private const int MaxItemsPerFeed = 10;
    private const long MaxEnclosureBytes = 512L * 1024 * 1024;

    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IChannelInterruptService _interruptService;
    private readonly IFFmpegSegmenterService _ffmpegSegmenterService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<RssFeedService> _logger;

    public RssFeedService(
        IServiceScopeFactory serviceScopeFactory,
        IChannelInterruptService interruptService,
        IFFmpegSegmenterService ffmpegSegmenterService,
        IHttpClientFactory httpClientFactory,
        ILogger<RssFeedService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _interruptService = interruptService;
        _ffmpegSegmenterService = ffmpegSegmenterService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "RSS feed service started; polling every {Minutes}m",
            (int)PollInterval.TotalMinutes);

        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

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
                _logger.LogWarning(ex, "RSS feed poll failed; will retry");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task PollOnce(CancellationToken cancellationToken)
    {
        using IServiceScope scope = _serviceScopeFactory.CreateScope();
        IConfigElementRepository configRepository =
            scope.ServiceProvider.GetRequiredService<IConfigElementRepository>();
        ITtsSynthesisService ttsSynthesisService =
            scope.ServiceProvider.GetRequiredService<ITtsSynthesisService>();

        List<RssFeed> feeds = await LoadFeeds(configRepository, cancellationToken);
        if (feeds.Count == 0)
        {
            return;
        }

        _logger.LogDebug(
            "Polling {Count} rss feed(s): {Names}",
            feeds.Count,
            string.Join(", ", feeds.Map(f => f.Name)));

        Option<string> maybeFFprobePath = await configRepository.GetValue<string>(
            ConfigElementKey.FFprobePath,
            cancellationToken);

        foreach (RssFeed feed in feeds.Filter(f => f.Enabled))
        {
            try
            {
                await PollFeed(configRepository, feed, maybeFFprobePath, ttsSynthesisService, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "RSS feed {Name}: poll failed; will retry next cycle", feed.Name);
            }
        }
    }

    private async Task PollFeed(
        IConfigElementRepository configRepository,
        RssFeed feed,
        Option<string> maybeFFprobePath,
        ITtsSynthesisService ttsSynthesisService,
        CancellationToken cancellationToken)
    {
        DateTimeOffset watermark = await GetWatermark(configRepository, feed.Name, cancellationToken);
        var newWatermark = watermark;

        HttpClient client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(60);

        string xml = await client.GetStringAsync(feed.Url, cancellationToken);
        var doc = XDocument.Parse(xml);

        // rss 2.0: rss/channel/item with enclosure; pubDate rfc1123-ish
        var episodes = doc.Descendants("item")
            .Map(item => new
            {
                Title = item.Element("title")?.Value?.Trim() ?? "Untitled",
                EnclosureUrl = item.Element("enclosure")?.Attribute("url")?.Value,
                PubDate = ParsePubDate(item.Element("pubDate")?.Value)
            })
            .Filter(e => !string.IsNullOrWhiteSpace(e.EnclosureUrl) && e.PubDate.IsSome)
            .Take(MaxItemsPerFeed)
            .ToList();

        foreach (var episode in episodes.OrderBy(e => e.PubDate.IfNone(DateTimeOffset.MinValue)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (DateTimeOffset pubDate in episode.PubDate)
            {
                if (pubDate <= watermark)
                {
                    continue;
                }

                bool enqueued = await DownloadAndEnqueue(
                    feed,
                    episode.Title,
                    episode.EnclosureUrl,
                    maybeFFprobePath,
                    ttsSynthesisService,
                    cancellationToken);

                if (enqueued && pubDate > newWatermark)
                {
                    newWatermark = pubDate;
                }
            }
        }

        if (newWatermark > watermark)
        {
            await configRepository.Upsert(
                ConfigElementKey.RssFeedWatermark(feed.Name),
                newWatermark.ToString("O", CultureInfo.InvariantCulture),
                cancellationToken);
        }
    }

    private async Task<bool> DownloadAndEnqueue(
        RssFeed feed,
        string title,
        string enclosureUrl,
        Option<string> maybeFFprobePath,
        ITtsSynthesisService ttsSynthesisService,
        CancellationToken cancellationToken)
    {
        string extension = Path.GetExtension(new Uri(enclosureUrl).AbsolutePath);
        if (string.IsNullOrWhiteSpace(extension) || extension.Length > 5)
        {
            extension = ".mp3";
        }

        string downloadPath = Path.Combine(
            FileSystemLayout.InterruptsFolder,
            $"rss-{Guid.NewGuid()}{extension}");

        try
        {
            HttpClient client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(10);

            using HttpResponseMessage response = await client.GetAsync(
                enclosureUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength is > MaxEnclosureBytes)
            {
                _logger.LogWarning(
                    "RSS feed {Name}: {Title} enclosure exceeds size limit; skipping",
                    feed.Name,
                    title);

                return true; // advance watermark; do not retry forever
            }

            await using (Stream source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (FileStream target = File.Create(downloadPath))
            {
                await source.CopyToAsync(target, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "RSS feed {Name}: failed to download {Title}; will retry next cycle",
                feed.Name,
                title);

            TryDelete(downloadPath);
            return false; // watermark not advanced - retried next poll
        }

        Option<TimeSpan> maybeDuration = await ProbeDuration(maybeFFprobePath, downloadPath, cancellationToken);
        if (maybeDuration.IsNone)
        {
            _logger.LogWarning(
                "RSS feed {Name}: could not probe {Title}; skipping (will not retry)",
                feed.Name,
                title);

            TryDelete(downloadPath);
            return true;
        }

        foreach (TimeSpan duration in maybeDuration)
        {
            InterruptStyle style = string.Equals(feed.Style, "duck", StringComparison.OrdinalIgnoreCase)
                ? InterruptStyle.Duck
                : InterruptStyle.Replace;

            await DeliveryDispatch.Dispatch(
                "RSS feed",
                feed.Name,
                title,
                downloadPath,
                duration,
                feed.Channels,
                feed.Priority,
                style,
                Math.Clamp(feed.DuckPercent, 0, 100) / 100.0,
                feed.TtlSeconds,
                deleteContentWhenDone: true,
                feed.IntroText,
                feed.OutroText,
                feed.TtsEndpoint,
                feed.Voice,
                feed.WebhookUrl,
                _interruptService,
                ttsSynthesisService,
                _ffmpegSegmenterService,
                _httpClientFactory,
                (p, ct) => ProbeDuration(maybeFFprobePath, p, ct),
                _logger,
                cancellationToken);
        }

        return true;
    }

    private static Option<DateTimeOffset> ParsePubDate(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Option<DateTimeOffset>.None;
        }

        // rfc1123 with signed offset variants ("+0000") and named zones
        string cleaned = value.Trim()
            .Replace(" GMT", " +0000", StringComparison.OrdinalIgnoreCase)
            .Replace(" UT", " +0000", StringComparison.OrdinalIgnoreCase)
            .Replace(" EST", " -0500", StringComparison.OrdinalIgnoreCase)
            .Replace(" EDT", " -0400", StringComparison.OrdinalIgnoreCase)
            .Replace(" CST", " -0600", StringComparison.OrdinalIgnoreCase)
            .Replace(" CDT", " -0500", StringComparison.OrdinalIgnoreCase)
            .Replace(" MST", " -0700", StringComparison.OrdinalIgnoreCase)
            .Replace(" MDT", " -0600", StringComparison.OrdinalIgnoreCase)
            .Replace(" PST", " -0800", StringComparison.OrdinalIgnoreCase)
            .Replace(" PDT", " -0700", StringComparison.OrdinalIgnoreCase);

        string[] formats =
        [
            "ddd, dd MMM yyyy HH:mm:ss zzz",
            "ddd, dd MMM yyyy HH:mm:ss zzzz",
            "ddd, d MMM yyyy HH:mm:ss zzz",
            "dd MMM yyyy HH:mm:ss zzz"
        ];

        foreach (string format in formats)
        {
            if (DateTimeOffset.TryParseExact(
                    cleaned,
                    format,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateTimeOffset parsed))
            {
                return parsed;
            }
        }

        if (DateTimeOffset.TryParse(cleaned, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset fallback))
        {
            return fallback;
        }

        return Option<DateTimeOffset>.None;
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
                _logger.LogWarning(ex, "Failed to probe rss download {Path}", path);
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
            _logger.LogWarning(ex, "Failed to delete rss temp file {Path}", path);
        }
    }

    private static async Task<List<RssFeed>> LoadFeeds(
        IConfigElementRepository configRepository,
        CancellationToken cancellationToken)
    {
        Option<string> maybeJson = await configRepository.GetValue<string>(
            ConfigElementKey.RssFeeds,
            cancellationToken);

        foreach (string json in maybeJson.Filter(j => !string.IsNullOrWhiteSpace(j)))
        {
            try
            {
                return JsonSerializer.Deserialize<List<RssFeed>>(json) ?? [];
            }
            catch
            {
                return [];
            }
        }

        return [];
    }

    private async Task<DateTimeOffset> GetWatermark(
        IConfigElementRepository configRepository,
        string feedName,
        CancellationToken cancellationToken)
    {
        Option<string> maybeValue = await configRepository.GetValue<string>(
            ConfigElementKey.RssFeedWatermark(feedName),
            cancellationToken);

        foreach (string value in maybeValue)
        {
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed))
            {
                return parsed;
            }
        }

        DateTimeOffset now = DateTimeOffset.Now;
        _logger.LogInformation(
            "RSS feed {Name}: initialized watermark to {Now}; the existing feed backlog will not air, only episodes published after this",
            feedName,
            now);

        await configRepository.Upsert(
            ConfigElementKey.RssFeedWatermark(feedName),
            now.ToString("O", CultureInfo.InvariantCulture),
            cancellationToken);

        return now;
    }
}
