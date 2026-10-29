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

    private static TimeSpan Hm(int hours, int minutes) => new(hours, minutes, 0);

    // AC-2 (019)
    [Fact]
    public void DailyTarget_IsEightHours()
    {
        Assert.Equal(TimeSpan.FromHours(8), DayRecord.DailyTarget);
    }

    // AC-1, AC-2 (019)
    [Fact]
    public void TimeLeft_SubtractsEachClosedSpan_AfterItsLeaving()
    {
        DayRecord record = new DayRecord([Arrival(8, 0), Leaving(12, 0), Arrival(12, 30), Leaving(17, 0)]);

        Assert.Equal([Hm(8, 0), Hm(4, 0), Hm(4, 0), Hm(0, -30)], record.TimeLeft());
    }

    // AC-2 (019)
    [Fact]
    public void TimeLeft_IsParallelToEntries_WhateverTheBookingOrder()
    {
        DayRecord record = DayRecord.Empty.Add(Leaving(17, 0)).Add(Arrival(8, 0));

        Assert.Equal([Hm(8, 0), Hm(-1, 0)], record.TimeLeft());
    }

    // AC-3 (019)
    [Fact]
    public void TimeLeft_EmptyDay_IsEmpty()
    {
        Assert.Empty(DayRecord.Empty.TimeLeft());
    }

    // AC-1, AC-3 (019)
    [Fact]
    public void TimeLeft_SecondArrivalWhileOpen_IsIgnored()
    {
        DayRecord record = new DayRecord([Arrival(9, 0), Arrival(10, 0), Leaving(12, 0)]);

        Assert.Equal([Hm(8, 0), Hm(8, 0), Hm(5, 0)], record.TimeLeft());
    }

    // AC-1, AC-3 (019)
    [Fact]
    public void TimeLeft_LeavingWithoutOpenSpan_SubtractsNothing()
    {
        DayRecord record = new DayRecord([Leaving(7, 0), Arrival(8, 0)]);

        Assert.Equal([Hm(8, 0), Hm(8, 0)], record.TimeLeft());
    }

    // AC-1 (019)
    [Fact]
    public void TimeLeft_SecondLeavingAfterClose_SubtractsNothing()
    {
        DayRecord record = new DayRecord([Arrival(8, 0), Leaving(12, 0), Leaving(13, 0)]);

        Assert.Equal([Hm(8, 0), Hm(4, 0), Hm(4, 0)], record.TimeLeft());
    }

    // AC-1 (019)
    [Fact]
    public void TimeLeft_UnclosedArrival_SubtractsNothing()
    {
        DayRecord record = new DayRecord([Arrival(8, 0), Leaving(10, 0), Arrival(11, 0)]);

        Assert.Equal([Hm(8, 0), Hm(6, 0), Hm(6, 0)], record.TimeLeft());
    }

    // AC-3 (019)
    [Fact]
    public void TimeLeft_ZeroLengthSpan_SubtractsNothing()
    {
        DayRecord record = new DayRecord([Arrival(9, 0), Leaving(9, 0)]);

        Assert.Equal([Hm(8, 0), Hm(8, 0)], record.TimeLeft());
    }

    // Remove entry (job 021) ---------------------------------------------------------------

    private static DayRecord ThreeEntries() => DayRecord.Empty.Add(Arrival(8, 0)).Add(Leaving(12, 0)).Add(Arrival(13, 0));

    // AC-9 (021)
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RemoveAt_RemovesFirstMiddleOrLast_AndKeepsOrder(int number)
    {
        DayRecord record = ThreeEntries();
        List<DayEntry> expected = record.Entries.ToList();
        expected.RemoveAt(number - 1);

        DayRecord removed = record.RemoveAt(number);

        Assert.Equal(expected, removed.Entries);
    }

    // AC-9 (021)
    [Fact]
    public void RemoveAt_ReturnsNewRecord_AndLeavesOriginalUnchanged()
    {
        DayRecord record = ThreeEntries();

        DayRecord removed = record.RemoveAt(2);

        Assert.NotSame(record, removed);
        Assert.Equal([Arrival(8, 0), Leaving(12, 0), Arrival(13, 0)], record.Entries);
        Assert.Equal([Arrival(8, 0), Arrival(13, 0)], removed.Entries);
    }

    // AC-9 (021)
    [Fact]
    public void RemoveAt_SameTime_RemovesTheEntryAtThatPosition()
    {
        DayRecord record = DayRecord.Empty.Add(Leaving(12, 0)).Add(Arrival(12, 0)).Add(Leaving(12, 0));

        Assert.Equal([Arrival(12, 0), Leaving(12, 0)], record.RemoveAt(1).Entries);
        Assert.Equal([Leaving(12, 0), Leaving(12, 0)], record.RemoveAt(2).Entries);
        Assert.Equal([Leaving(12, 0), Arrival(12, 0)], record.RemoveAt(3).Entries);
    }

    // AC-9 (021)
    [Fact]
    public void RemoveAt_OnlyEntry_GivesEmptyDay()
    {
        DayRecord removed = DayRecord.Empty.Add(Arrival(8, 0)).RemoveAt(1);

        Assert.True(removed.IsEmpty);
        Assert.Equal("{\"entries\":[]}", removed.ToJson());
    }

    // AC-9 (021)
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void RemoveAt_OutOfRange_Throws(int number)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ThreeEntries().RemoveAt(number));
        Assert.Throws<ArgumentOutOfRangeException>(() => DayRecord.Empty.RemoveAt(1));
    }

    // Change entry (job 023) ---------------------------------------------------------------

    // AC-11 (023)
    [Theory]
    [InlineData(1, 8, 30)]
    [InlineData(2, 12, 30)]
    [InlineData(3, 13, 15)]
    public void ChangeTime_FirstMiddleOrLast_KeepsKindAndOtherEntries(int number, int hour, int minute)
    {
        DayRecord record = ThreeEntries();
        List<DayEntry> expected = record.Entries.ToList();
        expected[number - 1] = new DayEntry(expected[number - 1].Kind, new TimeOnly(hour, minute));

        DayRecord changed = record.ChangeTime(number, new TimeOnly(hour, minute));

        Assert.Equal(expected, changed.Entries);
    }

    // AC-11 (023)
    [Fact]
    public void ChangeTime_PastOthers_ReSortsTheDay()
    {
        DayRecord changed = ThreeEntries().ChangeTime(1, new TimeOnly(14, 0));

        Assert.Equal([Leaving(12, 0), Arrival(13, 0), Arrival(14, 0)], changed.Entries);
    }

    // AC-11 (023)
    [Fact]
    public void ChangeTime_EarlierThanOthers_ReSortsTheDay()
    {
        DayRecord changed = ThreeEntries().ChangeTime(3, new TimeOnly(7, 0));

        Assert.Equal([Arrival(7, 0), Arrival(8, 0), Leaving(12, 0)], changed.Entries);
    }

    // AC-11 (023)
    [Fact]
    public void ChangeTime_TieWithExistingTime_PutsChangedEntryAfterOthers()
    {
        DayRecord record = ThreeEntries();

        Assert.Equal([Arrival(8, 0), Leaving(12, 0), Arrival(12, 0)], record.ChangeTime(3, new TimeOnly(12, 0)).Entries);
        Assert.Equal([Arrival(8, 0), Arrival(13, 0), Leaving(13, 0)], record.ChangeTime(2, new TimeOnly(13, 0)).Entries);
        Assert.Equal([Leaving(12, 0), Arrival(12, 0), Arrival(13, 0)], record.ChangeTime(1, new TimeOnly(12, 0)).Entries);
    }

    // AC-11 (023)
    [Fact]
    public void ChangeTime_SameTime_MovesEntryAfterOthersWithThatTime()
    {
        DayRecord record = DayRecord.Empty.Add(Arrival(12, 0)).Add(Leaving(12, 0));

        Assert.Equal([Leaving(12, 0), Arrival(12, 0)], record.ChangeTime(1, new TimeOnly(12, 0)).Entries);
    }

    // AC-11 (023)
    [Theory]
    [InlineData(1, 14, 0)]
    [InlineData(2, 7, 0)]
    [InlineData(3, 12, 0)]
    public void ChangeTime_MatchesRemoveThenAdd(int number, int hour, int minute)
    {
        DayRecord record = ThreeEntries();
        DayEntry entry = record.Entries[number - 1];

        DayRecord expected = record.RemoveAt(number).Add(new DayEntry(entry.Kind, new TimeOnly(hour, minute)));

        Assert.Equal(expected, record.ChangeTime(number, new TimeOnly(hour, minute)));
    }

    // AC-11 (023)
    [Fact]
    public void ChangeTime_ReturnsNewRecord_AndLeavesOriginalUnchanged()
    {
        DayRecord record = ThreeEntries();

        DayRecord changed = record.ChangeTime(2, new TimeOnly(12, 30));

        Assert.NotSame(record, changed);
        Assert.Equal([Arrival(8, 0), Leaving(12, 0), Arrival(13, 0)], record.Entries);
        Assert.Equal([Arrival(8, 0), Leaving(12, 30), Arrival(13, 0)], changed.Entries);
    }

    // AC-11 (023)
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void ChangeTime_OutOfRange_Throws(int number)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ThreeEntries().ChangeTime(number, new TimeOnly(9, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => DayRecord.Empty.ChangeTime(1, new TimeOnly(9, 0)));
    }
}
