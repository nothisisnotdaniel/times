using System.Text.RegularExpressions;

namespace Times;

/// <summary>The kinds of value a setting can hold.</summary>
internal enum SettingKind
{
    /// <summary>On or off, stored as <c>true</c>/<c>false</c>.</summary>
    OnOff,

    /// <summary>A whole number in <see cref="Setting.Min"/>..<see cref="Setting.Max"/>.</summary>
    Number,

    /// <summary>One of <see cref="Setting.Options"/>, stored as the option's text.</summary>
    Choice,
}

/// <summary>
/// One registered setting. <see cref="Default"/> is a <see cref="bool"/> for
/// <see cref="SettingKind.OnOff"/>, an <see cref="int"/> for <see cref="SettingKind.Number"/> and a
/// <see cref="string"/> (one of <see cref="Options"/>) for <see cref="SettingKind.Choice"/>.
/// <see cref="Min"/> and <see cref="Max"/> are only used by numbers, <see cref="Options"/> only by choices.
/// </summary>
internal sealed record Setting
{
    public Setting(string name, string description, SettingKind kind, object defaultValue, int min, int max, IReadOnlyList<string> options)
    {
        Name = name;
        Description = description;
        Kind = kind;
        Default = defaultValue;
        Min = min;
        Max = max;
        Options = options;
    }

    public string Name { get; }

    public string Description { get; }

    public SettingKind Kind { get; }

    public object Default { get; }

    public int Min { get; }

    public int Max { get; }

    public IReadOnlyList<string> Options { get; }

    /// <summary>True if <paramref name="value"/> is a value this setting can hold.</summary>
    public bool Accepts(object value) => Kind switch
    {
        SettingKind.OnOff => value is bool,
        SettingKind.Number => value is int number && number >= Min && number <= Max,
        SettingKind.Choice => value is string option && Options.Contains(option, StringComparer.Ordinal),
        _ => false,
    };
}

/// <summary>
/// The single registry of user settings, the way <see cref="CommandRegistry"/> holds the commands.
/// Each feature registers the settings it needs, and the <c>settings</c> panel lists whatever is
/// registered, in registration order. Values live in <c>settings.json</c> (<see cref="SettingsStore"/>).
/// </summary>
internal sealed partial class SettingsRegistry
{
    /// <summary>The longest text a choice option may have.</summary>
    public const int MaxOptionLength = 40;

    private readonly List<Setting> settings = [];

    /// <summary>The registered settings, in registration order.</summary>
    public IReadOnlyList<Setting> Settings => settings;

    /// <summary>The name of the daily work target setting, in minutes.</summary>
    public const string DailyTargetName = "daily-target";

    /// <summary>The daily work target, in minutes, when none is set (8:00).</summary>
    public const int DailyTargetDefault = 480;

    /// <summary>The highest daily work target, in minutes (24:00).</summary>
    public const int DailyTargetMax = 1440;

    /// <summary>The name of the setting that says what happens when a newer version is found.</summary>
    public const string UpdateModeName = "update-mode";

    /// <summary>Only say that a newer version is there; <c>update</c> stages it for quit.</summary>
    public const string UpdateNotifyOnly = "notify only";

    /// <summary>Download and verify at start, install after quit.</summary>
    public const string UpdateInstallOnQuit = "install on quit";

    /// <summary>Like <see cref="UpdateInstallOnQuit"/>, and at quit say when to start <c>tm</c> again.</summary>
    public const string UpdateInstallAndRestart = "install and restart";

    /// <summary>The name of the setting that picks which releases count as updates.</summary>
    public const string UpdateChannelName = "update-channel";

    /// <summary>The registry the app ships with: each feature registers its settings here.</summary>
    public static SettingsRegistry CreateDefault()
    {
        SettingsRegistry registry = new SettingsRegistry();
        registry.RegisterNumber(DailyTargetName, "Daily work target in minutes (480 = 8:00)", DailyTargetDefault, 0, DailyTargetMax);
        registry.RegisterChoice(UpdateModeName, "What Times does when a newer version is out", UpdateNotifyOnly,
            [UpdateNotifyOnly, UpdateInstallOnQuit, UpdateInstallAndRestart]);
        registry.RegisterChoice(UpdateChannelName, "Which releases count as updates", UpdateChecker.Stable,
            [UpdateChecker.Stable, UpdateChecker.Beta, UpdateChecker.Develop]);
        return registry;
    }

