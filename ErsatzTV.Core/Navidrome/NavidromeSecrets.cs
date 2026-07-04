using ErsatzTV.Core.MediaSources;

namespace ErsatzTV.Core.Navidrome;

public class NavidromeSecrets : RemoteMediaSourceSecrets
{
    // RemoteMediaSourceSecrets.ApiKey is used to store the subsonic password
    public string Username { get; set; }
}
