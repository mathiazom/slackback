using FastEndpoints;

namespace Api.Routes.GetChannels;

public class GetChannels : EndpointWithoutRequest<GetChannelsResponse>
{
    public override void Configure()
    {
        Get("/channels");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        // TODO
        
        await SendAsync(
            new GetChannelsResponse
            {
                Channels =
                [
                    new GetChannelsResponse.ChannelResponse
                    {
                        Id = "UXXXXXX01",
                        Name = "general"
                    },
                    new GetChannelsResponse.ChannelResponse
                    {
                        Id = "UXXXXXX02",
                        Name = "random"
                    },
                    new GetChannelsResponse.ChannelResponse
                    {
                        Id = "UXXXXXX03",
                        Name = "happenings"
                    },
                    new GetChannelsResponse.ChannelResponse
                    {
                        Id = "UXXXXXX04",
                        Name = "katt"
                    },
                ]
            },
            cancellation: ct
        );
    }
}