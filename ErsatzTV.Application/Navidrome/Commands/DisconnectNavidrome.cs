using ErsatzTV.Core;

namespace ErsatzTV.Application.Navidrome;

public record DisconnectNavidrome : IRequest<Either<BaseError, Unit>>;
