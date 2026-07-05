using System.Security.Cryptography;
using System.Text;
using Flurl;

namespace ErsatzTV.Core.Navidrome;

public static class NavidromeUrl
{
    private const string SubsonicApiVersion = "1.16.1";
    private const string SubsonicClientName = "ersatztv";

    /// <summary>
    ///     navidrome://{coverArtId} => {address}/rest/getCoverArt?id={coverArtId}&amp;[subsonic auth]
    ///     extra query params on the artwork path (e.g. size) are passed through.
    /// </summary>
    public static Url ForCoverArt(string address, string username, string password, string artwork)
    {
        string remainder = artwork.Replace("navidrome://", string.Empty);
        string[] split = remainder.Split('?');
        string coverArtId = split[0];

        string salt = Guid.NewGuid().ToString("N")[..12];
        string token = ComputeToken(password ?? string.Empty, salt);

        Url url = Url.Parse(address)
            .AppendPathSegments("rest", "getCoverArt")
            .SetQueryParam("id", coverArtId)
            .SetQueryParam("u", username)
            .SetQueryParam("t", token)
            .SetQueryParam("s", salt)
            .SetQueryParam("v", SubsonicApiVersion)
            .SetQueryParam("c", SubsonicClientName);

        if (split.Length == 2)
        {
            url = url.SetQueryParams(Url.ParseQueryParams(split[1]));
        }

        return url;
    }

    /// <summary>navidrome://{coverArtId} => navidrome/{coverArtId} (relative proxy path)</summary>
    public static Url RelativeProxyForArtwork(string artwork)
    {
        string remainder = artwork.Replace("navidrome://", string.Empty);
        string[] split = remainder.Split('?');

        Url url = Url.Parse("navidrome").AppendPathSegment(split[0]);

        if (split.Length == 2)
        {
            url = url.SetQueryParams(Url.ParseQueryParams(split[1]));
        }

        return url;
    }

#pragma warning disable CA5351 // Subsonic token auth requires MD5(password + salt)
    private static string ComputeToken(string password, string salt)
    {
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(password + salt));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
#pragma warning restore CA5351
}
