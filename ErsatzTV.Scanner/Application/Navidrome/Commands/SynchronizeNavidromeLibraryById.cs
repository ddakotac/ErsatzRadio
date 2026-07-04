using ErsatzTV.Core;

namespace ErsatzTV.Scanner.Application.Navidrome;

public record SynchronizeNavidromeLibraryById(
    string BaseUrl,
    int NavidromeLibraryId,
    bool ForceScan,
    bool DeepScan) : IRequest<Either<BaseError, string>>;
