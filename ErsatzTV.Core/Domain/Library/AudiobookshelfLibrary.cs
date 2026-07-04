namespace ErsatzTV.Core.Domain;

public class AudiobookshelfLibrary : Library
{
    public string ItemId { get; set; }
    public bool ShouldSyncItems { get; set; }

    // "book" or "podcast"
    public string AbsMediaType { get; set; }
}
