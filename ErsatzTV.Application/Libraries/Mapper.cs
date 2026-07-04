using ErsatzTV.Application.Emby;
using ErsatzTV.Application.Audiobookshelf;
using ErsatzTV.Application.Navidrome;
using ErsatzTV.Application.Jellyfin;
using ErsatzTV.Core.Domain;

namespace ErsatzTV.Application.Libraries;

internal static class Mapper
{
    public static LibraryViewModel ProjectToViewModel(Library library) =>
        library switch
        {
            LocalLibrary l => ProjectToViewModel(l),
            PlexLibrary p => new PlexLibraryViewModel(
                p.Id,
                p.Name,
                p.MediaKind,
                p.MediaSourceId,
                GetServerName(p.MediaSource)),
            JellyfinLibrary j => new JellyfinLibraryViewModel(
                j.Id,
                j.Name,
                j.MediaKind,
                j.ShouldSyncItems,
                j.MediaSourceId),
            EmbyLibrary e => new EmbyLibraryViewModel(e.Id, e.Name, e.MediaKind, e.ShouldSyncItems, e.MediaSourceId),
            NavidromeLibrary n => new NavidromeLibraryViewModel(
                n.Id,
                n.Name,
                n.MediaKind,
                n.ShouldSyncItems,
                n.MediaSourceId),
            AudiobookshelfLibrary a => new AudiobookshelfLibraryViewModel(
                a.Id,
                a.Name,
                a.MediaKind,
                a.ShouldSyncItems,
                a.MediaSourceId),
            _ => throw new ArgumentOutOfRangeException(nameof(library))
        };

    public static LocalLibraryViewModel ProjectToViewModel(LocalLibrary library) =>
        new(library.Id, library.Name, library.MediaKind, library.MediaSourceId);

    public static LocalLibraryPathViewModel ProjectToViewModel(LibraryPath libraryPath) =>
        new(libraryPath.Id, libraryPath.LibraryId, libraryPath.Path);

    private static string GetServerName(MediaSource ms) =>
        ms switch
        {
            PlexMediaSource pms => pms.ServerName,
            _ => string.Empty
        };
}
