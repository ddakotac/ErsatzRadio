namespace ErsatzTV.Core.Domain;

public class NavidromePathReplacement
{
    public int Id { get; set; }

    // prefix of the (relative) path reported by the subsonic api;
    // an empty value matches all songs in the library and prepends LocalPath
    public string NavidromePath { get; set; }
    public string LocalPath { get; set; }
    public int NavidromeMediaSourceId { get; set; }
    public NavidromeMediaSource NavidromeMediaSource { get; set; }
}