    /// <summary>Finds a setting by its name (exact, ordinal).</summary>
    public bool TryGet(string name, out Setting setting)
    {
        Setting? found = settings.Find(s => string.Equals(s.Name, name, StringComparison.Ordinal));
        setting = found!;
        return found is not null;
    }

    /// <summary>Adds an on/off setting. Throws <see cref="ArgumentException"/> (and adds nothing) as described in <see cref="Validate"/>.</summary>
    public void RegisterOnOff(string name, string description, bool defaultValue) =>
        Add(new Setting(name, description, SettingKind.OnOff, defaultValue, 0, 0, []));

    /// <summary>
    /// Adds a whole-number setting. Throws <see cref="ArgumentException"/> (and adds nothing) also when
    /// <paramref name="min"/> &gt; <paramref name="max"/> or the default is outside min..max.
    /// </summary>
    public void RegisterNumber(string name, string description, int defaultValue, int min, int max)
    {
        Validate(name, description);
        if (min > max)
        {
            throw new ArgumentException($"Setting '{name}' has a minimum above its maximum.", nameof(min));
        }

        if (defaultValue < min || defaultValue > max)
        {
            throw new ArgumentException($"The default of setting '{name}' is outside its range.", nameof(defaultValue));
        }

        Add(new Setting(name, description, SettingKind.Number, defaultValue, min, max, []));
    }

    /// <summary>
    /// Adds a setting that cycles through <paramref name="options"/>. Throws
    /// <see cref="ArgumentException"/> (and adds nothing) also when there are fewer than 2 options, an
    /// option is not a valid title (<see cref="ProjectList.IsValidTitle"/>) or longer than
    /// <see cref="MaxOptionLength"/>, two options are equal, or the default is not one of them.
    /// </summary>
    public void RegisterChoice(string name, string description, string defaultValue, IReadOnlyList<string> options)
    {
        Validate(name, description);
        if (options.Count < 2)
        {
            throw new ArgumentException($"Setting '{name}' needs at least 2 options.", nameof(options));
        }

        if (options.Any(o => !ProjectList.IsValidTitle(o) || o.Length > MaxOptionLength))
        {
            throw new ArgumentException($"Setting '{name}' has an option that is blank, too long or holds invisible characters.", nameof(options));
        }

        if (options.Distinct(StringComparer.Ordinal).Count() != options.Count)
        {
            throw new ArgumentException($"Setting '{name}' has an option more than once.", nameof(options));
        }

        if (!options.Contains(defaultValue, StringComparer.Ordinal))
        {
            throw new ArgumentException($"The default of setting '{name}' is not one of its options.", nameof(defaultValue));
        }

        Add(new Setting(name, description, SettingKind.Choice, defaultValue, 0, 0, options.ToArray()));
    }

    private void Add(Setting setting)
    {
        Validate(setting.Name, setting.Description);
        settings.Add(setting);
    }

    /// <summary>
    /// Every setting needs a name matching <c>^[a-z][a-z0-9-]{0,39}$</c> that is not registered yet,
    /// and a description that is not blank.
    /// </summary>
    private void Validate(string name, string description)
    {
        if (!NamePattern().IsMatch(name))
        {
            throw new ArgumentException(
                "A setting name must start with a-z and hold only a-z, 0-9 and '-', at most 40 characters.", nameof(name));
        }

        if (TryGet(name, out _))
        {
            throw new ArgumentException($"Setting '{name}' is already registered.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException($"Setting '{name}' needs a description.", nameof(description));
        }
    }

    // \z, not $: a trailing line break is not part of a name.
    [GeneratedRegex(@"^[a-z][a-z0-9-]{0,39}\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
