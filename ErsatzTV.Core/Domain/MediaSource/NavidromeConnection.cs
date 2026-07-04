namespace ErsatzTV.Core.Domain;

public class NavidromeConnection
{
    public int Id { get; set; }
    public string Address { get; set; }
    public int NavidromeMediaSourceId { get; set; }
    public NavidromeMediaSource NavidromeMediaSource { get; set; }
}
