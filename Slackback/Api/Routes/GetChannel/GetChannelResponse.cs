namespace Api.Routes.GetChannel;

public class GetChannelResponse
{
    public class ChannelResponse
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
    }

    public required ChannelResponse Channel { get; init; }
}