namespace Times.Tests;

public sealed class SettingsRegistryTests
{
    // AC-1 (091); AC-1 (093): the daily target comes first, then the two update settings.
    [Fact]
    public void CreateDefault_RegistersTheDailyTargetFirst()
    {
        IReadOnlyList<Setting> all = SettingsRegistry.CreateDefault().Settings;
        Setting setting = all[0];

        Assert.Equal(["daily-target", "report-rounding", "update-mode", "update-channel"], all.Select(s => s.Name));
        Assert.Equal("daily-target", setting.Name);
        Assert.Equal("Daily work target in minutes (480 = 8:00)", setting.Description);
        Assert.Equal(SettingKind.Number, setting.Kind);
        Assert.Equal(480, setting.Default);
        Assert.Equal(0, setting.Min);
        Assert.Equal(1440, setting.Max);
    }

    // AC-1 (104)
    [Fact]
    public void CreateDefault_RegistersReportRoundingDirectlyAfterTheDailyTarget()
    {
        IReadOnlyList<Setting> all = SettingsRegistry.CreateDefault().Settings;
        Setting setting = all[1];

        Assert.Equal("daily-target", all[0].Name);
        Assert.Equal("report-rounding", setting.Name);
        Assert.Equal(SettingsRegistry.ReportRoundingName, setting.Name);
        Assert.Equal(SettingKind.Choice, setting.Kind);
        Assert.Equal(["off", "5 min", "10 min", "15 min"], setting.Options);
        Assert.Equal("off", setting.Default);
        Assert.False(string.IsNullOrWhiteSpace(setting.Description));
    }

