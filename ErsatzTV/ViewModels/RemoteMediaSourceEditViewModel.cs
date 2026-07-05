namespace ErsatzTV.ViewModels;

public class RemoteMediaSourceEditViewModel
{
    public string Address { get; set; }
    public string Username { get; set; }
    public string ApiKey { get; set; }

    /// <summary>Set by the editor component when the media source requires a username (e.g. Navidrome).</summary>
    public bool RequireUsername { get; set; }
}
