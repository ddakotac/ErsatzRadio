using Flurl;

namespace ErsatzTV.Core.Audiobookshelf;

public static class AudiobookshelfUrl
{
    /// <summary>
    ///     abs://items/{id}/cover => {address}/api/items/{id}/cover?token={apiKey}
    ///     abs://authors/{id}/image => {address}/api/authors/{id}/image?token={apiKey}
    ///     extra query params on the artwork path (e.g. width) are passed through.
    /// </summary>
    public static Url ForArtwork(string address, string apiKey, string artwork)
    {
        string remainder = artwork.Replace("abs://", string.Empty);
        string[] split = remainder.Split('?');

        Url url = Url.Parse(address)
            .AppendPathSegment("api")
            .AppendPathSegments(split[0].Split('/'))
            .SetQueryParam("token", apiKey);

        if (split.Length == 2)
        {
            url = url.SetQueryParams(Url.ParseQueryParams(split[1]));
        }

        return url;
    }

    /// <summary>abs://items/{id}/cover => abs/items/{id}/cover (relative proxy path)</summary>
    public static Url RelativeProxyForArtwork(string artwork)
    {
        string remainder = artwork.Replace("abs://", string.Empty);
        string[] split = remainder.Split('?');

        Url url = Url.Parse("abs").AppendPathSegments(split[0].Split('/'));

        if (split.Length == 2)
        {
            url = url.SetQueryParams(Url.ParseQueryParams(split[1]));
        }

        return url;
    }
}
