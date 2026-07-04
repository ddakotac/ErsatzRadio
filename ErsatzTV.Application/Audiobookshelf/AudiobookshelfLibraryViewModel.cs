using ErsatzTV.Application.Libraries;
using ErsatzTV.Core.Domain;

namespace ErsatzTV.Application.Audiobookshelf;

public record AudiobookshelfLibraryViewModel(
    int Id,
    string Name,
    LibraryMediaKind MediaKind,
    bool ShouldSyncItems,
    int MediaSourceId)
    : LibraryViewModel("Audiobookshelf", Id, Name, MediaKind, MediaSourceId, string.Empty);
