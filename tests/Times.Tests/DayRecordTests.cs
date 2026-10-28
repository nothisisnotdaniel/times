using System.Globalization;
using System.Text.Json;

namespace Times.Tests;

public class DayRecordTests
{
    private static DayEntry Arrival(int hour, int minute) => new(EntryKind.Arrival, new TimeOnly(hour, minute));

    private static DayEntry Leaving(int hour, int minute) => new(EntryKind.Leaving, new TimeOnly(hour, minute));

    // AC-1
    [Fact]
    public void ToJson_WritesOnlyEntries_CamelCaseKind_InvariantTime()
    {
        DayRecord record = DayRecord.Empty.Add(Arrival(8, 15)).Add(Leaving(12, 0));

        Assert.Equal(
            "{\"entries\":[{\"kind\":\"arrival\",\"time\":\"08:15\"},{\"kind\":\"leaving\",\"time\":\"12:00\"}]}",
            record.ToJson());
    }

    // AC-1, AC-3
    [Fact]
    public void ToJson_UsesInvariantTime_UnderGermanCulture()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            DayRecord record = DayRecord.Empty.Add(Leaving(17, 0)).Add(Arrival(8, 0));

            Assert.Equal(
                "{\"entries\":[{\"kind\":\"arrival\",\"time\":\"08:00\"},{\"kind\":\"leaving\",\"time\":\"17:00\"}]}",
                record.ToJson());
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    // AC-2
    [Fact]
    public void Add_KeepsEarlierEntries_AndReturnsNewRecord()
    {
        DayRecord first = DayRecord.Empty.Add(Arrival(8, 15));
        DayRecord second = first.Add(Arrival(13, 0));

        Assert.Equal([Arrival(8, 15)], first.Entries);
        Assert.Equal([Arrival(8, 15), Arrival(13, 0)], second.Entries);
        Assert.True(DayRecord.Empty.IsEmpty);
    }

    // AC-3
    [Fact]
    public void Add_InsertsInTimeOrder()
    {
        DayRecord record = DayRecord.Empty.Add(Leaving(17, 0)).Add(Arrival(8, 0)).Add(Leaving(12, 0));

        Assert.Equal([Arrival(8, 0), Leaving(12, 0), Leaving(17, 0)], record.Entries);
    }

    // AC-3
    [Fact]
    public void Add_SameTime_KeepsBookingOrder_AndAllowsDuplicates()
    {
        DayRecord record = DayRecord.Empty
            .Add(Leaving(12, 0))
            .Add(Arrival(12, 0))
            .Add(Leaving(12, 0))
            .Add(Arrival(8, 0));

        Assert.Equal([Arrival(8, 0), Leaving(12, 0), Arrival(12, 0), Leaving(12, 0)], record.Entries);
    }

    // AC-3
    [Fact]
    public void RoundTrip_KeepsTiesInFileOrder()
    {
        DayRecord record = DayRecord.Empty.Add(Leaving(12, 0)).Add(Arrival(12, 0));

        Assert.Equal(record, DayRecord.FromJson(record.ToJson()));
        Assert.Equal([Leaving(12, 0), Arrival(12, 0)], DayRecord.FromJson(record.ToJson()).Entries);
    }

    // AC-6
    [Fact]
    public void FromJson_Legacy_ConvertsToEntries()
    {
        Assert.Equal(
            [Arrival(8, 15), Leaving(17, 30)],
            DayRecord.FromJson("{\"arrival\":\"08:15\",\"leaving\":\"17:30\"}").Entries);
        Assert.Equal([Arrival(8, 15)], DayRecord.FromJson("{\"arrival\":\"08:15\"}").Entries);
        Assert.Equal([Leaving(17, 30)], DayRecord.FromJson("{\"leaving\":\"17:30\"}").Entries);
        Assert.Equal(DayRecord.Empty, DayRecord.FromJson("{}"));
    }

    // AC-6
    [Fact]
    public void FromJson_LegacyLeavingBeforeArrival_LoadsInTimeOrder()
    {
        Assert.Equal(
            [Leaving(8, 0), Arrival(9, 0)],
            DayRecord.FromJson("{\"arrival\":\"09:00\",\"leaving\":\"08:00\"}").Entries);
    }

    // AC-9
    [Fact]
    public void FromJson_EmptyEntries_IsEmptyDay()
    {
        DayRecord record = DayRecord.FromJson("{\"entries\":[]}");

        Assert.True(record.IsEmpty);
        Assert.Equal(DayRecord.Empty, record);
    }

    // AC-8
    [Theory]
    [InlineData("{\"entries\":[{\"kind\":\"break\",\"time\":\"08:15\"}]}")]
    [InlineData("{\"entries\":[{\"kind\":\"arrival\"}]}")]
    [InlineData("{\"entries\":[{\"kind\":\"arrival\",\"time\":\"8:15\"}]}")]
    [InlineData("{\"entries\":{\"kind\":\"arrival\",\"time\":\"08:15\"}}")]
    [InlineData("{\"entries\":[],\"arrival\":\"08:15\"}")]
    [InlineData("null")]
    [InlineData("[]")]
    public void FromJson_Invalid_ThrowsJsonException(string json)
    {
        Assert.ThrowsAny<JsonException>(() => DayRecord.FromJson(json));
    }

    [Fact]
    public void Equality_ComparesEntries()
    {
        Assert.Equal(DayRecord.Empty.Add(Arrival(8, 0)), new DayRecord([Arrival(8, 0)]));
        Assert.Equal(
            DayRecord.Empty.Add(Arrival(8, 0)).GetHashCode(),
            new DayRecord([Arrival(8, 0)]).GetHashCode());
        Assert.NotEqual(DayRecord.Empty.Add(Arrival(8, 0)), DayRecord.Empty.Add(Leaving(8, 0)));
    }
}
