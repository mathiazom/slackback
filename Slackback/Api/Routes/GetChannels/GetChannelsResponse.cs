namespace Api.Routes.GetChannels;

public class GetChannelsResponse
{
    public class ChannelResponse
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
    }

    public required GetChannelsResponse.ChannelResponse[] Channels { get; init; }
}