using Dapper;
using FastEndpoints;
using Npgsql;

namespace Api.Routes.GetStats;

public class GetStats(NpgsqlDataSource db) : EndpointWithoutRequest<GetStatsResponse>
{
    public override void Configure()
    {
        Get("/stats");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);

        var stats = await conn.QuerySingleAsync<StatsRow>(
            new CommandDefinition(
                """
                SELECT
                    count(*) AS MessageCount,
                    floor(min((data->>'ts')::numeric))::bigint AS FirstMessage,
                    floor(max((data->>'ts')::numeric))::bigint AS LastMessage
                FROM message
                """,
                cancellationToken: ct
            )
        );

        await SendAsync(
            new GetStatsResponse
            {
                MessageCount = stats.MessageCount,
                FirstMessage = stats.FirstMessage is { } first ? DateTimeOffset.FromUnixTimeSeconds(first).ToString("o") : null,
                LastMessage = stats.LastMessage is { } last ? DateTimeOffset.FromUnixTimeSeconds(last).ToString("o") : null,
            },
            cancellation: ct
        );
    }

    private record StatsRow(long MessageCount, long? FirstMessage, long? LastMessage);
}
