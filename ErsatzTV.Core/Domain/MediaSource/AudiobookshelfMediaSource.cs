namespace ErsatzTV.Core.Domain;

public class AudiobookshelfMediaSource : MediaSource
{
    public string ServerName { get; set; }
    public List<AudiobookshelfConnection> Connections { get; set; }
    public List<AudiobookshelfPathReplacement> PathReplacements { get; set; }
}
