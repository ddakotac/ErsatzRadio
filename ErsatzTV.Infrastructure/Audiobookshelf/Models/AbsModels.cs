using System.Text.Json.Serialization;

namespace ErsatzTV.Infrastructure.Audiobookshelf.Models;

public class AbsStatusResponse
{
    [JsonPropertyName("app")]
    public string App { get; set; }

    [JsonPropertyName("serverVersion")]
    public string ServerVersion { get; set; }
}

public class AbsLibrariesResponse
{
    [JsonPropertyName("libraries")]
    public List<AbsLibrary> Libraries { get; set; } = [];
}

public class AbsLibrary
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("mediaType")]
    public string MediaType { get; set; }
}

public class AbsAuthorsResponse
{
    [JsonPropertyName("authors")]
    public List<AbsAuthor> Authors { get; set; } = [];
}

public class AbsAuthor
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; }

    [JsonPropertyName("numBooks")]
    public int NumBooks { get; set; }

    [JsonPropertyName("updatedAt")]
    public long? UpdatedAt { get; set; }

    [JsonPropertyName("addedAt")]
    public long? AddedAt { get; set; }
}

public class AbsItemsResponse
{
    [JsonPropertyName("results")]
    public List<AbsLibraryItem> Results { get; set; } = [];

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("page")]
    public int Page { get; set; }

    [JsonPropertyName("limit")]
    public int Limit { get; set; }
}

public class AbsLibraryItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("path")]
    public string Path { get; set; }

    [JsonPropertyName("addedAt")]
    public long? AddedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public long? UpdatedAt { get; set; }

    [JsonPropertyName("media")]
    public AbsMedia Media { get; set; }
}

public class AbsMedia
{
    [JsonPropertyName("metadata")]
    public AbsMediaMetadata Metadata { get; set; }

    [JsonPropertyName("duration")]
    public double? Duration { get; set; }

    [JsonPropertyName("numTracks")]
    public int? NumTracks { get; set; }

    [JsonPropertyName("numEpisodes")]
    public int? NumEpisodes { get; set; }

    [JsonPropertyName("audioFiles")]
    public List<AbsAudioFile> AudioFiles { get; set; }

    [JsonPropertyName("episodes")]
    public List<AbsPodcastEpisode> Episodes { get; set; }

    [JsonPropertyName("chapters")]
    public List<AbsChapter> Chapters { get; set; }

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; }
}

public class AbsMediaMetadata
{
    [JsonPropertyName("title")]
    public string Title { get; set; }

    [JsonPropertyName("authorName")]
    public string AuthorName { get; set; }

    [JsonPropertyName("author")]
    public string Author { get; set; }

    [JsonPropertyName("seriesName")]
    public string SeriesName { get; set; }

    [JsonPropertyName("genres")]
    public List<string> Genres { get; set; }

    [JsonPropertyName("publishedYear")]
    [JsonConverter(typeof(AbsFlexibleStringConverter))]
    public string PublishedYear { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; }

    // podcasts: "episodic" or "serial"
    [JsonPropertyName("type")]
    public string Type { get; set; }
}

public class AbsAudioFile
{
    // book tracks populate this; podcast episode audio files return null
    [JsonPropertyName("index")]
    public int? Index { get; set; }

    [JsonPropertyName("metadata")]
    public AbsFileMetadata Metadata { get; set; }

    [JsonPropertyName("duration")]
    public double? Duration { get; set; }

    [JsonPropertyName("bitRate")]
    public int? BitRate { get; set; }

    [JsonPropertyName("codec")]
    public string Codec { get; set; }

    [JsonPropertyName("mimeType")]
    public string MimeType { get; set; }
}

public class AbsFileMetadata
{
    [JsonPropertyName("filename")]
    public string Filename { get; set; }

    [JsonPropertyName("path")]
    public string Path { get; set; }

    [JsonPropertyName("ext")]
    public string Ext { get; set; }
}

public class AbsPodcastEpisode
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("index")]
    public int? Index { get; set; }

    [JsonPropertyName("season")]
    [JsonConverter(typeof(AbsFlexibleStringConverter))]
    public string Season { get; set; }

    [JsonPropertyName("episode")]
    [JsonConverter(typeof(AbsFlexibleStringConverter))]
    public string Episode { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; }

    [JsonPropertyName("publishedAt")]
    public long? PublishedAt { get; set; }

    [JsonPropertyName("addedAt")]
    public long? AddedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public long? UpdatedAt { get; set; }

    [JsonPropertyName("audioFile")]
    public AbsAudioFile AudioFile { get; set; }
}

public class AbsChapter
{
    [JsonPropertyName("start")]
    public double? Start { get; set; }

    [JsonPropertyName("end")]
    public double? End { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; }
}

public class AbsExpandedItemResponse : AbsLibraryItem
{
}

public class AbsBookGroupsResponse
{
    [JsonPropertyName("results")]
    public List<AbsBookGroup> Results { get; set; }

    [JsonPropertyName("total")]
    public int? Total { get; set; }
}

public class AbsBookGroup
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("books")]
    public List<AbsBookGroupBook> Books { get; set; }
}

public class AbsBookGroupBook
{
    [JsonPropertyName("id")]
    public string Id { get; set; }
}

/// <summary>
/// abs is inconsistent about numeric vs string fields across versions
/// </summary>
public class AbsFlexibleStringConverter : System.Text.Json.Serialization.JsonConverter<string>
{
    public override string Read(
        ref System.Text.Json.Utf8JsonReader reader,
        Type typeToConvert,
        System.Text.Json.JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            System.Text.Json.JsonTokenType.Number => reader.TryGetInt64(out long l)
                ? l.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
            System.Text.Json.JsonTokenType.String => reader.GetString(),
            System.Text.Json.JsonTokenType.Null => null,
            _ => throw new System.Text.Json.JsonException($"Unexpected token {reader.TokenType} for string")
        };

    public override void Write(
        System.Text.Json.Utf8JsonWriter writer,
        string value,
        System.Text.Json.JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}