    // AC-1 (104)
    [Theory]
    [InlineData("off", 0)]
    [InlineData("5 min", 5)]
    [InlineData("10 min", 10)]
    [InlineData("15 min", 15)]
    [InlineData("7 min", 0)]
    [InlineData("15 MIN", 0)]
    [InlineData(" 15 min", 0)]
    [InlineData("15", 0)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    public void ReportRoundingMinutes_MapsOnlyTheExactOptions(string? option, int expected)
    {
        Assert.Equal(expected, SettingsRegistry.ReportRoundingMinutes(option));
    }

    // AC-1 (093)
    [Fact]
    public void CreateDefault_RegistersUpdateModeAndChannelChoices()
    {
        SettingsRegistry registry = SettingsRegistry.CreateDefault();

        Assert.True(registry.TryGet("update-mode", out Setting mode));
        Assert.Equal(SettingKind.Choice, mode.Kind);
        Assert.Equal(["notify only", "install on quit", "install and restart"], mode.Options);
        Assert.Equal("notify only", mode.Default);
        Assert.False(string.IsNullOrWhiteSpace(mode.Description));

        Assert.True(registry.TryGet("update-channel", out Setting channel));
        Assert.Equal(SettingKind.Choice, channel.Kind);
        Assert.Equal(["stable", "beta", "develop"], channel.Options);
        Assert.Equal("stable", channel.Default);
        Assert.False(string.IsNullOrWhiteSpace(channel.Description));
    }

    // AC-1
    [Fact]
    public void Register_KeepsRegistrationOrder_AndEveryField()
    {
        SettingsRegistry registry = new SettingsRegistry();

        registry.RegisterOnOff("round", "Round times", true);
        registry.RegisterNumber("target", "Daily target", 480, 0, 720);
        registry.RegisterChoice("theme", "Theme", "light", ["dark", "light"]);
        registry.RegisterNumber("a", "Single letter, min equals max", -3, -3, -3);
        registry.RegisterOnOff("a1234567890-abcdefghijklmnopqrstuvwxyz12", "Forty characters", false);

        Assert.Equal(["round", "target", "theme", "a", "a1234567890-abcdefghijklmnopqrstuvwxyz12"], registry.Settings.Select(s => s.Name));
        Setting target = registry.Settings[1];
        Assert.Equal(SettingKind.Number, target.Kind);
        Assert.Equal("Daily target", target.Description);
        Assert.Equal(480, target.Default);
        Assert.Equal(0, target.Min);
        Assert.Equal(720, target.Max);
        Setting theme = registry.Settings[2];
        Assert.Equal(SettingKind.Choice, theme.Kind);
        Assert.Equal("light", theme.Default);
        Assert.Equal(["dark", "light"], theme.Options);
        Assert.Equal(true, registry.Settings[0].Default);
        Assert.True(registry.TryGet("theme", out Setting found) && found == theme);
        Assert.False(registry.TryGet("Theme", out _));
    }

    // AC-1
    [Theory]
    [InlineData("")]
    [InlineData("Round")]
    [InlineData("1round")]
    [InlineData("-round")]
    [InlineData("round time")]
    [InlineData("round_time")]
    [InlineData("round\n")]
    [InlineData("ründ")]
    [InlineData("a1234567890-abcdefghijklmnopqrstuvwxyz123")]
    public void Register_InvalidName_Throws_AndAddsNothing(string name)
    {
        SettingsRegistry registry = new SettingsRegistry();

        Assert.Throws<ArgumentException>(() => registry.RegisterOnOff(name, "Description", false));
        Assert.Throws<ArgumentException>(() => registry.RegisterNumber(name, "Description", 1, 0, 2));
        Assert.Throws<ArgumentException>(() => registry.RegisterChoice(name, "Description", "a", ["a", "b"]));

        Assert.Empty(registry.Settings);
    }

    // AC-1
    [Fact]
    public void Register_DuplicateName_Throws_WhateverTheKind_AndAddsNothing()
    {
        SettingsRegistry registry = new SettingsRegistry();
        registry.RegisterOnOff("round", "Round times", false);

        Assert.Throws<ArgumentException>(() => registry.RegisterOnOff("round", "Again", true));
        Assert.Throws<ArgumentException>(() => registry.RegisterNumber("round", "Again", 1, 0, 2));
        Assert.Throws<ArgumentException>(() => registry.RegisterChoice("round", "Again", "a", ["a", "b"]));

        Setting only = Assert.Single(registry.Settings);
        Assert.Equal("Round times", only.Description);
    }

    // AC-1
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\n")]
    public void Register_BlankDescription_Throws_AndAddsNothing(string description)
    {
        SettingsRegistry registry = new SettingsRegistry();

        Assert.Throws<ArgumentException>(() => registry.RegisterOnOff("round", description, false));
        Assert.Throws<ArgumentException>(() => registry.RegisterNumber("target", description, 1, 0, 2));
        Assert.Throws<ArgumentException>(() => registry.RegisterChoice("theme", description, "a", ["a", "b"]));

        Assert.Empty(registry.Settings);
    }

    // AC-1
    [Theory]
    [InlineData(5, 6, 4)] // min > max
    [InlineData(-1, 0, 10)] // default below min
    [InlineData(11, 0, 10)] // default above max
    public void RegisterNumber_BadRange_Throws_AndAddsNothing(int defaultValue, int min, int max)
    {
        SettingsRegistry registry = new SettingsRegistry();

        Assert.Throws<ArgumentException>(() => registry.RegisterNumber("target", "Target", defaultValue, min, max));

        Assert.Empty(registry.Settings);
    }

    public static TheoryData<string, string[]> BadChoices => new()
    {
        { "a", [] },
        { "a", ["a"] },
        { "a", ["a", "a"] },
        { "a", ["a", "b", "a"] },
        { "a", ["a", ""] },
        { "a", ["a", "  "] },
        { "a", ["a", "b\u0007"] },
        { "a", ["a", "b‮c"] },
        { "a", ["a", "b​c"] },
        { "a", ["a", new string('x', 41)] },
        { "c", ["a", "b"] },
        { "A", ["a", "b"] },
    };

    // AC-1
    [Theory]
    [MemberData(nameof(BadChoices))]
    public void RegisterChoice_BadOptionsOrDefault_Throws_AndAddsNothing(string defaultValue, string[] options)
    {
        SettingsRegistry registry = new SettingsRegistry();

        Assert.Throws<ArgumentException>(() => registry.RegisterChoice("theme", "Theme", defaultValue, options));

        Assert.Empty(registry.Settings);
    }

    // AC-1 (a lone surrogate cannot travel through theory data, which replaces it)
    [Fact]
    public void RegisterChoice_OptionWithALoneSurrogate_Throws_AndAddsNothing()
    {
        SettingsRegistry registry = new SettingsRegistry();

        Assert.Throws<ArgumentException>(() => registry.RegisterChoice("theme", "Theme", "a", ["a", "b\ud800"]));

        Assert.Empty(registry.Settings);
    }

    // AC-1
    [Fact]
    public void RegisterChoice_FortyCharacterOption_IsFine_AndOptionsAreCopied()
    {
        SettingsRegistry registry = new SettingsRegistry();
        string[] options = ["a", new string('x', 40)];

        registry.RegisterChoice("theme", "Theme", "a", options);
        options[0] = "changed";

        Assert.Equal(["a", new string('x', 40)], registry.Settings[0].Options);
    }
}
