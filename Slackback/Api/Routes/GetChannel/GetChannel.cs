using FastEndpoints;

namespace Api.Routes.GetChannel;

public class GetChannel : EndpointWithoutRequest<GetChannelResponse>
{    
    public override void Configure()
    {
        Get("/channels/{ChannelId}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var channelId = Route<string>("ChannelId", isRequired: true)!;

        // TODO
        
        await SendAsync(
            new GetChannelResponse
            {
                Channel = new GetChannelResponse.ChannelResponse
                {
                    Id = channelId,
                    Name = "general"
                }
            },
            cancellation: ct
        );
    }
}