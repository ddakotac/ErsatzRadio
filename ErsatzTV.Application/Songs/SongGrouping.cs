namespace ErsatzTV.Application.Songs;

internal static class SongGrouping
{
    /// <summary>album artist when present, otherwise first artist</summary>
    internal static string EffectiveArtist(IList<string> albumArtists, IList<string> artists)
    {
        foreach (string albumArtist in Optional(albumArtists).Flatten()
                     .Filter(a => !string.IsNullOrWhiteSpace(a))
                     .HeadOrNone())
        {
            return albumArtist;
        }

        return Optional(artists).Flatten()
            .Filter(a => !string.IsNullOrWhiteSpace(a))
            .HeadOrNone()
            .IfNone(string.Empty);
    }
}
