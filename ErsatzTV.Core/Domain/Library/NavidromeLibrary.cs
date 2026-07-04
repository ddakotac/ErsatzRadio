namespace ErsatzTV.Core.Domain;

public class NavidromeLibrary : Library
{
    // subsonic music folder id
    public string ItemId { get; set; }
    public bool ShouldSyncItems { get; set; }
}
