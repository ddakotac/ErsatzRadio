using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Iptv;

namespace ErsatzTV.Application.Channels;

public class GetChannelPlaylistHandler(
    IChannelRepository channelRepository,
    IConfigElementRepository configElementRepository)
    : IRequestHandler<GetChannelPlaylist, ChannelPlaylist>
{
    public async Task<ChannelPlaylist> Handle(GetChannelPlaylist request, CancellationToken cancellationToken)
    {
        List<Channel> channels = EnsureMode(await channelRepository.GetAll(cancellationToken), request.Mode);

        Dictionary<string, string> radioFormats =
            await GetRadioFormats(channels, configElementRepository, cancellationToken);

        return new ChannelPlaylist(
            request.Scheme,
            request.Host,
            request.BaseUrl,
            channels,
            request.UserAgent,
            request.AccessToken,
            radioFormats);
    }

    private static async Task<Dictionary<string, string>> GetRadioFormats(
        IEnumerable<Channel> channels,
        IConfigElementRepository configElementRepository,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>();

        foreach (Channel channel in channels.Where(c => c.SongVideoMode == ChannelSongVideoMode.AudioOnly))
        {
            Option<string> maybeFormat = await configElementRepository.GetValue<string>(
                ConfigElementKey.RadioFormat(channel.Number),
                cancellationToken);

            string format = maybeFormat
                .Filter(f => string.Equals(f, "mp3", StringComparison.OrdinalIgnoreCase))
                .Map(_ => "mp3")
                .IfNone("aac");

            result.Add(channel.Number, format);
        }

        return result;
    }

    private static List<Channel> EnsureMode(IEnumerable<Channel> channels, string mode)
    {
        var result = new List<Channel>();
        foreach (Channel channel in channels)
        {
            if (!channel.IsEnabled)
            {
                continue;
            }

            switch (mode.ToLowerInvariant())
            {
                case "segmenter":
                    channel.StreamingMode = StreamingMode.HttpLiveStreamingSegmenter;
                    result.Add(channel);
                    break;
                case "hls-direct":
                    channel.StreamingMode = StreamingMode.HttpLiveStreamingDirect;
                    result.Add(channel);
                    break;
                case "ts-legacy":
                    channel.StreamingMode = StreamingMode.TransportStream;
                    result.Add(channel);
                    break;
                case "ts":
                    channel.StreamingMode = StreamingMode.TransportStreamHybrid;
                    result.Add(channel);
                    break;
                default:
                    result.Add(channel);
                    break;
            }
        }

        return result;
    }
}
