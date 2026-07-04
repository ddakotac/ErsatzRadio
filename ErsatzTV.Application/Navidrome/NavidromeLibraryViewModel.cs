using ErsatzTV.Application.Libraries;
using ErsatzTV.Core.Domain;

namespace ErsatzTV.Application.Navidrome;

public record NavidromeLibraryViewModel(
    int Id,
    string Name,
    LibraryMediaKind MediaKind,
    bool ShouldSyncItems,
    int MediaSourceId)
    : LibraryViewModel("Navidrome", Id, Name, MediaKind, MediaSourceId, string.Empty);
