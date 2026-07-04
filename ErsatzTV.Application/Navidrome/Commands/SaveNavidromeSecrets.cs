using ErsatzTV.Core;
using ErsatzTV.Core.Navidrome;

namespace ErsatzTV.Application.Navidrome;

public record SaveNavidromeSecrets(NavidromeSecrets Secrets) : IRequest<Either<BaseError, Unit>>;
