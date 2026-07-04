using ErsatzTV.Core.Domain.MediaServer;

namespace ErsatzTV.Core.Navidrome;

public record NavidromeConnectionParameters(string Address, string Username, string Password, int MediaSourceId)
    : MediaServerConnectionParameters;
