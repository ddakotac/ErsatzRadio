using ErsatzTV.Core;

namespace ErsatzTV.Application.Navidrome;

public record UpdateNavidromePathReplacements(
    int NavidromeMediaSourceId,
    List<NavidromePathReplacementItem> PathReplacements) : IRequest<Either<BaseError, Unit>>;

public record NavidromePathReplacementItem(int Id, string NavidromePath, string LocalPath);
