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
                ORDER BY m.public_id
                """,
                new { ChannelId = channelId },
                cancellationToken: ct
            )
        );

        await SendAsync(
            new GetChannelResponse
            {
                Channel = new GetChannelResponse.ChannelResponse
                {
                    Id = channel.Id,
                    Name = channel.Name,
                    Topic = channel.Topic
                },
                Messages = messageRows.Select(row => MapMessage(row, seaweedFs.PublicUrl)).ToArray()
            },
            cancellation: ct
        );
    }

    private static GetChannelResponse.MessageResponse MapMessage(MessageRow row, string seaweedFsPublicUrl)
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
            Files = files.ToArray()
        };
    }

    private record ChannelRow(string Id, string Name, string? Topic);

    private record MessageRow(string Id, string? Text, string? AuthorName, string? FilesJson);
}
