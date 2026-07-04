namespace ErsatzTV.Core.Domain;

public class NavidromeMediaSource : MediaSource
{
    public string ServerName { get; set; }
    public List<NavidromeConnection> Connections { get; set; }
    public List<NavidromePathReplacement> PathReplacements { get; set; }
}
