using ErsatzTV.Core;

namespace ErsatzTV.Application.Navidrome;

public interface ISynchronizeNavidromeLibraryById : IRequest<Either<BaseError, string>>,
    IScannerBackgroundServiceRequest
{
    int NavidromeLibraryId { get; }
    bool ForceScan { get; }
    bool DeepScan { get; }
}

public record SynchronizeNavidromeLibraryByIdIfNeeded(int NavidromeLibraryId) : ISynchronizeNavidromeLibraryById
{
    public bool ForceScan => false;
    public bool DeepScan => false;
}

public record ForceSynchronizeNavidromeLibraryById(int NavidromeLibraryId, bool DeepScan)
    : ISynchronizeNavidromeLibraryById
{
    public bool ForceScan => true;
}
