using System.Text.Json.Serialization;

namespace ErsatzTV.Infrastructure.Navidrome.Models;

public class SubsonicResponseWrapper
{
    [JsonPropertyName("subsonic-response")]
    public SubsonicResponse SubsonicResponse { get; set; }
}

public class SubsonicResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; }

    [JsonPropertyName("serverVersion")]
    public string ServerVersion { get; set; }

    [JsonPropertyName("error")]
    public SubsonicError Error { get; set; }

    [JsonPropertyName("musicFolders")]
    public SubsonicMusicFolders MusicFolders { get; set; }

    [JsonPropertyName("albumList2")]
    public SubsonicAlbumList2 AlbumList2 { get; set; }

    [JsonPropertyName("album")]
    public SubsonicAlbum Album { get; set; }
}

public class SubsonicError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; }
}

public class SubsonicMusicFolders
{
    [JsonPropertyName("musicFolder")]
    public List<SubsonicMusicFolder> MusicFolder { get; set; } = [];
}

public class SubsonicMusicFolder
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }
}

public class SubsonicAlbumList2
{
    [JsonPropertyName("album")]
    public List<SubsonicAlbum> Album { get; set; } = [];
}

public class SubsonicAlbum
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("artist")]
    public string Artist { get; set; }

    [JsonPropertyName("artistId")]
    public string ArtistId { get; set; }

    [JsonPropertyName("year")]
    public int? Year { get; set; }

    [JsonPropertyName("genre")]
    public string Genre { get; set; }

    [JsonPropertyName("songCount")]
    public int SongCount { get; set; }

    [JsonPropertyName("song")]
    public List<SubsonicSong> Song { get; set; } = [];
}

public class SubsonicSong
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; }

    [JsonPropertyName("album")]
    public string Album { get; set; }

    [JsonPropertyName("artist")]
    public string Artist { get; set; }

    [JsonPropertyName("albumId")]
    public string AlbumId { get; set; }

    [JsonPropertyName("artistId")]
    public string ArtistId { get; set; }

    [JsonPropertyName("track")]
    public int? Track { get; set; }

    [JsonPropertyName("discNumber")]
    public int? DiscNumber { get; set; }

    [JsonPropertyName("year")]
    public int? Year { get; set; }

    [JsonPropertyName("genre")]
    public string Genre { get; set; }

    [JsonPropertyName("duration")]
    public int? Duration { get; set; }

    [JsonPropertyName("bitRate")]
    public int? BitRate { get; set; }

    [JsonPropertyName("path")]
    public string Path { get; set; }

    [JsonPropertyName("suffix")]
    public string Suffix { get; set; }

    [JsonPropertyName("contentType")]
    public string ContentType { get; set; }

    [JsonPropertyName("displayAlbumArtist")]
    public string DisplayAlbumArtist { get; set; }

    [JsonPropertyName("sortName")]
    public string SortName { get; set; }
}
