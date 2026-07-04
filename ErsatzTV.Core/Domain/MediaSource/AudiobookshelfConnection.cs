namespace ErsatzTV.Core.Domain;

public class AudiobookshelfConnection
{
    public int Id { get; set; }
    public string Address { get; set; }
    public int AudiobookshelfMediaSourceId { get; set; }
    public AudiobookshelfMediaSource AudiobookshelfMediaSource { get; set; }
}
