namespace Api.Routes.GetActivity;

public class GetActivityResponse
{
    public class DayResponse
    {
        // As YYYY-MM-DD.
        public required string Date { get; init; }
        // Messages posted by people that day, thread replies included.
        public required long Count { get; init; }
        // Distinct people among them.
        public required long Users { get; init; }
    }

    // Only days with such messages, oldest first.
    public required GetActivityResponse.DayResponse[] Days { get; init; }
}
