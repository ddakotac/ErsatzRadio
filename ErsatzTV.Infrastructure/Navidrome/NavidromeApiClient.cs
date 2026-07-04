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
        // first pass: page through all albums in the music folder to learn the
        // full album list and total song count
        var albums = new List<SubsonicAlbum>();
        var offset = 0;
        while (true)
        {
            SubsonicResponse response = await GetSubsonicResponse(
                connectionParameters,
                "getAlbumList2",
                new Dictionary<string, string>
                {
                    ["type"] = "alphabeticalByName",
                    ["size"] = AlbumPageSize.ToString(CultureInfo.InvariantCulture),
                    ["offset"] = offset.ToString(CultureInfo.InvariantCulture),
                    ["musicFolderId"] = library.ItemId
                },
                cancellationToken);

            List<SubsonicAlbum> page = Optional(response.AlbumList2)
                .Map(al => al.Album)
                .Flatten()
                .ToList();

            albums.AddRange(page);

            if (page.Count < AlbumPageSize)
            {
                break;
            }

            offset += AlbumPageSize;
        }

        int totalSongCount = albums.Sum(a => a.SongCount);

        // second pass: fetch songs for each album
        foreach (SubsonicAlbum albumStub in albums)
        {
            SubsonicResponse response = await GetSubsonicResponse(
                connectionParameters,
                "getAlbum",
                new Dictionary<string, string> { ["id"] = albumStub.Id },
                cancellationToken);

            List<SubsonicSong> songs = Optional(response.Album)
                .Map(a => a.Song)
                .Flatten()
                .ToList();

            foreach (SubsonicSong song in songs)
            {
                foreach (NavidromeSong navidromeSong in ProjectToSong(song))
                {
                    yield return Tuple(navidromeSong, totalSongCount);
                }
            }
        }
    }

    private Option<NavidromeSong> ProjectToSong(SubsonicSong item)
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
                Streams = [],
                Chapters = []
            };

            string albumArtist = string.IsNullOrWhiteSpace(item.DisplayAlbumArtist)
                ? item.Artist
                : item.DisplayAlbumArtist;

            var metadata = new SongMetadata
            {
                MetadataKind = MetadataKind.External,
                Title = item.Title,
                SortTitle = SortTitle.GetSortTitle(item.Title),
                Album = item.Album,
                Artists = string.IsNullOrWhiteSpace(item.Artist) ? [] : [item.Artist],
                AlbumArtists = string.IsNullOrWhiteSpace(albumArtist) ? [] : [albumArtist],
                Track = item.Track?.ToString(CultureInfo.InvariantCulture),
                Year = item.Year,
                DateAdded = now,
                DateUpdated = now,
                Genres = string.IsNullOrWhiteSpace(item.Genre)
                    ? []
                    : [new Genre { Name = item.Genre }],
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

    private static string ComputeEtag(SubsonicSong item)
    {
        string fingerprint = string.Join(
            '|',
            item.Id,
            item.Title,
            item.Album,
            item.Artist,
            item.DisplayAlbumArtist,
            item.Track?.ToString(CultureInfo.InvariantCulture),
            item.DiscNumber?.ToString(CultureInfo.InvariantCulture),
            item.Year?.ToString(CultureInfo.InvariantCulture),
            item.Genre,
            item.Duration?.ToString(CultureInfo.InvariantCulture),
            item.BitRate?.ToString(CultureInfo.InvariantCulture),
            item.Path);

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint));
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
