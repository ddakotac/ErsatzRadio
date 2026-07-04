using System.Runtime.CompilerServices;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Navidrome;
using ErsatzTV.Core.Metadata;
using ErsatzTV.Core.Navidrome;
using ErsatzTV.Infrastructure.Navidrome.Models;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Infrastructure.Navidrome;

public class NavidromeApiClient : INavidromeApiClient
{
    private const string SubsonicApiVersion = "1.16.1";
    private const string SubsonicClientName = "ErsatzRadio";
    private const int AlbumPageSize = 500;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<NavidromeApiClient> _logger;

    public NavidromeApiClient(
        IHttpClientFactory httpClientFactory,
        ILogger<NavidromeApiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<Either<BaseError, NavidromeServerInformation>> GetServerInformation(
        NavidromeConnectionParameters connectionParameters)
    {
        try
        {
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            SubsonicResponse response = await GetSubsonicResponse(
                connectionParameters,
                "ping",
                [],
                cts.Token);

            string serverName = string.IsNullOrWhiteSpace(response.Type) ? "Navidrome" : response.Type;
            string serverVersion = response.ServerVersion ?? response.Version ?? string.Empty;

            return new NavidromeServerInformation(serverName, serverVersion);
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
        {
            _logger.LogError(ex, "Timeout getting navidrome server information");
            return BaseError.New("Navidrome did not respond in time");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting navidrome server information");
            return BaseError.New(ex.Message);
        }
    }

    public async Task<Either<BaseError, List<NavidromeLibrary>>> GetLibraries(
        NavidromeConnectionParameters connectionParameters)
    {
        try
        {
            SubsonicResponse response = await GetSubsonicResponse(
                connectionParameters,
                "getMusicFolders",
                [],
                CancellationToken.None);

            var libraries = Optional(response.MusicFolders)
                .Map(mf => mf.MusicFolder)
                .Flatten()
                .Map(folder => new NavidromeLibrary
                {
                    ItemId = folder.Id,
                    Name = folder.Name,
                    MediaKind = LibraryMediaKind.Songs,
                    ShouldSyncItems = false,
                    Paths = [new LibraryPath { Path = $"navidrome://{folder.Id}" }]
                })
                .ToList();

            return libraries;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting navidrome libraries");
            return BaseError.New(ex.Message);
        }
    }

    public async IAsyncEnumerable<Tuple<NavidromeSong, int>> GetSongLibraryItems(
        NavidromeConnectionParameters connectionParameters,
        NavidromeLibrary library,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // the subsonic api reports tag-derived virtual paths, so we use
        // navidrome's native rest api which exposes real filesystem paths
        string token = await GetNativeToken(connectionParameters, cancellationToken);

        HttpClient client = _httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(connectionParameters.Address);
        client.DefaultRequestHeaders.Add("x-nd-authorization", $"Bearer {token}");

        const int PageSize = 500;
        var offset = 0;
        var totalSongCount = -1;

        while (true)
        {
            string url =
                $"/api/song?_start={offset}&_end={offset + PageSize}&_sort=title&library_id={Uri.EscapeDataString(library.ItemId ?? string.Empty)}";

            using HttpResponseMessage response = await client.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();

            if (totalSongCount < 0 &&
                response.Headers.TryGetValues("x-total-count", out IEnumerable<string> counts) &&
                int.TryParse(counts.FirstOrDefault(), out int parsedCount))
            {
                totalSongCount = parsedCount;
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            List<NavidromeNativeSong> page = await JsonSerializer.DeserializeAsync<List<NavidromeNativeSong>>(
                stream,
                JsonOptions,
                cancellationToken) ?? [];

            if (totalSongCount < 0)
            {
                totalSongCount = page.Count;
            }

            foreach (NavidromeNativeSong song in page)
            {
                // tolerate servers that ignore the library filter
                if (!string.IsNullOrEmpty(song.LibraryId) &&
                    !string.IsNullOrEmpty(library.ItemId) &&
                    song.LibraryId != library.ItemId)
                {
                    continue;
                }

                foreach (NavidromeSong navidromeSong in ProjectToSong(song))
                {
                    yield return Tuple(navidromeSong, Math.Max(totalSongCount, 1));
                }
            }

            if (page.Count < PageSize)
            {
                break;
            }

            offset += PageSize;
        }
    }

    private async Task<string> GetNativeToken(
        NavidromeConnectionParameters connectionParameters,
        CancellationToken cancellationToken)
    {
        HttpClient client = _httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(connectionParameters.Address);

        using var content = new StringContent(
            JsonSerializer.Serialize(
                new { username = connectionParameters.Username, password = connectionParameters.Password }),
            Encoding.UTF8,
            "application/json");

        using HttpResponseMessage response = await client.PostAsync("/auth/login", content, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        NavidromeLoginResponse login = await JsonSerializer.DeserializeAsync<NavidromeLoginResponse>(
            stream,
            JsonOptions,
            cancellationToken);

        return login?.Token ?? throw new InvalidOperationException("Navidrome native login failed");
    }

    private Option<NavidromeSong> ProjectToSong(NavidromeNativeSong item)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(item.Path))
            {
                _logger.LogWarning("Navidrome song {Id} ({Title}) has no path; skipping", item.Id, item.Title);
                return None;
            }

            DateTime now = DateTime.UtcNow;
            var duration = TimeSpan.FromSeconds(item.Duration ?? 0);

            var version = new MediaVersion
            {
                Name = "Main",
                Duration = duration,
                DateAdded = now,
                MediaFiles =
                [
                    new MediaFile
                    {
                        Path = item.Path,
                        PathHash = PathUtils.GetPathHash(item.Path)
                    }
                ],
                Streams =
                [
                    new MediaStream
                    {
                        Index = 0,
                        MediaStreamKind = MediaStreamKind.Audio,
                        Codec = (item.Suffix ?? System.IO.Path.GetExtension(item.Path)?.TrimStart('.') ?? string.Empty)
                            .ToLowerInvariant(),
                        Channels = 2,
                        Default = true
                    }
                ],
                Chapters = []
            };

            string albumArtist = string.IsNullOrWhiteSpace(item.AlbumArtist)
                ? item.Artist
                : item.AlbumArtist;

            var genres = new List<Genre>();
            foreach (NavidromeNativeGenre genre in Optional(item.Genres).Flatten())
            {
                if (!string.IsNullOrWhiteSpace(genre.Name))
                {
                    genres.Add(new Genre { Name = genre.Name });
                }
            }

            if (genres.Count == 0 && !string.IsNullOrWhiteSpace(item.Genre))
            {
                genres.Add(new Genre { Name = item.Genre });
            }

            var metadata = new SongMetadata
            {
                MetadataKind = MetadataKind.External,
                Title = item.Title,
                SortTitle = SortTitle.GetSortTitle(item.Title),
                Album = item.Album,
                Artists = string.IsNullOrWhiteSpace(item.Artist) ? [] : [item.Artist],
                AlbumArtists = string.IsNullOrWhiteSpace(albumArtist) ? [] : [albumArtist],
                Track = item.TrackNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Year = item.Year,
                DateAdded = now,
                DateUpdated = now,
                Genres = genres,
                Tags = [],
                Studios = [],
                Actors = [],
                Artwork = [],
                Guids = []
            };

            var song = new NavidromeSong
            {
                ItemId = item.Id,
                Etag = ComputeEtag(item),
                MediaVersions = [version],
                SongMetadata = [metadata],
                TraktListItems = []
            };

            return song;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error projecting navidrome song {Id}", item.Id);
            return None;
        }
    }

    private static string ComputeEtag(NavidromeNativeSong item)
    {
        string fingerprint = string.Join(
            '|',
            item.Id,
            item.Title,
            item.Album,
            item.Artist,
            item.AlbumArtist,
            item.TrackNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            item.DiscNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            item.Year?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            item.Genre,
            item.Duration?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            item.BitRate?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            item.Path);

        byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(fingerprint));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task<SubsonicResponse> GetSubsonicResponse(
        NavidromeConnectionParameters connectionParameters,
        string endpoint,
        Dictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        HttpClient client = _httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(connectionParameters.Address);

        string url = $"/rest/{endpoint}?{BuildQueryString(connectionParameters, parameters)}";

        using HttpResponseMessage httpResponse = await client.GetAsync(url, cancellationToken);
        httpResponse.EnsureSuccessStatusCode();

        await using Stream stream = await httpResponse.Content.ReadAsStreamAsync(cancellationToken);
        SubsonicResponseWrapper wrapper = await JsonSerializer.DeserializeAsync<SubsonicResponseWrapper>(
            stream,
            JsonOptions,
            cancellationToken);

        SubsonicResponse response = wrapper?.SubsonicResponse ??
                                    throw new InvalidOperationException("Invalid subsonic response");

        if (!string.Equals(response.Status, "ok", StringComparison.OrdinalIgnoreCase))
        {
            string message = response.Error?.Message ?? "unknown subsonic error";
            int code = response.Error?.Code ?? -1;
            throw new InvalidOperationException($"Subsonic error {code}: {message}");
        }

        return response;
    }

    private static string BuildQueryString(
        NavidromeConnectionParameters connectionParameters,
        Dictionary<string, string> parameters)
    {
        string salt = Guid.NewGuid().ToString("N")[..12];
        string token = ComputeToken(connectionParameters.Password, salt);

        var allParameters = new Dictionary<string, string>(parameters)
        {
            ["u"] = connectionParameters.Username,
            ["t"] = token,
            ["s"] = salt,
            ["v"] = SubsonicApiVersion,
            ["c"] = SubsonicClientName,
            ["f"] = "json"
        };

        return string.Join(
            '&',
            allParameters.Map(kvp => $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value ?? string.Empty)}"));
    }

    #pragma warning disable CA5351 // Subsonic token auth requires MD5(password + salt)
    private static string ComputeToken(string password, string salt)
    {
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(password + salt));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
#pragma warning restore CA5351

}
