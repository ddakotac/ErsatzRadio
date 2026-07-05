using ErsatzTV.Core;

namespace ErsatzTV.Application.Audiobookshelf;

public record DisconnectAudiobookshelf : IRequest<Either<BaseError, Unit>>;
