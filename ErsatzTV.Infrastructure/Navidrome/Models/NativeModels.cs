using System.Text.Json.Serialization;

namespace ErsatzTV.Infrastructure.Navidrome.Models;

public class NavidromeLoginResponse
{
    [JsonPropertyName("token")]
    public string Token { get; set; }

    [JsonPropertyName("username")]
    public string Username { get; set; }
}

// song shape from navidrome's native rest api (/api/song), which exposes the
// real filesystem path; the subsonic api only reports tag-derived virtual paths
public class NavidromeNativeSong
{
    [JsonConverter(typeof(FlexibleStringConverter))]
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; }

    [JsonPropertyName("album")]
    public string Album { get; set; }

    [JsonPropertyName("artist")]
    public string Artist { get; set; }

    [JsonPropertyName("albumArtist")]
    public string AlbumArtist { get; set; }

    [JsonConverter(typeof(FlexibleStringConverter))]
    [JsonPropertyName("albumId")]
    public string AlbumId { get; set; }

    [JsonConverter(typeof(FlexibleStringConverter))]
    [JsonPropertyName("artistId")]
    public string ArtistId { get; set; }

    [JsonConverter(typeof(FlexibleStringConverter))]
    [JsonPropertyName("libraryId")]
    public string LibraryId { get; set; }

    [JsonPropertyName("trackNumber")]
    public int? TrackNumber { get; set; }

    [JsonPropertyName("discNumber")]
    public int? DiscNumber { get; set; }

    [JsonPropertyName("year")]
    public int? Year { get; set; }

    [JsonPropertyName("genre")]
    public string Genre { get; set; }

    [JsonPropertyName("genres")]
    public List<NavidromeNativeGenre> Genres { get; set; }

    [JsonPropertyName("duration")]
    public double? Duration { get; set; }

    [JsonPropertyName("bitRate")]
    public int? BitRate { get; set; }

    [JsonPropertyName("path")]
    public string Path { get; set; }

    [JsonPropertyName("suffix")]
    public string Suffix { get; set; }
}

public class NavidromeNativeGenre
{
    [JsonConverter(typeof(FlexibleStringConverter))]
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }
}
