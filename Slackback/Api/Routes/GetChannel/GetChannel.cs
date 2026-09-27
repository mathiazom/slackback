using System.Text.Json;
using Dapper;
using FastEndpoints;
using Npgsql;

namespace Api.Routes.GetChannel;

public class GetChannel(NpgsqlDataSource db, SeaweedFsOptions seaweedFs) : EndpointWithoutRequest<GetChannelResponse>
{
    public override void Configure()
    {
        Get("/channels/{ChannelId}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var channelId = Route<string>("ChannelId", isRequired: true)!;

        await using var conn = await db.OpenConnectionAsync(ct);

        var channel = await conn.QuerySingleOrDefaultAsync<ChannelRow>(
            new CommandDefinition(
                """
                SELECT
                    public_id AS Id,
                    data->>'name' AS Name,
                    data->'topic'->>'value' AS Topic
                FROM channel
                WHERE public_id = @ChannelId
                ORDER BY timestamp DESC
                LIMIT 1
                """,
                new { ChannelId = channelId },
                cancellationToken: ct
            )
        );

        if (channel is null)
        {
            await SendNotFoundAsync(ct);
            return;
        }

        var messageRows = await conn.QueryAsync<MessageRow>(
            new CommandDefinition(
                """
                SELECT
                    m.public_id AS Id,
                    m.thread_ts AS ThreadTs,
                    m.data->>'text' AS Text,
                    COALESCE(u.data->>'real_name', u.data->>'name', m.data->>'user') AS AuthorName,
                    (m.data->'files')::text AS FilesJson
                FROM message m
                JOIN channel c ON c.id = m.channel_id
                LEFT JOIN LATERAL (
                    SELECT data
                    FROM "user"
                    WHERE public_id = m.data->>'user'
                    ORDER BY timestamp DESC
                    LIMIT 1
                ) u ON true
                WHERE c.public_id = @ChannelId
                ORDER BY COALESCE(m.thread_ts, m.public_id), m.public_id
                """,
                new { ChannelId = channelId },
                cancellationToken: ct
            )
        );

        var messages = messageRows
            .GroupBy(row => row.ThreadTs ?? row.Id)
            .Select(group =>
            {
                var rows = group.ToList();
                var replies = rows.Skip(1).Select(row => MapMessage(row, [], seaweedFs.PublicUrl));
                return MapMessage(rows[0], replies, seaweedFs.PublicUrl);
            })
            .ToArray();

        await SendAsync(
            new GetChannelResponse
            {
                Channel = new GetChannelResponse.ChannelResponse
                {
                    Id = channel.Id,
                    Name = channel.Name,
                    Topic = channel.Topic
                },
                Messages = messages
            },
            cancellation: ct
        );
    }

    private static GetChannelResponse.MessageResponse MapMessage(MessageRow row, IEnumerable<GetChannelResponse.MessageResponse> replies, string seaweedFsPublicUrl)
    {
        var files = new List<GetChannelResponse.FileResponse>();

        var filesElement = row.FilesJson is not null ? JsonDocument.Parse(row.FilesJson).RootElement : default;

        if (filesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var file in filesElement.EnumerateArray())
            {
                if (!file.TryGetProperty("archived_file_id", out var archivedFileIdProp))
                {
                    continue;
                }

                var archivedFileId = archivedFileIdProp.GetString();
                if (string.IsNullOrEmpty(archivedFileId))
                {
                    continue;
                }

                files.Add(new GetChannelResponse.FileResponse
                {
                    Id = file.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? archivedFileId : archivedFileId,
                    Name = file.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null,
                    Mimetype = file.TryGetProperty("mimetype", out var mimetypeProp) ? mimetypeProp.GetString() : null,
                    Url = $"{seaweedFsPublicUrl}/{archivedFileId}"
                });
            }
        }

        return new GetChannelResponse.MessageResponse
        {
            Id = row.Id,
            Text = row.Text,
            AuthorName = row.AuthorName ?? "Unknown",
            Files = files.ToArray(),
            Replies = replies.ToArray()
        };
    }

    private record ChannelRow(string Id, string Name, string? Topic);

    private record MessageRow(string Id, string? ThreadTs, string? Text, string? AuthorName, string? FilesJson);
}
