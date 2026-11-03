using Spectre.Console.Rendering;
using Spectre.Console.Testing;

namespace Times.Tests;

public sealed class SettingsPanelTests
{
    private const int Width = 80;

    private static string[] Render(IRenderable panel)
    {
        TestConsole console = new TestConsole();
        console.Profile.Width = Width;
        console.Write(panel);
        return console.Output.TrimEnd('\n').Split('\n');
    }

    private static string RenderAnsi(IRenderable panel)
    {
        TestConsole console = new TestConsole().EmitAnsiSequences();
        console.Profile.Width = Width;
        console.Write(panel);
        return console.Output;
    }

    private static string Inner(string line)
    {
        Assert.StartsWith("│ ", line);
        Assert.EndsWith(" │", line);
        return line[2..^2].TrimEnd();
    }

    private static SettingValues Defaults() => SettingValues.Defaults(SettingsStoreTests.Registry());

    // AC-3
    [Fact]
    public void Panel_HasSettingsHeader_OneRowPerSetting_AndTheKeyLine()
    {
        string[] lines = Render(SettingsPanel.Render(SettingsStoreTests.Registry(), Defaults(), 0, null));

        Assert.StartsWith("╭─Settings─", lines[0]);
        Assert.StartsWith("╰", lines[^1]);
        Assert.All(lines, l => Assert.Equal(Width, l.Length));
        Assert.Equal(6, lines.Length);
        Assert.Equal("› round   off   Round times", Inner(lines[1]));
        Assert.Equal("  target  480   Daily target", Inner(lines[2]));
        Assert.Equal("  theme   dark  Theme", Inner(lines[3]));
        Assert.Equal("↑/↓ move · Enter/Space change · Esc close", Inner(lines[4]));
    }

    // AC-3, AC-6
    [Fact]
    public void Panel_ShowsCurrentValues_OnOffAsOnOff_AndMarksTheSelectedRow()
    {
        SettingValues values = Defaults().With("round", true).With("target", -10).With("theme", "light");

        string[] lines = Render(SettingsPanel.Render(SettingsStoreTests.Registry(), values, 2, null));

        Assert.Equal("  round   on     Round times", Inner(lines[1]));
        Assert.Equal("  target  -10    Daily target", Inner(lines[2]));
        Assert.Equal("› theme   light  Theme", Inner(lines[3]));
    }

    // AC-3
    [Fact]
    public void Panel_Colours_GreyBorder_BoldSteelblueHeader_SteelblueNames_GreyDescriptionsAndKeys()
    {
        string output = RenderAnsi(SettingsPanel.Render(SettingsStoreTests.Registry(), Defaults(), 0, null));

        Assert.Matches(@"\x1b\[(90|38;5;8)m╭─", output);
        Assert.Matches(@"\x1b\[1;38;5;75mSettings", output);
        Assert.Matches(@"\x1b\[38;5;75mround", output);
        Assert.Matches(@"\x1b\[(90|38;5;8)mRound times", output);
        Assert.Matches(@"\x1b\[(90|38;5;8)m↑/↓ move", output);
        Assert.Matches(@"\x1b\[1;38;5;75m›", output);
    }

    // AC-3
    [Fact]
    public void Panel_EscapesNamesValuesAndDescriptions()
    {
        SettingsRegistry registry = new SettingsRegistry();
        registry.RegisterChoice("theme", "Pick [bold]one[/] of [x]", "[red]x[/]", ["[red]x[/]", "[b]"]);

        string[] lines = Render(SettingsPanel.Render(registry, SettingValues.Defaults(registry), 0, null));

        Assert.Equal("› theme  [red]x[/]  Pick [bold]one[/] of [x]", Inner(lines[1]));
    }

    // AC-3
    [Fact]
    public void LongLines_AreCut_NeverWrapped()
    {
        SettingsRegistry registry = new SettingsRegistry();
        registry.RegisterOnOff("round", new string('d', 200), false);

        string[] lines = Render(SettingsPanel.Render(registry, SettingValues.Defaults(registry), 0, null));

        Assert.Equal(4, lines.Length);
        Assert.EndsWith("… │", lines[1]);
    }

    // AC-4
    [Fact]
    public void NoSettings_ShowsTheGreyNote_AndOnlyEsc()
    {
        string[] lines = Render(SettingsPanel.Render(SettingsRegistry.CreateDefault(), SettingValues.Defaults(SettingsRegistry.CreateDefault()), 0, null));
        string output = RenderAnsi(SettingsPanel.Render(SettingsRegistry.CreateDefault(), SettingValues.Defaults(SettingsRegistry.CreateDefault()), 0, null));

        Assert.Equal(4, lines.Length);
        Assert.Equal("No settings exist yet.", Inner(lines[1]));
        Assert.Equal("Esc close", Inner(lines[2]));
        Assert.Matches(@"\x1b\[(90|38;5;8)mNo settings exist yet\.", output);
        Assert.DoesNotContain("›", output);
    }

    // AC-10
    [Fact]
    public void Unreadable_ShowsRedReason_GreyNextStep_ThenTheDefaults()
    {
        string reason = "settings.json has a wrong value for \"[x]\".";
        string[] lines = Render(SettingsPanel.Render(SettingsStoreTests.Registry(), Defaults(), 0, reason));
        string output = RenderAnsi(SettingsPanel.Render(SettingsStoreTests.Registry(), Defaults(), 0, reason));

        Assert.Equal("The settings cannot be read: settings.json has a wrong value for \"[x]\".", Inner(lines[1]));
        Assert.Equal("Fix or delete settings.json in the data folder (help shows where).", Inner(lines[2]));
        Assert.Equal("› round   off   Round times", Inner(lines[3]));
        Assert.Matches(@"\x1b\[38;5;9mThe settings cannot be read:", output);
        Assert.Matches(@"\x1b\[(90|38;5;8)mFix or delete settings\.json", output);
        Assert.Equal(3, SettingsPanel.LineOf(0, unreadable: true));
        Assert.Equal(2, SettingsPanel.LineOf(1, unreadable: false));
    }
}
