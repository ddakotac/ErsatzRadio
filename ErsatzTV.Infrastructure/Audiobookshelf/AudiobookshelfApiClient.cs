using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ErsatzTV.Core;
using ErsatzTV.Core.Audiobookshelf;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Audiobookshelf;
using ErsatzTV.Core.Metadata;
using ErsatzTV.Infrastructure.Audiobookshelf.Models;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Infrastructure.Audiobookshelf;

public class AudiobookshelfApiClient : IAudiobookshelfApiClient
{
    private const int PageSize = 100;
    private const string BookMediaType = "book";
    private const string PodcastMediaType = "podcast";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AudiobookshelfApiClient> _logger;

    public AudiobookshelfApiClient(
        IHttpClientFactory httpClientFactory,
        ILogger<AudiobookshelfApiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<Either<BaseError, AudiobookshelfServerInformation>> GetServerInformation(
        AudiobookshelfConnectionParameters connectionParameters)
    {
        try
        {
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            AbsStatusResponse status = await GetJson<AbsStatusResponse>(
                connectionParameters,
                "/status",
                cts.Token);

            string serverName = string.IsNullOrWhiteSpace(status?.App) ? "Audiobookshelf" : status.App;
            return new AudiobookshelfServerInformation(serverName, status?.ServerVersion ?? string.Empty);
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
        {
            _logger.LogError(ex, "Timeout getting audiobookshelf server information");
            return BaseError.New("Audiobookshelf did not respond in time");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting audiobookshelf server information");
            return BaseError.New(ex.Message);
        }
    }

    public async Task<Either<BaseError, List<AudiobookshelfLibrary>>> GetLibraries(
        AudiobookshelfConnectionParameters connectionParameters)
    {
        try
        {
            AbsLibrariesResponse response = await GetJson<AbsLibrariesResponse>(
                connectionParameters,
                "/api/libraries",
                CancellationToken.None);

            var libraries = new List<AudiobookshelfLibrary>();
            foreach (AbsLibrary library in Optional(response?.Libraries).Flatten())
            {
                if (library.MediaType is not (BookMediaType or PodcastMediaType))
                {
                    // e.g. ebook-only libraries are not playable
                    continue;
                }

                libraries.Add(
                    new AudiobookshelfLibrary
                    {
                        ItemId = library.Id,
                        Name = library.Name,
                        AbsMediaType = library.MediaType,
                        MediaKind = LibraryMediaKind.Shows,
                        ShouldSyncItems = false,
                        Paths = [new LibraryPath { Path = $"audiobookshelf://{library.Id}" }]
                    });
            }

            return libraries;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting audiobookshelf libraries");
            return BaseError.New(ex.Message);
        }
    }

    public async IAsyncEnumerable<Tuple<AudiobookshelfShow, int>> GetShowLibraryItems(
        AudiobookshelfConnectionParameters connectionParameters,
        AudiobookshelfLibrary library,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (library.AbsMediaType == PodcastMediaType)
        {
            // each podcast is a show
            await foreach ((AbsLibraryItem item, int total) in PageLibraryItems(
                               connectionParameters,
                               library.ItemId,
                               filter: null,
                               cancellationToken))
            {
                foreach (AudiobookshelfShow show in ProjectPodcastToShow(item))
                {
                    yield return Tuple(show, total);
                }
            }
        }
        else
        {
            // each author is a show
            AbsAuthorsResponse response = await GetJson<AbsAuthorsResponse>(
                connectionParameters,
                $"/api/libraries/{library.ItemId}/authors",
                cancellationToken);

            List<AbsAuthor> authors = Optional(response?.Authors).Flatten()
                .Filter(a => a.NumBooks > 0)
                .ToList();

            foreach (AbsAuthor author in authors)
            {
                foreach (AudiobookshelfShow show in ProjectAuthorToShow(author))
                {
                    yield return Tuple(show, Math.Max(authors.Count, 1));
                }
            }
        }
    }

    public async IAsyncEnumerable<Tuple<AudiobookshelfSeason, int>> GetSeasonLibraryItems(
        AudiobookshelfConnectionParameters connectionParameters,
        AudiobookshelfLibrary library,
        AudiobookshelfShow show,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (library.AbsMediaType == PodcastMediaType)
        {
            // single synthetic season per podcast
            string podcastId = show.ItemId.Replace("podcast:", string.Empty);
            var season = new AudiobookshelfSeason
            {
                ItemId = $"{show.ItemId}:s1",
                Etag = show.Etag,
                SeasonNumber = 1,
                SeasonMetadata =
                [
                    new SeasonMetadata
                    {
                        MetadataKind = MetadataKind.External,
                        Title = "Season 1",
                        DateAdded = DateTime.UtcNow,
                        DateUpdated = DateTime.UtcNow,
                        Tags = [],
                        Guids = [],
                        Artwork = []
                    }
                ],
                TraktListItems = []
            };

            yield return Tuple(season, 1);
        }
        else
        {
            // each book by this author is a season; author id is embedded in show item id
            string authorId = show.ItemId.Replace("author:", string.Empty);
            string filter = "authors." + Convert.ToBase64String(Encoding.UTF8.GetBytes(authorId));

            var books = new List<AbsLibraryItem>();
            await foreach ((AbsLibraryItem item, int _) in PageLibraryItems(
                               connectionParameters,
                               library.ItemId,
                               filter,
                               cancellationToken))
            {
                books.Add(item);
            }

            var ordered = books
                .OrderBy(b => b.Media?.Metadata?.PublishedYear ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(b => b.Media?.Metadata?.Title ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (var i = 0; i < ordered.Count; i++)
            {
                AbsLibraryItem book = ordered[i];
                var season = new AudiobookshelfSeason
                {
                    ItemId = book.Id,
                    Etag = ComputeEtag(book.Id, book.UpdatedAt, i + 1),
                    SeasonNumber = i + 1,
                    SeasonMetadata =
                    [
                        new SeasonMetadata
                        {
                            MetadataKind = MetadataKind.External,
                            Title = book.Media?.Metadata?.Title ?? $"Season {i + 1}",
                            DateAdded = FromUnixMs(book.AddedAt),
                            DateUpdated = FromUnixMs(book.UpdatedAt),
                            Tags = [],
                            Guids = [],
                            Artwork = []
                        }
                    ],
                    TraktListItems = []
                };

                yield return Tuple(season, Math.Max(ordered.Count, 1));
            }
        }
    }

    public async IAsyncEnumerable<Tuple<AudiobookshelfEpisode, int>> GetEpisodeLibraryItems(
        AudiobookshelfConnectionParameters connectionParameters,
        AudiobookshelfLibrary library,
        AudiobookshelfSeason season,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (library.AbsMediaType == PodcastMediaType)
        {
            string podcastId = season.ItemId
                .Replace("podcast:", string.Empty)
                .Replace(":s1", string.Empty);

            AbsExpandedItemResponse item = await GetJson<AbsExpandedItemResponse>(
                connectionParameters,
                $"/api/items/{podcastId}?expanded=1",
                cancellationToken);

            List<AbsPodcastEpisode> episodes = Optional(item?.Media?.Episodes).Flatten()
                .Filter(e => !string.IsNullOrWhiteSpace(e.AudioFile?.Metadata?.Path))
                .OrderBy(e => e.PublishedAt ?? 0)
                .ToList();

            var number = 0;
            foreach (AbsPodcastEpisode episode in episodes)
            {
                number++;
                foreach (AudiobookshelfEpisode projected in ProjectPodcastEpisode(item, episode, number))
                {
                    yield return Tuple(projected, Math.Max(episodes.Count, 1));
                }
            }
        }
        else
        {
            // season item id IS the book library item id; episodes are audio tracks
            AbsExpandedItemResponse book = await GetJson<AbsExpandedItemResponse>(
                connectionParameters,
                $"/api/items/{season.ItemId}?expanded=1",
                cancellationToken);

            List<AbsAudioFile> tracks = Optional(book?.Media?.AudioFiles).Flatten()
                .Filter(f => !string.IsNullOrWhiteSpace(f.Metadata?.Path))
                .OrderBy(f => f.Index ?? int.MaxValue)
                .ThenBy(f => f.Metadata?.Filename ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var number = 0;
            foreach (AbsAudioFile track in tracks)
            {
                number++;
                foreach (AudiobookshelfEpisode projected in ProjectTrackToEpisode(book, track, number))
                {
                    yield return Tuple(projected, Math.Max(tracks.Count, 1));
                }
            }
        }
    }

    private Option<AudiobookshelfShow> ProjectAuthorToShow(AbsAuthor author)
    {
        try
        {
            var metadata = new ShowMetadata
            {
                MetadataKind = MetadataKind.External,
                Title = author.Name,
                SortTitle = SortTitle.GetSortTitle(author.Name),
                Plot = author.Description,
                DateAdded = FromUnixMs(author.AddedAt),
                DateUpdated = FromUnixMs(author.UpdatedAt),
                Genres = [],
                Tags = [new Tag { Name = "audiobook-author" }],
                Studios = [],
                Actors = [],
                Artwork = [],
                Guids = []
            };

            return new AudiobookshelfShow
            {
                ItemId = $"author:{author.Id}",
                Etag = ComputeEtag(author.Id, author.UpdatedAt, author.NumBooks),
                ShowMetadata = [metadata],
                TraktListItems = []
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error projecting audiobookshelf author {Id}", author.Id);
            return None;
        }
    }

    private Option<AudiobookshelfShow> ProjectPodcastToShow(AbsLibraryItem item)
    {
        try
        {
            string podcastType = item.Media?.Metadata?.Type ?? "episodic";

            var metadata = new ShowMetadata
            {
                MetadataKind = MetadataKind.External,
                Title = item.Media?.Metadata?.Title ?? "Podcast",
                SortTitle = SortTitle.GetSortTitle(item.Media?.Metadata?.Title ?? "Podcast"),
                Plot = item.Media?.Metadata?.Description,
                DateAdded = FromUnixMs(item.AddedAt),
                DateUpdated = FromUnixMs(item.UpdatedAt),
                Genres = Optional(item.Media?.Metadata?.Genres).Flatten()
                    .Filter(g => !string.IsNullOrWhiteSpace(g))
                    .Map(g => new Genre { Name = g })
                    .ToList(),
                Tags =
                [
                    new Tag { Name = "podcast" },
                    new Tag { Name = $"podcast-{podcastType}" }
                ],
                Studios = [],
                Actors = [],
                Artwork = [],
                Guids = []
            };

            return new AudiobookshelfShow
            {
                ItemId = $"podcast:{item.Id}",
                Etag = ComputeEtag(item.Id, item.UpdatedAt, item.Media?.NumEpisodes ?? 0),
                ShowMetadata = [metadata],
                TraktListItems = []
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error projecting audiobookshelf podcast {Id}", item.Id);
            return None;
        }
    }

    private Option<AudiobookshelfEpisode> ProjectTrackToEpisode(
        AbsLibraryItem book,
        AbsAudioFile track,
        int episodeNumber)
    {
        try
        {
            string bookTitle = book.Media?.Metadata?.Title ?? "Audiobook";
            string title = Path.GetFileNameWithoutExtension(track.Metadata?.Filename);
            if (string.IsNullOrWhiteSpace(title))
            {
                title = $"{bookTitle} - Part {episodeNumber}";
            }

            // book tracks always carry an index in practice; positional fallback keeps
            // null-index files from colliding on itemId
            int trackIndex = track.Index ?? episodeNumber;

            return ProjectEpisode(
                itemId: $"{book.Id}:t{trackIndex}",
                etag: ComputeEtag(book.Id, book.UpdatedAt, trackIndex, track.Metadata?.Path),
                title: title,
                episodeNumber: episodeNumber,
                path: track.Metadata.Path,
                duration: track.Duration,
                codec: track.Codec,
                releaseDate: null,
                plot: null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error projecting audiobookshelf track {Book}/{Index}", book.Id, track.Index);
            return None;
        }
    }

    private Option<AudiobookshelfEpisode> ProjectPodcastEpisode(
        AbsLibraryItem podcast,
        AbsPodcastEpisode episode,
        int episodeNumber)
    {
        try
        {
            DateTime? releaseDate = episode.PublishedAt.HasValue
                ? FromUnixMs(episode.PublishedAt)
                : null;

            return ProjectEpisode(
                itemId: $"pe:{episode.Id}",
                etag: ComputeEtag(episode.Id, episode.UpdatedAt ?? podcast.UpdatedAt, episodeNumber,
                    episode.AudioFile?.Metadata?.Path),
                title: episode.Title ?? $"Episode {episodeNumber}",
                episodeNumber: episodeNumber,
                path: episode.AudioFile.Metadata.Path,
                duration: episode.AudioFile.Duration,
                codec: episode.AudioFile.Codec,
                releaseDate: releaseDate,
                plot: episode.Description);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error projecting audiobookshelf podcast episode {Id}", episode.Id);
            return None;
        }
    }

    private static AudiobookshelfEpisode ProjectEpisode(
        string itemId,
        string etag,
        string title,
        int episodeNumber,
        string path,
        double? duration,
        string codec,
        DateTime? releaseDate,
        string plot)
    {
        DateTime now = DateTime.UtcNow;

        var version = new MediaVersion
        {
            Name = "Main",
            Duration = TimeSpan.FromSeconds(duration ?? 0),
            DateAdded = now,
            MediaFiles =
            [
                new MediaFile
                {
                    Path = path,
                    PathHash = PathUtils.GetPathHash(path)
                }
            ],
            Streams =
            [
                new MediaStream
                {
                    Index = 0,
                    MediaStreamKind = MediaStreamKind.Audio,
                    Codec = (codec ?? Path.GetExtension(path)?.TrimStart('.') ?? string.Empty).ToLowerInvariant(),
                    Channels = 2,
                    Default = true
                }
            ],
            Chapters = []
        };

        var metadata = new EpisodeMetadata
        {
            MetadataKind = MetadataKind.External,
            EpisodeNumber = episodeNumber,
            Title = title,
            SortTitle = SortTitle.GetSortTitle(title),
            Plot = plot,
            ReleaseDate = releaseDate,
            Year = releaseDate?.Year,
            DateAdded = now,
            DateUpdated = now,
            Genres = [],
            Tags = [],
            Studios = [],
            Actors = [],
            Directors = [],
            Writers = [],
            Artwork = [],
            Guids = []
        };

        return new AudiobookshelfEpisode
        {
            ItemId = itemId,
            Etag = etag,
            MediaVersions = [version],
            EpisodeMetadata = [metadata],
            TraktListItems = []
        };
    }

    private async IAsyncEnumerable<Tuple<AbsLibraryItem, int>> PageLibraryItems(
        AudiobookshelfConnectionParameters connectionParameters,
        string libraryId,
        string filter,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var page = 0;
        var total = -1;
        while (true)
        {
            string url = $"/api/libraries/{libraryId}/items?limit={PageSize}&page={page}";
            if (!string.IsNullOrWhiteSpace(filter))
            {
                url += $"&filter={Uri.EscapeDataString(filter)}";
            }

            AbsItemsResponse response = await GetJson<AbsItemsResponse>(
                connectionParameters,
                url,
                cancellationToken);

            List<AbsLibraryItem> results = Optional(response?.Results).Flatten().ToList();
            if (total < 0)
            {
                total = response?.Total ?? results.Count;
            }

            foreach (AbsLibraryItem item in results)
            {
                yield return Tuple(item, Math.Max(total, 1));
            }

            if (results.Count < PageSize)
            {
                break;
            }

            page++;
        }
    }

    private async Task<T> GetJson<T>(
        AudiobookshelfConnectionParameters connectionParameters,
        string relativeUrl,
        CancellationToken cancellationToken)
    {
        HttpClient client = _httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(connectionParameters.Address);
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {connectionParameters.ApiKey}");

        using HttpResponseMessage response = await client.GetAsync(relativeUrl, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
    }

    private static DateTime FromUnixMs(long? unixMs) =>
        unixMs.HasValue
            ? DateTimeOffset.FromUnixTimeMilliseconds(unixMs.Value).UtcDateTime
            : DateTime.UtcNow;

    private static string ComputeEtag(params object[] parts)
    {
        string fingerprint = string.Join(
            '|',
            parts.Map(p => p?.ToString() ?? string.Empty));

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
