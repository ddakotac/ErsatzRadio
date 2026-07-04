using ErsatzTV.Core;
using ErsatzTV.Core.Audiobookshelf;

namespace ErsatzTV.Application.Audiobookshelf;

public record SaveAudiobookshelfSecrets(AudiobookshelfSecrets Secrets) : IRequest<Either<BaseError, Unit>>;
