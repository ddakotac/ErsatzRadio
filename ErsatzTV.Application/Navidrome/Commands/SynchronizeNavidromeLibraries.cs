using ErsatzTV.Core;

namespace ErsatzTV.Application.Navidrome;

public record SynchronizeNavidromeLibraries(int NavidromeMediaSourceId) : IRequest<Either<BaseError, Unit>>,
    IScannerBackgroundServiceRequest;
