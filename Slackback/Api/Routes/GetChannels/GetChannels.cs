using Dapper;
using FastEndpoints;
using Npgsql;

namespace Api.Routes.GetChannels;

public class GetChannels(NpgsqlDataSource db) : EndpointWithoutRequest<GetChannelsResponse>
{
    public override void Configure()
    {
        Get("/channels");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);

        var channels = await conn.QueryAsync<GetChannelsResponse.ChannelResponse>(
            new CommandDefinition(
                """
                SELECT DISTINCT ON (public_id)
                    public_id AS Id,
                    data->>'name' AS Name
                FROM channel
                ORDER BY public_id, timestamp DESC
                """,
                cancellationToken: ct
            )
        );

        await SendAsync(
            new GetChannelsResponse { Channels = channels.ToArray() },
            cancellation: ct
        );
    }
}
