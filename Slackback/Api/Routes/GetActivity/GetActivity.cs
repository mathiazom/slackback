using Dapper;
using FastEndpoints;
using Npgsql;

namespace Api.Routes.GetActivity;

public class GetActivity(NpgsqlDataSource db) : EndpointWithoutRequest<GetActivityResponse>
{
    public override void Configure()
    {
        Get("/activity");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        // Days are bucketed in the caller's time zone, so they match the dates it
        // renders elsewhere.
        var timeZone = Query<string>("tz", isRequired: true)!;

        await using var conn = await db.OpenConnectionAsync(ct);

        var days = await conn.QueryAsync<GetActivityResponse.DayResponse>(
            new CommandDefinition(
                """
                SELECT
                    to_char(to_timestamp((data->>'ts')::numeric) AT TIME ZONE @TimeZone, 'YYYY-MM-DD') AS Date,
                    count(*) AS Count,
                    count(DISTINCT data->>'user') AS Users
                FROM message
                -- People posting: no bots (Slackbot included, which isn't flagged
                -- as a bot user), and no system events like joins or renames.
                -- Allowed by subtype, so new kinds of events stay out.
                WHERE NOT data ? 'bot_id'
                    AND (data->>'subtype' IS NULL OR data->>'subtype' IN ('thread_broadcast', 'me_message', 'file_share'))
                GROUP BY 1
                ORDER BY 1
                """,
                new { TimeZone = timeZone },
                cancellationToken: ct
            )
        );

        await SendAsync(new GetActivityResponse { Days = days.ToArray() }, cancellation: ct);
    }
}
