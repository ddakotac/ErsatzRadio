using ErsatzTV.Core;

namespace ErsatzTV.Application.Navidrome;

public record UpdateNavidromeLibraryPreferences
    (List<NavidromeLibraryPreference> Preferences) : IRequest<Either<BaseError, Unit>>;

public record NavidromeLibraryPreference(int Id, bool ShouldSyncItems);
