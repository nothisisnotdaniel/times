using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Times;

/// <summary>
/// One day's bookings, saved as <c>{"arrival":"08:15","leaving":"17:30"}</c>. Times are local
/// wall-clock <c>HH:mm</c> in the invariant culture. A value that is not booked is left out.
/// </summary>
internal sealed record DayRecord
{
    public static DayRecord Empty { get; } = new();

    public TimeOnly? Arrival { get; init; }

    public TimeOnly? Leaving { get; init; }

    [JsonIgnore]
    public bool IsEmpty => Arrival is null && Leaving is null;

    /// <summary>The <c>System.Text.Json</c> settings for day files.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new WallClockConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Reads a day file's contents. Throws <see cref="JsonException"/> if they are not a valid day.</summary>
    public static DayRecord FromJson(string json) =>
        JsonSerializer.Deserialize<DayRecord>(json, JsonOptions)
        ?? throw new JsonException("The day file holds no day.");

    /// <summary>Writes and reads a <see cref="TimeOnly"/> as exactly <c>HH:mm</c>.</summary>
    private sealed class WallClockConverter : JsonConverter<TimeOnly>
    {
        private const string Format = "HH:mm";

        public override TimeOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            string? text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            if (text is null
                || !TimeOnly.TryParseExact(text, Format, CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly time))
            {
                throw new JsonException("A time in the day file is not HH:mm.");
            }

            return time;
        }

        public override void Write(Utf8JsonWriter writer, TimeOnly value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString(Format, CultureInfo.InvariantCulture));
    }
}
