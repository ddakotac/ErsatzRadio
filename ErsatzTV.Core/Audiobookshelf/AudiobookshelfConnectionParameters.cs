using ErsatzTV.Core.Domain.MediaServer;

namespace ErsatzTV.Core.Audiobookshelf;

public record AudiobookshelfConnectionParameters(string Address, string ApiKey, int MediaSourceId)
    : MediaServerConnectionParameters;
