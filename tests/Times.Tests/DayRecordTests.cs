using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

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

    private static readonly TimeSpan EightHours = TimeSpan.FromHours(8);

    // AC-2 (091)
    [Fact]
    public void TimeLeft_StartsAtTheGivenTarget()
    {
        DayRecord record = DayRecord.Empty.Add(Arrival(8, 0)).Add(Leaving(12, 0));

        Assert.Equal([Hm(7, 30), Hm(3, 30)], record.TimeLeft(Hm(7, 30)));
        Assert.Equal([Hm(0, 0), Hm(-4, 0)], record.TimeLeft(TimeSpan.Zero));
        Assert.Equal(Hm(4, 0), record.WorkTime());
    }

    // AC-1, AC-2 (019)
    [Fact]
    public void TimeLeft_SubtractsEachClosedSpan_AfterItsLeaving()
    {
        DayRecord record = new DayRecord([Arrival(8, 0), Leaving(12, 0), Arrival(12, 30), Leaving(17, 0)]);

        Assert.Equal([Hm(8, 0), Hm(4, 0), Hm(4, 0), Hm(0, -30)], record.TimeLeft(EightHours));
    }

    // AC-2 (019)
    [Fact]
    public void TimeLeft_IsParallelToEntries_WhateverTheBookingOrder()
    {
        DayRecord record = DayRecord.Empty.Add(Leaving(17, 0)).Add(Arrival(8, 0));

        Assert.Equal([Hm(8, 0), Hm(-1, 0)], record.TimeLeft(EightHours));
    }

    // AC-3 (019)
    [Fact]
    public void TimeLeft_EmptyDay_IsEmpty()
    {
        Assert.Empty(DayRecord.Empty.TimeLeft(EightHours));
    }

    // AC-1, AC-3 (019); AC-4, AC-7 (045)
    [Fact]
    public void TimeLeft_SecondArrivalWhileOpen_ShowsWorkSoFar()
    {
        DayRecord record = new DayRecord([Arrival(9, 0), Arrival(10, 0), Leaving(12, 0)]);

        Assert.Equal([Hm(8, 0), Hm(7, 0), Hm(5, 0)], record.TimeLeft(EightHours));
    }

    // AC-1, AC-3 (019)
    [Fact]
    public void TimeLeft_LeavingWithoutOpenSpan_SubtractsNothing()
    {
        DayRecord record = new DayRecord([Leaving(7, 0), Arrival(8, 0)]);

        Assert.Equal([Hm(8, 0), Hm(8, 0)], record.TimeLeft(EightHours));
    }

    // AC-1 (019)
    [Fact]
    public void TimeLeft_SecondLeavingAfterClose_SubtractsNothing()
    {
        DayRecord record = new DayRecord([Arrival(8, 0), Leaving(12, 0), Leaving(13, 0)]);

        Assert.Equal([Hm(8, 0), Hm(4, 0), Hm(4, 0)], record.TimeLeft(EightHours));
    }

    // AC-1 (019)
    [Fact]
    public void TimeLeft_UnclosedArrival_SubtractsNothing()
    {
        DayRecord record = new DayRecord([Arrival(8, 0), Leaving(10, 0), Arrival(11, 0)]);

        Assert.Equal([Hm(8, 0), Hm(6, 0), Hm(6, 0)], record.TimeLeft(EightHours));
    }

    // AC-3 (019)
    [Fact]
    public void TimeLeft_ZeroLengthSpan_SubtractsNothing()
    {
        DayRecord record = new DayRecord([Arrival(9, 0), Leaving(9, 0)]);

        Assert.Equal([Hm(8, 0), Hm(8, 0)], record.TimeLeft(EightHours));
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

    // Breaks and checkpoints (job 036) ----------------------------------------------------

    private static DayEntry BreakStart(int hour, int minute) => new(EntryKind.BreakStart, new TimeOnly(hour, minute));

    private static DayEntry BreakEnd(int hour, int minute) => new(EntryKind.BreakEnd, new TimeOnly(hour, minute));

    private static DayEntry Checkpoint(int hour, int minute, string name, int? workItem = null) =>
        new(EntryKind.Checkpoint, new TimeOnly(hour, minute), name, workItem);

    // AC-6 (036)
    [Fact]
    public void ToJson_WritesNewKinds_AndLeavesOutMissingWorkItem()
    {
        DayRecord record = new DayRecord(
        [
            Arrival(8, 0),
            BreakStart(12, 0),
            BreakEnd(12, 30),
            Checkpoint(13, 0, "Standup", 5678),
            Checkpoint(14, 0, "2024 review"),
            Leaving(17, 0),
        ]);

        Assert.Equal(
            "{\"entries\":[" +
            "{\"kind\":\"arrival\",\"time\":\"08:00\"}," +
            "{\"kind\":\"breakStart\",\"time\":\"12:00\"}," +
            "{\"kind\":\"breakEnd\",\"time\":\"12:30\"}," +
            "{\"kind\":\"checkpoint\",\"time\":\"13:00\",\"name\":\"Standup\",\"workItem\":5678}," +
            "{\"kind\":\"checkpoint\",\"time\":\"14:00\",\"name\":\"2024 review\"}," +
            "{\"kind\":\"leaving\",\"time\":\"17:00\"}]}",
            record.ToJson());
    }

    // AC-6 (036)
    [Fact]
    public void RoundTrip_KeepsNewKinds_NamesAndWorkItems()
    {
        DayRecord record = new DayRecord(
        [
            BreakStart(12, 0),
            BreakEnd(12, 30),
            Checkpoint(9, 30, "Stand  up [x] \"quoted\" äö", 5678),
            Checkpoint(10, 0, "No project"),
        ]);

        DayRecord loaded = DayRecord.FromJson(record.ToJson());

        Assert.Equal(record, loaded);
        Assert.Equal("Stand  up [x] \"quoted\" äö", loaded.Entries[0].Name);
        Assert.Equal(5678, loaded.Entries[0].WorkItem);
        Assert.Null(loaded.Entries[1].WorkItem);
        Assert.Null(loaded.Entries[2].Name);
    }

    // AC-6, AC-7 (036)
    [Fact]
    public void FromJson_ReadsPropertiesInAnyOrder()
    {
        DayRecord record = DayRecord.FromJson(
            "{\"entries\":[{\"workItem\":7,\"name\":\"Review\",\"time\":\"09:00\",\"kind\":\"checkpoint\"}]}");

        Assert.Equal([Checkpoint(9, 0, "Review", 7)], record.Entries);
    }

    // AC-7 (036)
    [Theory]
    [InlineData("{\"entries\":[{\"kind\":\"checkpoint\",\"time\":\"09:00\"}]}")]
    [InlineData("{\"entries\":[{\"kind\":\"checkpoint\",\"time\":\"09:00\",\"name\":null}]}")]
    [InlineData("{\"entries\":[{\"kind\":\"checkpoint\",\"time\":\"09:00\",\"name\":42}]}")]
    [InlineData("{\"entries\":[{\"kind\":\"checkpoint\",\"time\":\"09:00\",\"name\":\"\"}]}")]
    [InlineData("{\"entries\":[{\"kind\":\"checkpoint\",\"time\":\"09:00\",\"name\":\"   \"}]}")]
    [InlineData("{\"entries\":[{\"kind\":\"checkpoint\",\"time\":\"09:00\",\"name\":\"a\\nb\"}]}")]
    [InlineData("{\"entries\":[{\"kind\":\"checkpoint\",\"time\":\"09:00\",\"name\":\"a\\u2028b\"}]}")]
    [InlineData("{\"entries\":[{\"kind\":\"checkpoint\",\"time\":\"09:00\",\"name\":\"A\",\"name\":\"B\"}]}")]
    [InlineData("{\"entries\":[{\"kind\":\"checkpoint\",\"time\":\"09:00\",\"name\":\"A\",\"workItem\":0}]}")]
    [InlineData("{\"entries\":[{\"kind\":\"checkpoint\",\"time\":\"09:00\",\"name\":\"A\",\"workItem\":-3}]}")]
    [InlineData("{\"entries\":[{\"kind\":\"checkpoint\",\"time\":\"09:00\",\"name\":\"A\",\"workItem\":1.5}]}")]
    [InlineData("{\"entries\":[{\"kind\":\"checkpoint\",\"time\":\"09:00\",\"name\":\"A\",\"workItem\":\"5\"}]}")]
    [InlineData("{\"entries\":[{\"kind\":\"checkpoint\",\"time\":\"09:00\",\"name\":\"A\",\"workItem\":null}]}")]
    [InlineData("{\"entries\":[{\"kind\":\"checkpoint\",\"time\":\"09:00\",\"name\":\"A\",\"workItem\":3000000000}]}")]
    [InlineData("{\"entries\":[{\"kind\":\"checkpoint\",\"time\":\"09:00\",\"name\":\"A\",\"workItem\":1,\"workItem\":2}]}")]
    [InlineData("{\"entries\":[{\"kind\":\"lunch\",\"time\":\"09:00\"}]}")]
    [InlineData("{\"entries\":[{\"kind\":\"Checkpoint\",\"time\":\"09:00\",\"name\":\"A\"}]}")]
    [InlineData("{\"entries\":[{\"kind\":\"break\",\"time\":\"09:00\"}]}")]
    public void FromJson_InvalidCheckpointOrKind_ThrowsJsonException(string json)
    {
        Assert.ThrowsAny<JsonException>(() => DayRecord.FromJson(json));
    }

    // AC-7 (036)
    [Fact]
    public void FromJson_NameLongerThanMax_Throws_AndMaxLengthLoads()
    {
        string max = new string('a', ProjectList.MaxTitleLength);
        string tooLong = max + "a";

        Assert.Equal(max, DayRecord.FromJson($"{{\"entries\":[{{\"kind\":\"checkpoint\",\"time\":\"09:00\",\"name\":\"{max}\"}}]}}").Entries[0].Name);
        Assert.ThrowsAny<JsonException>(() =>
            DayRecord.FromJson($"{{\"entries\":[{{\"kind\":\"checkpoint\",\"time\":\"09:00\",\"name\":\"{tooLong}\"}}]}}"));
    }

    // AC-7 (036)
    [Theory]
    [InlineData("{\"arrival\":\"08:15\",\"leaving\":\"17:30\"}")]
    [InlineData("{\"entries\":[{\"kind\":\"arrival\",\"time\":\"08:15\"},{\"kind\":\"leaving\",\"time\":\"17:30\"}]}")]
    public void FromJson_FilesFromBeforeJob036_StillLoad(string json)
    {
        Assert.Equal([Arrival(8, 15), Leaving(17, 30)], DayRecord.FromJson(json).Entries);
    }

    // AC-6 (036)
    [Fact]
    public void ToJson_ArrivalAndLeaving_Unchanged()
    {
        Assert.Equal(
            "{\"entries\":[{\"kind\":\"arrival\",\"time\":\"08:15\"},{\"kind\":\"leaving\",\"time\":\"12:00\"}]}",
            new DayRecord([Arrival(8, 15), Leaving(12, 0)]).ToJson());
    }

    // AC-9 (036); AC-1, AC-7 (045)
    [Fact]
    public void TimeLeft_BreakIsNotWork_CheckpointChangesNothing()
    {
        DayRecord record = new DayRecord(
            [Arrival(8, 0), BreakStart(12, 0), Checkpoint(12, 15, "Lunch talk", 5), BreakEnd(12, 30), Leaving(17, 0)]);

        Assert.Equal([Hm(8, 0), Hm(4, 0), Hm(4, 0), Hm(4, 0), Hm(0, -30)], record.TimeLeft(EightHours));
    }

    // AC-9 (036); AC-1, AC-7 (045)
    [Fact]
    public void TimeLeft_BreakEndWithoutBreak_IsIgnored_CheckpointShowsWorkSoFar()
    {
        DayRecord record = new DayRecord([Arrival(8, 0), BreakEnd(10, 0), Checkpoint(11, 0, "X"), BreakStart(12, 0), Leaving(13, 0)]);

        // Work runs 08:00 to 12:00; the break from 12:00 ends at the 13:00 leaving.
        Assert.Equal([Hm(8, 0), Hm(6, 0), Hm(5, 0), Hm(4, 0), Hm(4, 0)], record.TimeLeft(EightHours));
    }

    // AC-1, AC-2 (045)
    [Fact]
    public void TimeLeft_EveryKind_ShowsWorkLeftAtItsTime_WithoutBreaks()
    {
        DayRecord record = new DayRecord(
            [Arrival(8, 0), Checkpoint(9, 30, "Kickoff"), BreakStart(12, 0), BreakEnd(12, 30), Checkpoint(15, 0, "Review"), Leaving(17, 0)]);

        Assert.Equal([Hm(8, 0), Hm(6, 30), Hm(4, 0), Hm(4, 0), Hm(1, 30), Hm(0, -30)], record.TimeLeft(EightHours));
    }

    // AC-4 (045)
    [Fact]
    public void TimeLeft_OpenArrivalThenCheckpoint_CountsUpToTheCheckpoint()
    {
        DayRecord record = new DayRecord([Arrival(8, 0), Checkpoint(10, 0, "X")]);

        Assert.Equal([Hm(8, 0), Hm(6, 0)], record.TimeLeft(EightHours));
    }

    // AC-4 (045)
    [Fact]
    public void TimeLeft_BreakNeverEnded_EndsAtTheLeaving()
    {
        DayRecord record = new DayRecord([Arrival(8, 0), BreakStart(12, 0), Leaving(13, 0), Arrival(14, 0), Leaving(15, 0)]);

        Assert.Equal([Hm(8, 0), Hm(4, 0), Hm(4, 0), Hm(4, 0), Hm(3, 0)], record.TimeLeft(EightHours));
    }

    // AC-4 (045)
    [Fact]
    public void TimeLeft_BreakEndWithoutBreakStart_IsIgnored()
    {
        DayRecord record = new DayRecord([Arrival(8, 0), BreakEnd(10, 0), Leaving(12, 0)]);

        Assert.Equal([Hm(8, 0), Hm(6, 0), Hm(4, 0)], record.TimeLeft(EightHours));
    }

    // AC-4 (045)
    [Fact]
    public void TimeLeft_BreakOutsideASpan_IsIgnored()
    {
        DayRecord record = new DayRecord([BreakStart(7, 0), Arrival(8, 0), BreakEnd(9, 0), Leaving(10, 0)]);

        Assert.Equal([Hm(8, 0), Hm(8, 0), Hm(7, 0), Hm(6, 0)], record.TimeLeft(EightHours));
    }

    // AC-4 (045)
    [Fact]
    public void TimeLeft_SecondBreakStartAndArrivalDuringBreak_AreIgnored()
    {
        DayRecord record = new DayRecord(
            [Arrival(8, 0), BreakStart(10, 0), BreakStart(11, 0), Arrival(11, 30), BreakEnd(12, 0), Leaving(13, 0)]);

        Assert.Equal([Hm(8, 0), Hm(6, 0), Hm(6, 0), Hm(6, 0), Hm(6, 0), Hm(5, 0)], record.TimeLeft(EightHours));
    }

    // AC-4 (045)
    [Fact]
    public void TimeLeft_CheckpointWithoutArrival_IsTheTarget()
    {
        DayRecord record = new DayRecord([Checkpoint(9, 0, "X")]);

        Assert.Equal([Hm(8, 0)], record.TimeLeft(EightHours));
    }

    // AC-10 (036)
    [Fact]
    public void ChangeTime_KeepsKindNameAndWorkItem()
    {
        DayRecord record = new DayRecord([Arrival(8, 0), Checkpoint(9, 0, "Standup", 5678), BreakStart(12, 0)]);

        DayRecord changed = record.ChangeTime(2, new TimeOnly(13, 0)).ChangeTime(2, new TimeOnly(7, 30));

        Assert.Equal([BreakStart(7, 30), Arrival(8, 0), Checkpoint(13, 0, "Standup", 5678)], changed.Entries);
    }

    // AC-10 (036)
    [Fact]
    public void RemoveAt_RemovesBreakOrCheckpoint()
    {
        DayRecord record = new DayRecord([BreakStart(12, 0), Checkpoint(13, 0, "A", 1), BreakEnd(14, 0)]);

        Assert.Equal([BreakStart(12, 0), BreakEnd(14, 0)], record.RemoveAt(2).Entries);
        Assert.Equal([Checkpoint(13, 0, "A", 1), BreakEnd(14, 0)], record.RemoveAt(1).Entries);
    }

    // AC-7 (036)
    [Fact]
    public void DayEntry_RejectsInvalidCheckpoints_AndNamesOnOtherKinds()
    {
        TimeOnly nine = new TimeOnly(9, 0);
        Assert.ThrowsAny<ArgumentException>(() => new DayEntry(EntryKind.Checkpoint, nine));
        Assert.ThrowsAny<ArgumentException>(() => new DayEntry(EntryKind.Checkpoint, nine, " ", null));
        Assert.ThrowsAny<ArgumentException>(() => new DayEntry(EntryKind.Checkpoint, nine, "a\tb", null));
        Assert.ThrowsAny<ArgumentException>(() => new DayEntry(EntryKind.Checkpoint, nine, "A", 0));
        Assert.ThrowsAny<ArgumentException>(() => new DayEntry(EntryKind.Arrival, nine, "A", null));
        Assert.ThrowsAny<ArgumentException>(() => new DayEntry(EntryKind.BreakStart, nine, null, 5));
    }

    // AC-1 (047)
    [Fact]
    public void WorkAndBreakTime_EmptyDay_AreZero()
    {
        Assert.Equal(TimeSpan.Zero, DayRecord.Empty.WorkTime());
        Assert.Equal(TimeSpan.Zero, DayRecord.Empty.BreakTime());
    }

    // AC-1 (047)
    [Fact]
    public void WorkTime_IsDailyTargetMinusLastTimeLeft()
    {
        DayRecord record = new DayRecord(
            [Arrival(7, 10), BreakStart(9, 0), BreakEnd(9, 20), Leaving(12, 0), Arrival(13, 0), BreakStart(18, 5)]);

        Assert.Equal(EightHours - record.TimeLeft(EightHours)[^1], record.WorkTime());
        Assert.Equal(Hm(9, 35), record.WorkTime());
    }

    // AC-1 (047), AC-3 (084)
    [Fact]
    public void BreakTime_NothingAccruesAfterLastEntry_AndLunchGapAddsToBreaks()
    {
        // The open break after 18:05 has no end, so it adds nothing; the 12:00 to 13:00 gap adds 1:00.
        DayRecord record = new DayRecord(
            [Arrival(7, 10), BreakStart(9, 0), BreakEnd(9, 20), Leaving(12, 0), Arrival(13, 0), BreakStart(18, 5)]);

        Assert.Equal(Hm(1, 20), record.BreakTime());
        Assert.Equal(Hm(9, 35), record.WorkTime());
        Assert.Equal([Hm(8, 0), Hm(6, 10), Hm(6, 10), Hm(3, 30), Hm(3, 30), Hm(-1, -35)], record.TimeLeft(EightHours));
    }

    // AC-2 (047)
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(6, 0, 0)]
    [InlineData(6, 1, 30)]
    [InlineData(9, 0, 30)]
    [InlineData(9, 1, 45)]
    [InlineData(11, 0, 45)]
    public void RequiredBreak_FollowsStrictThresholds(int hours, int minutes, int requiredMinutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(requiredMinutes), DayRecord.RequiredBreak(Hm(hours, minutes)));
    }

    private static void AssertBreaks(DayRecord record, TimeSpan work, TimeSpan taken, TimeSpan required)
    {
        Assert.Equal(work, record.WorkTime());
        Assert.Equal(taken, record.BreakTime());
        Assert.Equal(required, DayRecord.RequiredBreak(record.WorkTime()));
    }

    // AC-3 (047)
    [Fact]
    public void Breaks_SixHoursWithoutBreak_NeedNone()
    {
        AssertBreaks(new DayRecord([Arrival(8, 0), Leaving(14, 0)]), Hm(6, 0), Hm(0, 0), Hm(0, 0));
    }

    // AC-3 (047)
    [Fact]
    public void Breaks_HalfHourBreak_MeetsThirtyMinutes()
    {
        AssertBreaks(
            new DayRecord([Arrival(8, 0), BreakStart(12, 0), BreakEnd(12, 30), Leaving(17, 0)]),
            Hm(8, 30), Hm(0, 30), Hm(0, 30));
    }

    // AC-3 (047)
    [Fact]
    public void Breaks_TwentyMinuteBreak_IsShortOfThirty()
    {
        AssertBreaks(
            new DayRecord([Arrival(8, 0), BreakStart(12, 0), BreakEnd(12, 20), Leaving(17, 0)]),
            Hm(8, 40), Hm(0, 20), Hm(0, 30));
    }

    // AC-3 (047)
    [Fact]
    public void Breaks_OverNineHours_NeedFortyFive()
    {
        AssertBreaks(
            new DayRecord([Arrival(7, 0), BreakStart(11, 0), BreakEnd(11, 30), Leaving(17, 0)]),
            Hm(9, 30), Hm(0, 30), Hm(0, 45));
    }

    // AC-3 (047)
    [Fact]
    public void Breaks_TwoBreaksAndCheckpoint_AddUp()
    {
        AssertBreaks(
            new DayRecord(
            [
                Arrival(8, 0), BreakStart(10, 0), BreakEnd(10, 15), Checkpoint(11, 0, "A", null),
                BreakStart(12, 0), BreakEnd(12, 30), Leaving(18, 0),
            ]),
            Hm(9, 15), Hm(0, 45), Hm(0, 45));
    }

    // AC-3 (047)
    [Fact]
    public void Breaks_UnendedBreak_CountsUpToLeaving()
    {
        DayRecord record = new DayRecord([Arrival(8, 0), BreakStart(12, 0), Leaving(13, 0)]);

        Assert.Equal(Hm(4, 0), record.WorkTime());
        Assert.Equal(Hm(1, 0), record.BreakTime());
    }

    // AC-1, AC-10 (084)
    [Fact]
    public void Breaks_LeaveAndReturn_CountsGapAsBreak()
    {
        DayRecord record = new DayRecord([Arrival(8, 0), Leaving(12, 0), Arrival(12, 45), Leaving(17, 0)]);

        AssertBreaks(record, Hm(8, 15), Hm(0, 45), Hm(0, 30));
        Assert.True(record.BreakTime() >= DayRecord.RequiredBreak(record.WorkTime()));
        Assert.Equal([Hm(8, 0), Hm(4, 0), Hm(4, 0), Hm(0, -15)], record.TimeLeft(EightHours));
    }

    // AC-4, AC-10 (084)
    [Fact]
    public void BreakTime_OffTimeWithoutLeavingBeforeOrArrivalAfter_IsNotBreak()
    {
        DayRecord morning = new DayRecord([Arrival(8, 0), Leaving(12, 0)]);
        DayRecord afterLeaving = new DayRecord([Arrival(8, 0), Leaving(17, 0), BreakStart(17, 30)]);
        DayRecord beforeArrival = new DayRecord([BreakStart(7, 0), BreakEnd(7, 30), Arrival(8, 0), Leaving(9, 0)]);

        Assert.Equal(TimeSpan.Zero, morning.BreakTime());
        Assert.Equal(Hm(4, 0), morning.WorkTime());
        Assert.Equal([Hm(8, 0), Hm(4, 0)], morning.TimeLeft(EightHours));
        Assert.Equal(TimeSpan.Zero, afterLeaving.BreakTime());
        Assert.Equal(Hm(9, 0), afterLeaving.WorkTime());
        Assert.Equal([Hm(8, 0), Hm(-1, 0), Hm(-1, 0)], afterLeaving.TimeLeft(EightHours));
        Assert.Equal(TimeSpan.Zero, beforeArrival.BreakTime());
        Assert.Equal(Hm(1, 0), beforeArrival.WorkTime());
        Assert.Equal([Hm(8, 0), Hm(8, 0), Hm(8, 0), Hm(7, 0)], beforeArrival.TimeLeft(EightHours));
    }

    // AC-5, AC-10 (084)
    [Fact]
    public void BreakTime_EntriesInsideLunchGap_DoNotSplitOrAddToIt()
    {
        DayRecord breakEnd = new DayRecord(
            [Arrival(8, 0), Leaving(12, 0), BreakEnd(12, 30), Arrival(13, 0), Leaving(17, 0)]);
        DayRecord secondLeaving = new DayRecord(
            [Arrival(8, 0), Leaving(12, 0), Leaving(12, 30), Arrival(13, 0), Leaving(17, 0)]);
        DayRecord checkpoint = new DayRecord(
            [Arrival(8, 0), Leaving(12, 0), Checkpoint(12, 15, "Lunch"), Arrival(13, 0), Leaving(17, 0)]);

        foreach (DayRecord record in new[] { breakEnd, secondLeaving, checkpoint })
        {
            AssertBreaks(record, Hm(8, 0), Hm(1, 0), Hm(0, 30));
            Assert.Equal([Hm(8, 0), Hm(4, 0), Hm(4, 0), Hm(4, 0), Hm(0, 0)], record.TimeLeft(EightHours));
        }
    }

    // AC-6, AC-10 (084)
    [Fact]
    public void BreakTime_UnendedBreakClosedByLeaving_ThenArrival_CountsBreakAndGap()
    {
        DayRecord record = new DayRecord(
            [Arrival(8, 0), BreakStart(12, 0), Leaving(12, 30), Arrival(13, 0), Leaving(17, 0)]);

        AssertBreaks(record, Hm(8, 0), Hm(1, 0), Hm(0, 30));
        Assert.Equal([Hm(8, 0), Hm(4, 0), Hm(4, 0), Hm(4, 0), Hm(0, 0)], record.TimeLeft(EightHours));
    }

    // AC-7, AC-10 (084)
    [Fact]
    public void BreakTime_LeavingAndArrivalAtSameTime_IsZeroGap()
    {
        DayRecord record = new DayRecord([Arrival(8, 0), Leaving(12, 0), Arrival(12, 0), Leaving(17, 0)]);

        AssertBreaks(record, Hm(9, 0), Hm(0, 0), Hm(0, 30));
        Assert.True(record.BreakTime() < DayRecord.RequiredBreak(record.WorkTime()));
        Assert.Equal([Hm(8, 0), Hm(4, 0), Hm(4, 0), Hm(-1, 0)], record.TimeLeft(EightHours));
    }

    // AC-8, AC-10 (084)
    [Fact]
    public void BreakTime_SeveralLunchGaps_AddUp()
    {
        DayRecord record = new DayRecord(
            [Arrival(7, 0), Leaving(10, 0), Arrival(10, 15), Leaving(12, 0), Arrival(12, 30), Leaving(17, 0)]);

        AssertBreaks(record, Hm(9, 15), Hm(0, 45), Hm(0, 45));
        Assert.Equal([Hm(8, 0), Hm(5, 0), Hm(5, 0), Hm(3, 15), Hm(3, 15), Hm(-1, -15)], record.TimeLeft(EightHours));
    }

    // AC-9 (084)
    [Fact]
    public void BreakTime_AwayAfterLeavingWithNoArrivalYet_CountsNothing()
    {
        DayRecord record = new DayRecord([Arrival(8, 0), Leaving(12, 0), Checkpoint(12, 30, "Away")]);

        Assert.Equal(TimeSpan.Zero, record.BreakTime());
    }

    // AC-3 (047)
    [Fact]
    public void Breaks_BreakOutsideSpan_IsIgnored()
    {
        DayRecord record = new DayRecord([BreakStart(7, 0), BreakEnd(7, 30), Arrival(8, 0), Leaving(9, 0)]);

        Assert.Equal(Hm(0, 0), record.BreakTime());
        Assert.Equal(Hm(1, 0), record.WorkTime());
    }

    // Same-time order (049) ------------------------------------------------------------------

    // AC-1 (049)
    [Fact]
    public void Add_BreakEndAfterCheckpointAtSameTime_GoesBeforeIt()
    {
        DayRecord record = DayRecord.Empty.Add(Checkpoint(12, 30, "Review")).Add(BreakEnd(12, 30));

        Assert.Equal([BreakEnd(12, 30), Checkpoint(12, 30, "Review")], record.Entries);
    }

    // AC-2 (049)
    [Fact]
    public void Add_ArrivalAfterCheckpointAtSameTime_GoesBeforeIt()
    {
        DayRecord record = DayRecord.Empty.Add(Checkpoint(8, 0, "Review")).Add(Arrival(8, 0));

        Assert.Equal([Arrival(8, 0), Checkpoint(8, 0, "Review")], record.Entries);
    }

    // AC-3 (049)
    [Fact]
    public void Add_SeveralCheckpointsBeforeBreakEnd_AllMoveAfterIt_InOrder()
    {
        DayRecord record = DayRecord.Empty.Add(Checkpoint(12, 30, "A")).Add(Checkpoint(12, 30, "B")).Add(BreakEnd(12, 30));

        Assert.Equal([BreakEnd(12, 30), Checkpoint(12, 30, "A"), Checkpoint(12, 30, "B")], record.Entries);
    }

    // AC-3 (049)
    [Fact]
    public void Add_CheckpointsAroundArrival_EndUpAfterIt_InOrder()
    {
        DayRecord record = DayRecord.Empty.Add(Checkpoint(12, 30, "A")).Add(Arrival(12, 30)).Add(Checkpoint(12, 30, "B"));

        Assert.Equal([Arrival(12, 30), Checkpoint(12, 30, "A"), Checkpoint(12, 30, "B")], record.Entries);
    }

    // Letters for the AC-4 theory: c = checkpoint, a = arrival, l = leaving, s = break start, e = break end.
    private static DayEntry FromLetter(char letter, int index) => letter switch
    {
        'c' => Checkpoint(12, 30, "C" + index.ToString(CultureInfo.InvariantCulture)),
        'a' => Arrival(12, 30),
        'l' => Leaving(12, 30),
        's' => BreakStart(12, 30),
        'e' => BreakEnd(12, 30),
        _ => throw new ArgumentOutOfRangeException(nameof(letter)),
    };

    // AC-4 (049): given booking order -> expected order; indexes name each entry's booking position.
    [Theory]
    [InlineData("cla", "1,2,0")]
    [InlineData("cs", "0,1")]
    [InlineData("la", "0,1")]
    [InlineData("se", "0,1")]
    [InlineData("ac", "0,1")]
    [InlineData("ce", "1,0")]
    [InlineData("cac", "1,0,2")]
    [InlineData("caclc", "1,0,2,3,4")]
    [InlineData("ccsae", "2,3,4,0,1")]
    public void Add_SameTime_OnlyCheckpointsBeforeTheLastOpenerMove(string booked, string expected)
    {
        DayEntry[] given = booked.Select(FromLetter).ToArray();
        DayRecord record = DayRecord.Empty;
        foreach (DayEntry entry in given)
        {
            record = record.Add(entry);
        }

        DayEntry[] want = expected.Split(',').Select(i => given[int.Parse(i, CultureInfo.InvariantCulture)]).ToArray();
        Assert.Equal(want, record.Entries);
        Assert.Equal(want, new DayRecord(given).Entries);
    }

    // AC-4 (049)
    [Fact]
    public void Order_CheckpointAtOtherTime_IsNeverMoved()
    {
        DayRecord record = DayRecord.Empty.Add(Checkpoint(12, 29, "Before")).Add(BreakEnd(12, 30)).Add(Checkpoint(12, 31, "After"));

        Assert.Equal([Checkpoint(12, 29, "Before"), BreakEnd(12, 30), Checkpoint(12, 31, "After")], record.Entries);
    }

    // AC-5 (049)
    [Fact]
    public void ChangeTime_CheckpointOntoOpenerTime_GoesAfterIt()
    {
        DayRecord record = new DayRecord([Arrival(8, 0), Checkpoint(9, 0, "X"), BreakStart(12, 0), BreakEnd(12, 30)]);

        Assert.Equal(
            [Arrival(8, 0), BreakStart(12, 0), BreakEnd(12, 30), Checkpoint(12, 30, "X")],
            record.ChangeTime(2, new TimeOnly(12, 30)).Entries);
        Assert.Equal(
            [Arrival(8, 0), Checkpoint(8, 0, "X"), BreakStart(12, 0), BreakEnd(12, 30)],
            record.ChangeTime(2, new TimeOnly(8, 0)).Entries);
    }

    // AC-5 (049)
    [Fact]
    public void ChangeTime_OpenerOntoCheckpointTime_GoesBeforeIt()
    {
        DayRecord record = new DayRecord([Arrival(8, 0), BreakStart(12, 0), Checkpoint(12, 30, "X"), BreakEnd(13, 0)]);

        Assert.Equal(
            [Arrival(8, 0), BreakStart(12, 0), BreakEnd(12, 30), Checkpoint(12, 30, "X")],
            record.ChangeTime(4, new TimeOnly(12, 30)).Entries);

        DayRecord late = new DayRecord([Checkpoint(9, 0, "Y"), Arrival(10, 0)]);
        Assert.Equal([Arrival(9, 0), Checkpoint(9, 0, "Y")], late.ChangeTime(2, new TimeOnly(9, 0)).Entries);
    }

    // AC-5 (049)
    [Fact]
    public void RemoveAt_KeepsRuleOrder()
    {
        DayRecord record = DayRecord.Empty
            .Add(Checkpoint(12, 30, "A"))
            .Add(Arrival(12, 30))
            .Add(Checkpoint(12, 30, "B"))
            .Add(BreakEnd(12, 30));

        Assert.Equal([Arrival(12, 30), BreakEnd(12, 30), Checkpoint(12, 30, "A"), Checkpoint(12, 30, "B")], record.Entries);
        Assert.Equal([Arrival(12, 30), Checkpoint(12, 30, "A"), Checkpoint(12, 30, "B")], record.RemoveAt(2).Entries);
        Assert.Equal([BreakEnd(12, 30), Checkpoint(12, 30, "A"), Checkpoint(12, 30, "B")], record.RemoveAt(1).Entries);
        Assert.Equal([Arrival(12, 30), BreakEnd(12, 30), Checkpoint(12, 30, "B")], record.RemoveAt(3).Entries);
    }

    // AC-6 (049)
    [Fact]
    public void FromJson_CheckpointBeforeBreakEnd_LoadsReordered_AndRoundTrips()
    {
        const string File =
            "{\"entries\":[{\"kind\":\"checkpoint\",\"time\":\"12:30\",\"name\":\"Review\"},{\"kind\":\"breakEnd\",\"time\":\"12:30\"}]}";
        const string Reordered =
            "{\"entries\":[{\"kind\":\"breakEnd\",\"time\":\"12:30\"},{\"kind\":\"checkpoint\",\"time\":\"12:30\",\"name\":\"Review\"}]}";

        DayRecord record = DayRecord.FromJson(File);

        Assert.Equal([BreakEnd(12, 30), Checkpoint(12, 30, "Review")], record.Entries);
        Assert.Equal(Reordered, record.ToJson());
        Assert.Equal(record, DayRecord.FromJson(record.ToJson()));
        Assert.Equal(Reordered, DayRecord.FromJson(Reordered).ToJson());
    }

    // AC-7 (049)
    [Fact]
    public void Totals_UseReorderedEntries()
    {
        DayRecord record = DayRecord.Empty
            .Add(Arrival(8, 0))
            .Add(BreakStart(12, 0))
            .Add(Checkpoint(12, 30, "X"))
            .Add(BreakEnd(12, 30))
            .Add(Leaving(13, 0));
        DayRecord without = new DayRecord([Arrival(8, 0), BreakStart(12, 0), BreakEnd(12, 30), Leaving(13, 0)]);

        Assert.Equal(without.WorkTime(), record.WorkTime());
        Assert.Equal(Hm(4, 30), record.WorkTime());
        Assert.Equal(Hm(0, 30), record.BreakTime());
        Assert.Equal([Hm(8, 0), Hm(4, 0), Hm(4, 0), Hm(4, 0), Hm(3, 30)], record.TimeLeft(EightHours));
    }

    // Safer titles and names (job 056) ---------------------------------------------------------

    // AC-2 (056): escaped cases are unescaped in the test, so lone surrogates never go through
    // xUnit's test case serialization.
    [Theory]
    [InlineData("abc\\u202Edef")]
    [InlineData("abc\\u200Bdef")]
    [InlineData("abc\\u200Cdef")]
    [InlineData("abc\\u200Ddef")]
    [InlineData("abc\\u2060def")]
    [InlineData("abc\\uFEFFdef")]
    [InlineData("abc\\u00ADdef")]
    [InlineData("abc\\u2066def")]
    [InlineData("abc\\uDB40\\uDC41def")]
    [InlineData("abc\\uD800def")]
    [InlineData("abc\\uDC00def")]
    [InlineData("abc\\uDC00\\uD800def")]
    [InlineData("\\u200B")]
    public void Checkpoint_FormatOrBrokenName_Throws(string escaped)
    {
        string name = Regex.Unescape(escaped);

        Assert.Throws<ArgumentException>(() => new DayEntry(EntryKind.Checkpoint, new TimeOnly(9, 0), name, null));
    }

    // AC-2 (056)
    [Theory]
    [InlineData("Website relaunch")]
    [InlineData("Caf\\u00E9 \\u00DCn\\u00EFcode")]
    [InlineData("\\u65E5\\u672C\\u8A9E")]
    [InlineData("Launch \\uD83D\\uDE80")]
    public void Checkpoint_OrdinaryUnicodeName_IsKept(string escaped)
    {
        string name = Regex.Unescape(escaped);

        Assert.Equal(name, Checkpoint(9, 0, name).Name);
    }

    // AC-6 (056)
    [Theory]
    [InlineData("{\"entries\":[{\"kind\":\"checkpoint\",\"time\":\"09:00\",\"name\":\"abc\u202Edef\"}]}")]
    [InlineData("{\"entries\":[{\"kind\":\"checkpoint\",\"time\":\"09:00\",\"name\":\"abc\u200Bdef\"}]}")]
    [InlineData("{\"entries\":[{\"kind\":\"checkpoint\",\"time\":\"09:00\",\"name\":\"abc\\u202edef\"}]}")]
    [InlineData("{\"entries\":[{\"kind\":\"checkpoint\",\"time\":\"09:00\",\"name\":\"abc\\ud800def\"}]}")]
    [InlineData("{\"entries\":[{\"kind\":\"checkpoint\",\"time\":\"09:00\",\"name\":\"abc\\udc00def\"}]}")]
    [InlineData("{\"entries\":[{\"kind\":\"checkpoint\",\"time\":\"09:00\",\"name\":\"\\ud800\"}]}")]
    public void FromJson_FormatOrBrokenName_ThrowsJsonException(string json)
    {
        JsonException ex = Assert.Throws<JsonException>(() => DayRecord.FromJson(json));
        Assert.Contains("control, formatting or broken characters", ex.Message);
    }

    // AC-6 (095)
    [Theory]
    [InlineData("{\"entries\":[{\"kind\":\"checkpoint\",\"time\":\"09:00\",\"name\":\"abc\u115Fdef\"}]}")]
    [InlineData("{\"entries\":[{\"kind\":\"checkpoint\",\"time\":\"09:00\",\"name\":\"abc\\u115fdef\"}]}")]
    [InlineData("{\"entries\":[{\"kind\":\"checkpoint\",\"time\":\"09:00\",\"name\":\"abc\\udb40\\udd00def\"}]}")]
    [InlineData("{\"entries\":[{\"kind\":\"checkpoint\",\"time\":\"09:00\",\"name\":\"abc\\u2800def\"}]}")]
    public void FromJson_InvisibleName_ThrowsJsonException(string json)
    {
        JsonException ex = Assert.Throws<JsonException>(() => DayRecord.FromJson(json));
        Assert.Contains("control, formatting or broken characters", ex.Message);
    }

    // AC-1, AC-3 (095): the DayEntry constructor uses the same check.
    [Theory]
    [InlineData("abc\\u3164def", false)]
    [InlineData("abc\\u2800def", false)]
    [InlineData("a\\uFE0F\\uFE0F", false)]
    [InlineData("Love \\u2764\\uFE0F", true)]
    public void Checkpoint_WiderInvisibleCheck(string escaped, bool valid)
    {
        string name = Regex.Unescape(escaped);

        if (valid)
        {
            Assert.Equal(name, Checkpoint(9, 0, name).Name);
        }
        else
        {
            Assert.Throws<ArgumentException>(() => new DayEntry(EntryKind.Checkpoint, new TimeOnly(9, 0), name, null));
        }
    }

    // Booked checkpoints (job 078) --------------------------------------------------------

    private static DayEntry Cp(int hour, int minute, string name, bool booked = false) =>
        new(EntryKind.Checkpoint, new TimeOnly(hour, minute), name, null, booked);

    // AC-1, AC-2 (078)
    [Fact]
    public void ToJson_WritesBookedOnlyOnBookedCheckpoints_AndRoundTrips()
    {
        DayRecord record = new DayRecord([Arrival(8, 0), Cp(9, 0, "A", booked: true), Cp(10, 0, "B")]);

        string json = record.ToJson();

        Assert.Equal(
            "{\"entries\":[{\"kind\":\"arrival\",\"time\":\"08:00\"},"
            + "{\"kind\":\"checkpoint\",\"time\":\"09:00\",\"name\":\"A\",\"booked\":true},"
            + "{\"kind\":\"checkpoint\",\"time\":\"10:00\",\"name\":\"B\"}]}",
            json);
        Assert.Equal(record, DayRecord.FromJson(json));
        Assert.True(DayRecord.FromJson(json).Entries[1].Booked);
    }

    // AC-6 (078)
    [Theory]
    [InlineData("{\"entries\":[{\"kind\":\"checkpoint\",\"time\":\"09:00\",\"name\":\"A\",\"workItem\":12}]}")]
    [InlineData("{\"entries\":[{\"kind\":\"checkpoint\",\"time\":\"09:00\",\"name\":\"A\",\"booked\":false}]}")]
    public void FromJson_WithoutBookedOrFalse_LoadsUnbooked(string json)
    {
        DayRecord record = DayRecord.FromJson(json);

        Assert.False(Assert.Single(record.Entries).Booked);
        Assert.DoesNotContain("booked", record.ToJson());
    }

    // AC-6 (078)
    [Fact]
    public void FromJson_Legacy_LoadsUnbooked()
    {
        DayRecord record = DayRecord.FromJson("{\"arrival\":\"08:15\",\"leaving\":\"12:00\"}");

        Assert.All(record.Entries, e => Assert.False(e.Booked));
    }

    // AC-7 (078)
    [Theory]
    [InlineData("\"true\"")]
    [InlineData("1")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("true,\"booked\":true")]
    [InlineData("true,\"booked\":false")]
    public void FromJson_BadBookedOnCheckpoint_ThrowsJsonException(string value)
    {
        string json = "{\"entries\":[{\"kind\":\"checkpoint\",\"time\":\"09:00\",\"name\":\"A\",\"booked\":" + value + "}]}";

        Assert.ThrowsAny<JsonException>(() => DayRecord.FromJson(json));
    }

    // AC-7 (078)
    [Theory]
    [InlineData("true")]
    [InlineData("\"yes\"")]
    [InlineData("null")]
    public void FromJson_BookedOnOtherKinds_IsIgnored(string value)
    {
        string json = "{\"entries\":[{\"kind\":\"arrival\",\"time\":\"08:00\",\"booked\":" + value + "}]}";

        DayEntry entry = Assert.Single(DayRecord.FromJson(json).Entries);

        Assert.Equal(new DayEntry(EntryKind.Arrival, new TimeOnly(8, 0)), entry);
        Assert.False(entry.Booked);
    }

    // AC-8 (078)
    [Theory]
    [InlineData((int)EntryKind.Arrival)]
    [InlineData((int)EntryKind.Leaving)]
    [InlineData((int)EntryKind.BreakStart)]
    [InlineData((int)EntryKind.BreakEnd)]
    public void DayEntry_BookedOnOtherKind_Throws(int kindValue)
    {
        EntryKind kind = (EntryKind)kindValue;
        Assert.Throws<ArgumentException>(() => new DayEntry(kind, new TimeOnly(8, 0), null, null, true));
        Assert.False(new DayEntry(kind, new TimeOnly(8, 0), null, null, false).Booked);
    }

    // AC-8 (078)
    [Fact]
    public void SetBooked_SetsAndClearsTheFlag_KeepingOrder()
    {
        DayRecord record = new DayRecord([Arrival(8, 0), Cp(9, 0, "A"), Cp(9, 0, "B"), Leaving(12, 0)]);

        DayRecord booked = record.SetBooked(3, true);

        Assert.Equal([Arrival(8, 0), Cp(9, 0, "A"), Cp(9, 0, "B", booked: true), Leaving(12, 0)], booked.Entries);
        Assert.Equal(record, booked.SetBooked(3, false));
        Assert.Equal(booked, booked.SetBooked(3, true));
    }

    // AC-8 (078)
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(5)]
    public void SetBooked_UnknownNumber_ThrowsArgumentOutOfRange(int number)
    {
        DayRecord record = new DayRecord([Arrival(8, 0), Cp(9, 0, "A"), Cp(10, 0, "B"), Leaving(12, 0)]);

        Assert.Throws<ArgumentOutOfRangeException>(() => record.SetBooked(number, true));
    }

    // AC-8 (078)
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void SetBooked_NotACheckpoint_ThrowsArgumentException(int number)
    {
        DayRecord record = new DayRecord([Arrival(8, 0), Cp(9, 0, "A"), Cp(10, 0, "B"), Leaving(12, 0)]);

        ArgumentException ex = Assert.Throws<ArgumentException>(() => record.SetBooked(number, true));
        Assert.IsNotType<ArgumentOutOfRangeException>(ex);
    }

    // AC-8 (078)
    [Fact]
    public void ChangeTime_OnBookedCheckpoint_KeepsItBooked()
    {
        DayRecord record = new DayRecord([Arrival(8, 0), Cp(9, 0, "A", booked: true), Leaving(12, 0)]);

        DayRecord changed = record.ChangeTime(2, new TimeOnly(11, 0));

        Assert.Equal([Arrival(8, 0), Cp(11, 0, "A", booked: true), Leaving(12, 0)], changed.Entries);
        Assert.True(Cp(9, 0, "A", booked: true).At(new TimeOnly(10, 0)).Booked);
    }

    // AC-8 (078)
    [Fact]
    public void Equality_DistinguishesBookedFromUnbooked()
    {
        DayEntry booked = Cp(9, 0, "A", booked: true);
        DayEntry unbooked = Cp(9, 0, "A");

        Assert.NotEqual(booked, unbooked);
        Assert.NotEqual(booked.GetHashCode(), unbooked.GetHashCode());
        Assert.Equal(booked, Cp(9, 0, "A", booked: true));
        Assert.NotEqual(new DayRecord([booked]), new DayRecord([unbooked]));
        Assert.Equal(unbooked, new DayEntry(EntryKind.Checkpoint, new TimeOnly(9, 0), "A", null));
    }

    // AC-10 (078)
    [Fact]
    public void TimeLeftAndBreaks_AreTheSameForBookedAndUnbooked()
    {
        DayRecord unbooked = new DayRecord(
        [
            Arrival(8, 0), Cp(9, 0, "A"), new DayEntry(EntryKind.BreakStart, new TimeOnly(12, 0)),
            new DayEntry(EntryKind.BreakEnd, new TimeOnly(12, 30)), Cp(13, 0, "B"), Leaving(17, 0),
        ]);
        DayRecord booked = unbooked.SetBooked(2, true).SetBooked(5, true);

        Assert.Equal(unbooked.TimeLeft(EightHours), booked.TimeLeft(EightHours));
        Assert.Equal(unbooked.WorkTime(), booked.WorkTime());
        Assert.Equal(unbooked.BreakTime(), booked.BreakTime());
    }

    // AC-1 (083)
    [Fact]
    public void UnendedBreaks_PairedBreak_OrNoBreaks_IsEmpty()
    {
        DayRecord paired = new DayRecord([Arrival(8, 0), BreakStart(12, 0), BreakEnd(12, 30), Leaving(17, 0)]);
        DayRecord none = new DayRecord([Arrival(8, 0), Leaving(17, 0)]);

        Assert.Empty(paired.UnendedBreaks());
        Assert.Empty(none.UnendedBreaks());
        Assert.Empty(DayRecord.Empty.UnendedBreaks());
    }

    // AC-2 (083)
    [Fact]
    public void UnendedBreaks_LeavingBeforeBreakEnd_ReturnsBreakStart()
    {
        DayRecord record = new DayRecord([Arrival(8, 0), BreakStart(12, 0), Leaving(17, 0)]);

        Assert.Equal([2], record.UnendedBreaks());
    }

    // AC-3 (083)
    [Fact]
    public void UnendedBreaks_EndOfDay_OrAnotherBreakStart_ReturnsBreakStart()
    {
        DayRecord endOfDay = new DayRecord([Arrival(8, 0), BreakStart(12, 0)]);
        DayRecord twoStarts = new DayRecord(
            [Arrival(8, 0), BreakStart(12, 0), BreakStart(13, 0), BreakEnd(13, 30), Leaving(17, 0)]);

        Assert.Equal([2], endOfDay.UnendedBreaks());
        Assert.Equal([2], twoStarts.UnendedBreaks());
    }

    // AC-3 (083)
    [Fact]
    public void UnendedBreaks_CheckpointBetweenStartAndEnd_DoesNotCount()
    {
        DayRecord record = new DayRecord(
            [Arrival(8, 0), BreakStart(12, 0), Cp(12, 10, "A"), BreakEnd(12, 30), Leaving(17, 0)]);

        Assert.Empty(record.UnendedBreaks());
    }

    // AC-3 (083)
    [Fact]
    public void UnendedBreaks_SeveralUnended_AscendingNumbers()
    {
        DayRecord record = new DayRecord(
            [Arrival(8, 0), BreakStart(10, 0), Arrival(11, 0), BreakStart(12, 0), Leaving(17, 0)]);

        Assert.Equal([2, 4], record.UnendedBreaks());
    }

    // AC-4 (083)
    [Fact]
    public void BreaksOutsideSpans_BeforeArrival_ReturnsBreakStart()
    {
        DayRecord record = new DayRecord([BreakStart(7, 0), Arrival(8, 0), Leaving(17, 0)]);

        Assert.Equal([1], record.BreaksOutsideSpans());
    }

    // AC-4 (083)
    [Fact]
    public void BreaksOutsideSpans_BetweenSpans_ReturnsBreakEnd()
    {
        DayRecord record = new DayRecord(
            [Arrival(8, 0), Leaving(12, 0), BreakEnd(12, 30), Arrival(13, 0), Leaving(17, 0)]);

        Assert.Equal([3], record.BreaksOutsideSpans());
    }

    // AC-4 (083)
    [Fact]
    public void BreaksOutsideSpans_BreakEndAfterLeaving_ReturnsBreakEnd()
    {
        DayRecord record = new DayRecord([Arrival(8, 0), BreakStart(12, 0), Leaving(17, 0), BreakEnd(17, 30)]);

        Assert.Equal([4], record.BreaksOutsideSpans());
    }

    // AC-4 (083)
    [Fact]
    public void BreaksOutsideSpans_ArrivalInsideSpan_AndLeavingOutside_ChangeNothing()
    {
        DayRecord record = new DayRecord(
            [Arrival(8, 0), Arrival(9, 0), Leaving(12, 0), Leaving(12, 10), BreakStart(12, 20), BreakEnd(12, 30)]);

        Assert.Equal([5, 6], record.BreaksOutsideSpans());
    }

    // AC-5 (083)
    [Fact]
    public void BreaksOutsideSpans_AllInsideSpans_IsEmpty()
    {
        DayRecord noLeaving = new DayRecord([Arrival(8, 0), BreakStart(12, 0), BreakEnd(12, 30)]);
        DayRecord secondSpan = new DayRecord(
            [Arrival(8, 0), Leaving(10, 0), Arrival(11, 0), BreakStart(12, 0), BreakEnd(12, 30), Leaving(17, 0)]);
        DayRecord withCheckpoint = new DayRecord(
            [Cp(7, 0, "A"), Arrival(8, 0), BreakStart(12, 0), BreakEnd(12, 30), Leaving(17, 0), Cp(18, 0, "B")]);

        Assert.Empty(noLeaving.BreaksOutsideSpans());
        Assert.Empty(secondSpan.BreaksOutsideSpans());
        Assert.Empty(withCheckpoint.BreaksOutsideSpans());
        Assert.Empty(DayRecord.Empty.BreaksOutsideSpans());
    }

    // AC-6 (083)
    [Fact]
    public void Hints_SameTime_FollowBookingOrder()
    {
        DayRecord breakFirst = DayRecord.Empty.Add(Arrival(8, 0)).Add(BreakStart(17, 0)).Add(Leaving(17, 0));
        DayRecord leaveFirst = DayRecord.Empty.Add(Arrival(8, 0)).Add(Leaving(17, 0)).Add(BreakStart(17, 0));

        Assert.Equal(EntryKind.BreakStart, breakFirst.Entries[1].Kind);
        Assert.Equal([2], breakFirst.UnendedBreaks());
        Assert.Empty(breakFirst.BreaksOutsideSpans());
        Assert.Equal(EntryKind.BreakStart, leaveFirst.Entries[2].Kind);
        Assert.Equal([3], leaveFirst.UnendedBreaks());
        Assert.Equal([3], leaveFirst.BreaksOutsideSpans());
    }

    // AC-10 (083)
    [Fact]
    public void Hints_DoNotChangeTimeLeftWorkTimeOrBreakTime()
    {
        DayRecord unended = new DayRecord([Arrival(8, 0), BreakStart(12, 0), Leaving(17, 0)]);
        DayRecord before = new DayRecord([BreakStart(7, 0), Arrival(8, 0), Leaving(17, 0)]);
        DayRecord between = new DayRecord(
            [Arrival(8, 0), Leaving(12, 0), BreakEnd(12, 30), Arrival(13, 0), Leaving(17, 0)]);
        DayRecord sameTime = DayRecord.Empty.Add(Arrival(8, 0)).Add(Leaving(17, 0)).Add(BreakStart(17, 0));

        Assert.Equal([TimeSpan.FromHours(8), TimeSpan.FromHours(4), TimeSpan.FromHours(4)], unended.TimeLeft(EightHours));
        Assert.Equal(TimeSpan.FromHours(4), unended.WorkTime());
        Assert.Equal(TimeSpan.FromHours(5), unended.BreakTime());
        Assert.Equal([TimeSpan.FromHours(8), TimeSpan.FromHours(8), TimeSpan.FromHours(-1)], before.TimeLeft(EightHours));
        Assert.Equal(TimeSpan.Zero, before.BreakTime());
        Assert.Equal(TimeSpan.FromHours(8), between.WorkTime());
        Assert.Equal(TimeSpan.FromHours(1), between.BreakTime());
        Assert.Equal([TimeSpan.FromHours(8), TimeSpan.FromHours(-1), TimeSpan.FromHours(-1)], sameTime.TimeLeft(EightHours));
        Assert.Equal(TimeSpan.Zero, sameTime.BreakTime());
    }

    /// <summary>
    /// Day files with an escaped lone surrogate (<c>\ud800</c> or <c>\udc00</c>) in a root property
    /// name, an entry property name, <c>kind</c>, <c>time</c> or a legacy <c>arrival</c>/<c>leaving</c>.
    /// </summary>
    public static TheoryData<string> LoneSurrogateDayFiles()
    {
        string[] templates =
        [
            "{{\"{0}\":1,\"entries\":[]}}",
            "{{\"entries\":[],\"a{0}b\":1}}",
            "{{\"entries\":[{{\"{0}\":1,\"kind\":\"arrival\",\"time\":\"08:00\"}}]}}",
            "{{\"entries\":[{{\"kind\":\"arrival\",\"time\":\"08:00\",\"x{0}\":1}}]}}",
            "{{\"entries\":[{{\"kind\":\"{0}\",\"time\":\"08:00\"}}]}}",
            "{{\"entries\":[{{\"kind\":\"arrival{0}\",\"time\":\"08:00\"}}]}}",
            "{{\"entries\":[{{\"kind\":\"arrival\",\"time\":\"{0}\"}}]}}",
            "{{\"entries\":[{{\"kind\":\"arrival\",\"time\":\"08:00{0}\"}}]}}",
            "{{\"arrival\":\"{0}\"}}",
            "{{\"leaving\":\"08:00{0}\"}}",
        ];
        TheoryData<string> data = new TheoryData<string>();
        foreach (string template in templates)
        {
            foreach (string surrogate in new[] { "\\ud800", "\\udc00" })
            {
                data.Add(string.Format(CultureInfo.InvariantCulture, template, surrogate));
            }
        }

        return data;
    }

    // AC-1 (086)
    [Theory]
    [MemberData(nameof(LoneSurrogateDayFiles))]
    public void FromJson_LoneSurrogateInNameKindOrTime_ThrowsJsonException(string json)
    {
        Assert.Contains("\\u", json);
        Assert.Throws<JsonException>(() => DayRecord.FromJson(json));
    }
}
