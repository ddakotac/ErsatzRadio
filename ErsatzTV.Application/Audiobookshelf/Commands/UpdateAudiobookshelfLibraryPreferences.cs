using ErsatzTV.Core;

namespace ErsatzTV.Application.Audiobookshelf;

public record UpdateAudiobookshelfLibraryPreferences
    (List<AudiobookshelfLibraryPreference> Preferences) : IRequest<Either<BaseError, Unit>>;

public record AudiobookshelfLibraryPreference(int Id, bool ShouldSyncItems);
