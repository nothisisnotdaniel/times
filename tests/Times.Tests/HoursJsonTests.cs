using System.Text.Json;

namespace Times.Tests;

public sealed class HoursJsonTests : IDisposable
{
    private static readonly DateOnly Today = new DateOnly(2026, 10, 27);
    private static readonly TimeOnly Now = new TimeOnly(18, 30);

    private readonly TempFolder data = new();

    public void Dispose() => data.Dispose();

    private DayStore Store => new DayStore(data.Path);

    private void Write(string date, params string[] entries) =>
        File.WriteAllText(Path.Combine(data.Path, date + ".json"), "{\"entries\":[" + string.Join(',', entries) + "]}");

    private static string E(string kind, string time) => $"{{\"kind\":\"{kind}\",\"time\":\"{time}\"}}";

    private static string Cp(string time, string name, int? workItem = null) =>
        workItem is { } w
            ? $"{{\"kind\":\"checkpoint\",\"time\":\"{time}\",\"name\":{JsonSerializer.Serialize(name)},\"workItem\":{w}}}"
            : $"{{\"kind\":\"checkpoint\",\"time\":\"{time}\",\"name\":{JsonSerializer.Serialize(name)}}}";

    private static DateOnly D(string text) => DateOnly.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

    private JsonElement Build(string from, string to, int step = 0, ProjectList? projects = null)
    {
        string text = HoursJson.Build(Store, D(from), D(to), Today, Now, step, projects);
        Assert.EndsWith("}\n", text, StringComparison.Ordinal);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static string? S(JsonElement element, string name) => element.GetProperty(name).GetString();

    // AC-2
    [Fact]
    public void Days_HoldArrivalLeavingBreaksAndMinutes_InDateOrder()
    {
        // Lunch gap 12:00-12:30, a closed break 10:00-10:15.
        Write("2026-10-20", E("arrival", "08:00"), E("breakStart", "10:00"), E("breakEnd", "10:15"), E("leaving", "12:00"),
            E("arrival", "12:30"), E("leaving", "17:00"));
        // An open break closed by the leaving, then a break with nothing after it.
        Write("2026-10-21", E("arrival", "09:00"), E("breakStart", "12:00"), E("leaving", "13:00"), E("breakStart", "13:30"));
        File.WriteAllText(Path.Combine(data.Path, "2026-10-22.json"), "{\"absence\":\"sick\"}");
        // Today, still working since 17:00: counts up to 18:30.
        Write("2026-10-27", E("arrival", "17:00"));

        JsonElement days = Build("2026-10-01", "2026-10-31").GetProperty("days");

        Assert.Equal(["2026-10-20", "2026-10-21", "2026-10-22", "2026-10-27"], days.EnumerateArray().Select(d => S(d, "date")));

        JsonElement lunch = days[0];
        Assert.Equal(JsonValueKind.Null, lunch.GetProperty("absence").ValueKind);
        Assert.Equal("08:00", S(lunch, "arrival"));
        Assert.Equal("17:00", S(lunch, "leaving"));
        Assert.Equal(1, lunch.GetProperty("breaks").GetArrayLength());
        Assert.Equal("10:00", S(lunch.GetProperty("breaks")[0], "start"));
        Assert.Equal("10:15", S(lunch.GetProperty("breaks")[0], "end"));
        Assert.Equal(45, lunch.GetProperty("breakMinutes").GetInt32());
        Assert.Equal(495, lunch.GetProperty("workMinutes").GetInt32());

        JsonElement open = days[1];
        Assert.Equal("13:00", S(open, "leaving"));
        Assert.Equal(2, open.GetProperty("breaks").GetArrayLength());
        Assert.Equal("13:00", S(open.GetProperty("breaks")[0], "end"));
        Assert.Equal("13:30", S(open.GetProperty("breaks")[1], "start"));
        Assert.Equal(JsonValueKind.Null, open.GetProperty("breaks")[1].GetProperty("end").ValueKind);
        Assert.Equal(60, open.GetProperty("breakMinutes").GetInt32());
        Assert.Equal(180, open.GetProperty("workMinutes").GetInt32());

        JsonElement sick = days[2];
        Assert.Equal("sick", S(sick, "absence"));
        Assert.Equal(JsonValueKind.Null, sick.GetProperty("arrival").ValueKind);
        Assert.Equal(JsonValueKind.Null, sick.GetProperty("leaving").ValueKind);
        Assert.Equal(0, sick.GetProperty("breaks").GetArrayLength());
        Assert.Equal(0, sick.GetProperty("workMinutes").GetInt32());

        JsonElement today = days[3];
        Assert.Equal("17:00", S(today, "arrival"));
        Assert.Equal(JsonValueKind.Null, today.GetProperty("leaving").ValueKind);
        Assert.Equal(90, today.GetProperty("workMinutes").GetInt32());
    }

    // AC-2
    [Theory]
    [InlineData("vacation")]
    [InlineData("publicHoliday")]
    public void Days_AbsenceNames(string absence)
    {
        File.WriteAllText(Path.Combine(data.Path, "2026-10-22.json"), "{\"absence\":\"" + absence + "\"}");

        Assert.Equal(absence, S(Build("2026-10-22", "2026-10-22").GetProperty("days")[0], "absence"));
    }

    // AC-2: a day outside the range is not listed.
    [Fact]
    public void Days_OutsideTheRange_AreLeftOut()
    {
        Write("2026-09-30", E("arrival", "08:00"));
        Write("2026-10-01", E("arrival", "08:00"));
        Write("2026-11-01", E("arrival", "08:00"));

        JsonElement days = Build("2026-10-01", "2026-10-31").GetProperty("days");

        Assert.Equal(["2026-10-01"], days.EnumerateArray().Select(d => S(d, "date")));
    }

    // AC-3
    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    public void WorkItems_MatchTheReportRows(int step)
    {
        Write("2026-10-20", Cp("09:00", "Review", 1234), Cp("09:23", "Mail"), Cp("10:07", "Fix", 1234), E("leaving", "11:00"));
        Write("2026-10-21", Cp("09:00", "Design", 1234), Cp("09:40", "Call", 99), E("leaving", "10:00"));
        // An open checkpoint on a past day.
        Write("2026-10-22", Cp("09:00", "Hanging", 7));
        ProjectList projects = new ProjectList([new Project(1234, "Quicklist title")]);

        JsonElement root = Build("2026-10-01", "2026-10-31", step, projects);
        ProjectReport report = ProjectReport.Calculate(Store, D("2026-10-01"), D("2026-10-31"), Today, Now, step);

        JsonElement rows = root.GetProperty("workItems");
        Assert.Equal(step, root.GetProperty("roundedToMinutes").GetInt32());
        Assert.Equal(report.Rows.Count, rows.GetArrayLength());
        for (int i = 0; i < report.Rows.Count; i++)
        {
            ReportRow expected = report.Rows[i];
            JsonElement row = rows[i];
            if (expected.WorkItem is { } item)
            {
                Assert.Equal(item, row.GetProperty("workItem").GetInt32());
            }
            else
            {
                Assert.Equal(JsonValueKind.Null, row.GetProperty("workItem").ValueKind);
            }

            Assert.Equal(expected.Title(projects), S(row, "title"));
            Assert.Equal((int)expected.Total.TotalMinutes, row.GetProperty("minutes").GetInt32());
            Assert.Equal(expected.Open, row.GetProperty("open").GetBoolean());
            Assert.Equal(expected.Names, row.GetProperty("checkpoints").EnumerateArray().Select(n => n.GetString()));
        }

        Assert.Equal((int)report.Total.TotalMinutes, root.GetProperty("totalMinutes").GetInt32());
        Assert.Equal(rows.EnumerateArray().Sum(r => r.GetProperty("minutes").GetInt32()), root.GetProperty("totalMinutes").GetInt32());

        // A name row and an open row are both there.
        Assert.Contains(rows.EnumerateArray(), r => r.GetProperty("workItem").ValueKind == JsonValueKind.Null && S(r, "title") == "Mail");
        Assert.Contains(rows.EnumerateArray(), r => r.GetProperty("open").GetBoolean());
        Assert.Equal("Quicklist title", S(rows[2], "title"));
        Assert.Equal(["Review", "Fix", "Design"], rows[2].GetProperty("checkpoints").EnumerateArray().Select(n => n.GetString()));
    }

    // AC-4
    [Fact]
    public void UnreadableDayFile_IsLeftOutAndCounted()
    {
        Write("2026-10-20", Cp("09:00", "Review", 1), E("leaving", "10:00"));
        File.WriteAllText(Path.Combine(data.Path, "2026-10-21.json"), "{ not json");

        JsonElement root = Build("2026-10-01", "2026-10-31");

        Assert.Equal(1, root.GetProperty("skippedFiles").GetInt32());
        Assert.Equal(["2026-10-20"], root.GetProperty("days").EnumerateArray().Select(d => S(d, "date")));
        Assert.Equal(1, root.GetProperty("workItems").GetArrayLength());
        Assert.Equal(60, root.GetProperty("totalMinutes").GetInt32());
    }

    // AC-4
    [Fact]
    public void EmptyRange_GivesEmptyArraysAndZero()
    {
        JsonElement root = Build("2026-10-01", "2026-10-31");

        Assert.Equal(0, root.GetProperty("days").GetArrayLength());
        Assert.Equal(0, root.GetProperty("workItems").GetArrayLength());
        Assert.Equal(0, root.GetProperty("totalMinutes").GetInt32());
        Assert.Equal(0, root.GetProperty("skippedFiles").GetInt32());
    }

    // AC-7
    [Fact]
    public void TrickyNamesAndTitles_RoundTrip()
    {
        string[] names = ["Say \"hi\"", "back\\slash", "Grüße 日本 ❤️", "<b>&amp;</b>"];
        Write("2026-10-20", Cp("09:00", names[0]), Cp("09:10", names[1]), Cp("09:20", names[2], 5), Cp("09:30", names[3], 6),
            E("leaving", "10:00"));
        string title = "Title \"q\" \\ <x> & ü";
        ProjectList projects = new ProjectList([new Project(6, title)]);

        string text = HoursJson.Build(Store, D("2026-10-20"), D("2026-10-20"), Today, Now, 0, projects);
        JsonElement rows = JsonDocument.Parse(text).RootElement.GetProperty("workItems");

        Assert.Equal(names[2], S(rows[0], "title"));
        Assert.Equal(title, S(rows[1], "title"));
        Assert.Equal([names[3]], rows[1].GetProperty("checkpoints").EnumerateArray().Select(n => n.GetString()));
        Assert.Contains(rows.EnumerateArray(), r => S(r, "title") == names[0]);
        Assert.Contains(rows.EnumerateArray(), r => S(r, "title") == names[1]);
        Assert.DoesNotContain("<", text, StringComparison.Ordinal);
        Assert.DoesNotContain("&", text, StringComparison.Ordinal);
    }

    // AC-1: the property order is fixed.
    [Fact]
    public void TopLevel_PropertiesInFixedOrder()
    {
        JsonElement root = Build("2026-10-01", "2026-10-31", 10);

        Assert.Equal(["schemaVersion", "from", "to", "roundedToMinutes", "skippedFiles", "days", "workItems", "totalMinutes"],
            root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("2026-10-01", S(root, "from"));
        Assert.Equal("2026-10-31", S(root, "to"));
        Assert.Equal(10, root.GetProperty("roundedToMinutes").GetInt32());
    }
}
