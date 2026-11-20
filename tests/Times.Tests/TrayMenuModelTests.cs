using System.Globalization;
using Times.Tray;

namespace Times.Tests;

// Job 306, AC-6: rows, actions, icon and tooltip from a status.
public sealed class TrayMenuModelTests
{
    private static readonly TrayAction[] AllActions = [TrayAction.Arrive, TrayAction.Break, TrayAction.BreakEnd, TrayAction.Leave];

    private static TrayStatus Status(TrayState state, TimeOnly? since, int left, TrayCheckpoint? checkpoint = null) =>
        new(new DateOnly(2026, 10, 27), state, since, checkpoint, 480 - left, 480, left);

    private static TrayAction[] Enabled(TrayMenuModel model) => AllActions.Where(model.CanRun).ToArray();

    [Fact]
    public void Working_WithCheckpoint()
    {
        TrayMenuModel model = TrayMenuModel.FromStatus(
            Status(TrayState.Working, new TimeOnly(8, 15), 192, new TrayCheckpoint("Design", 1234, new TimeOnly(9, 0))));

        Assert.Equal(["Working since 8:15", "Checkpoint: Design (#1234) since 9:00", "Left today: 3:12"], model.InfoRows);
        Assert.Equal([TrayAction.Break, TrayAction.Leave], Enabled(model));
        Assert.Equal(TrayIconState.In, model.Icon);
        Assert.Equal("Times · Working since 8:15 · Left today: 3:12", model.Tooltip);
    }

    [Fact]
    public void Checkpoint_WithoutWorkItem_HasNoNumber()
    {
        TrayMenuModel model = TrayMenuModel.FromStatus(
            Status(TrayState.Working, new TimeOnly(8, 15), 60, new TrayCheckpoint("Mail", null, new TimeOnly(10, 5))));

        Assert.Equal("Checkpoint: Mail since 10:05", model.InfoRows[1]);
    }

    [Fact]
    public void Break()
    {
        TrayMenuModel model = TrayMenuModel.FromStatus(Status(TrayState.Break, new TimeOnly(12, 0), 240));

        Assert.Equal(["On a break since 12:00", "Left today: 4:00"], model.InfoRows);
        Assert.Equal([TrayAction.BreakEnd], Enabled(model));
        Assert.Equal(TrayIconState.In, model.Icon);
    }

    [Fact]
    public void Out_NegativeLeft_IsOverTarget()
    {
        TrayMenuModel model = TrayMenuModel.FromStatus(Status(TrayState.Out, new TimeOnly(17, 0), -20));

        Assert.Equal(["Not checked in", "Over target: 0:20"], model.InfoRows);
        Assert.Equal([TrayAction.Arrive], Enabled(model));
        Assert.Equal(TrayIconState.Out, model.Icon);
        Assert.Equal("Times · Not checked in · Over target: 0:20", model.Tooltip);
    }

    [Fact]
    public void Absent_NoAction()
    {
        TrayMenuModel model = TrayMenuModel.FromStatus(Status(TrayState.Absent, null, 480));

        Assert.Equal(["Absent today", "Left today: 8:00"], model.InfoRows);
        Assert.Empty(Enabled(model));
        Assert.Equal(TrayIconState.Out, model.Icon);
    }

    [Theory]
    [InlineData(0, "Left today: 0:00")]
    [InlineData(5, "Left today: 0:05")]
    [InlineData(1500, "Left today: 25:00")]
    [InlineData(-61, "Over target: 1:01")]
    public void Durations_AreHmm(int left, string row)
    {
        Assert.Equal(row, TrayMenuModel.FromStatus(Status(TrayState.Out, null, left)).InfoRows[^1]);
    }

    [Fact]
    public void Times_AreInvariant_UnderAnotherCulture()
    {
        CultureInfo before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
            TrayMenuModel model = TrayMenuModel.FromStatus(Status(TrayState.Working, new TimeOnly(8, 15), 192));
            Assert.Equal("Working since 8:15", model.InfoRows[0]);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void LongName_IsCutTo40_WithEllipsis()
    {
        string name = new string('a', 60);
        TrayMenuModel model = TrayMenuModel.FromStatus(
            Status(TrayState.Working, new TimeOnly(8, 0), 0, new TrayCheckpoint(name, null, new TimeOnly(9, 0))));

        Assert.Equal($"Checkpoint: {new string('a', 39)}… since 9:00", model.InfoRows[1]);
    }

    [Fact]
    public void ControlAndBidiCharacters_AreRemoved()
    {
        string name = "Ev‮il⁦‏\n\tName\u001b[31m";
        TrayMenuModel model = TrayMenuModel.FromStatus(
            Status(TrayState.Working, new TimeOnly(8, 0), 0, new TrayCheckpoint(name, 7, new TimeOnly(9, 0))));

        Assert.Equal("Checkpoint: EvilName (#7) since 9:00", model.InfoRows[1]);
    }

    [Fact]
    public void Clean_NeverCutsASurrogatePair()
    {
        string text = new string('a', 38) + "😀" + "bbbb";

        string clean = TrayText.Clean(text, 40);

        Assert.Equal(new string('a', 38) + "…", clean);
    }

    [Fact]
    public void Error_OneRow_NoAction_OutIcon()
    {
        TrayMenuModel model = TrayMenuModel.FromError("Times did not answer.");

        Assert.Equal(["Times did not answer."], model.InfoRows);
        Assert.Empty(Enabled(model));
        Assert.Equal(TrayIconState.Out, model.Icon);
        Assert.Equal("Times · Times did not answer.", model.Tooltip);
    }

    [Fact]
    public void Loading_ReplacesTheFirstRow_DisablesActions_KeepsIcon()
    {
        TrayMenuModel model = TrayMenuModel.FromStatus(Status(TrayState.Working, new TimeOnly(8, 15), 192)).Loading();

        Assert.Equal(["Loading…", "Left today: 3:12"], model.InfoRows);
        Assert.Empty(Enabled(model));
        Assert.Equal(TrayIconState.In, model.Icon);
        Assert.Equal("Times · Loading… · Left today: 3:12", model.Tooltip);
    }
}
