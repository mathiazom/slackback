namespace Api.Routes.GetStats;

public class GetStatsResponse
{
    // All archived messages, thread replies included.
    public required long MessageCount { get; init; }
    // Null when nothing is archived yet.
    public string? FirstMessage { get; init; }
}
