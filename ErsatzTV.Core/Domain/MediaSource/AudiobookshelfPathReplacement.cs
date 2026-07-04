namespace ErsatzTV.Core.Domain;

public class AudiobookshelfPathReplacement
{
    public int Id { get; set; }
    public string AudiobookshelfPath { get; set; }
    public string LocalPath { get; set; }
    public int AudiobookshelfMediaSourceId { get; set; }
    public AudiobookshelfMediaSource AudiobookshelfMediaSource { get; set; }
}
