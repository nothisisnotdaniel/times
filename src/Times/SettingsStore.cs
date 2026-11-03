using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Times;

/// <summary>
/// The values of the registered settings, as read from <c>settings.json</c>. A registered setting the
/// file does not mention has its default. Names the file holds that no registered setting has are
/// kept as they were (raw JSON), so saving never loses them. Immutable: <see cref="With"/> returns a copy.
/// </summary>
internal sealed class SettingValues
{
    private const string SettingsProperty = "settings";

    private readonly SettingsRegistry registry;

    // The settings object's entries in file order: a registered name maps to its typed value, any
    // other name to its raw JSON. Registered names set later are appended.
    private readonly List<KeyValuePair<string, object>> entries;

    private SettingValues(SettingsRegistry registry, List<KeyValuePair<string, object>> entries)
    {
        this.registry = registry;
        this.entries = entries;
    }

    /// <summary>Every registered setting at its default, with nothing to write.</summary>
    public static SettingValues Defaults(SettingsRegistry registry) => new(registry, []);

    /// <summary>The value of <paramref name="name"/>: a <see cref="bool"/>, <see cref="int"/> or <see cref="string"/> by kind.</summary>
    public object Get(string name)
    {
        Setting setting = Find(name);
        foreach (KeyValuePair<string, object> entry in entries)
        {
            if (string.Equals(entry.Key, name, StringComparison.Ordinal))
            {
                return entry.Value;
            }
        }

        return setting.Default;
    }

    /// <summary>Whether the on/off setting <paramref name="name"/> is on.</summary>
    public bool IsOn(string name) => (bool)Get(Expect(name, SettingKind.OnOff));

    /// <summary>The value of the number setting <paramref name="name"/>.</summary>
    public int Number(string name) => (int)Get(Expect(name, SettingKind.Number));

    /// <summary>The selected option of the choice setting <paramref name="name"/>.</summary>
    public string Choice(string name) => (string)Get(Expect(name, SettingKind.Choice));

    /// <summary>
    /// A copy with <paramref name="name"/> set to <paramref name="value"/>. Throws
    /// <see cref="ArgumentException"/> if the setting is not registered or cannot hold the value.
    /// </summary>
    public SettingValues With(string name, object value)
    {
        Setting setting = Find(name);
        if (!setting.Accepts(value))
        {
            throw new ArgumentException($"Setting '{name}' cannot hold that value.", nameof(value));
        }

        List<KeyValuePair<string, object>> copy = [.. entries];
        int index = copy.FindIndex(e => string.Equals(e.Key, name, StringComparison.Ordinal));
        if (index < 0)
        {
            copy.Add(new KeyValuePair<string, object>(name, value));
        }
        else
        {
            copy[index] = new KeyValuePair<string, object>(name, value);
        }

        return new SettingValues(registry, copy);
    }

    /// <summary>
    /// Writes <c>{"settings":{…}}</c> with the names in file order (registered names set later at
    /// the end) and unknown names' values exactly as they were read.
    /// </summary>
    public string ToJson()
    {
        using MemoryStream stream = new MemoryStream();
        using (Utf8JsonWriter writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartObject(SettingsProperty);
            foreach (KeyValuePair<string, object> entry in entries)
            {
                writer.WritePropertyName(entry.Key);
                switch (entry.Value)
                {
                    case bool on:
                        writer.WriteBooleanValue(on);
                        break;
                    case int number:
                        writer.WriteNumberValue(number);
                        break;
                    case string option:
                        writer.WriteStringValue(option);
                        break;
                    case JsonElement raw:
                        // Exactly the text that was read, so an unknown setting is kept unchanged.
                        writer.WriteRawValue(raw.GetRawText());
                        break;
                    default:
                        throw new InvalidOperationException("A setting holds a value of an unknown type.");
                }
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Reads the contents of <c>settings.json</c>. Throws <see cref="JsonException"/> if they are not
    /// JSON, and <see cref="InvalidDataException"/> if they are not exactly <c>{"settings":{…}}</c>,
    /// hold a property more than once (at any depth) or a property name that is not valid text, or
    /// hold a value a registered setting cannot take (wrong type, out of range, unknown option).
    /// Never returns partial values.
    /// </summary>
    public static SettingValues FromJson(SettingsRegistry registry, string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        try
        {
            return FromDocument(registry, document.RootElement);
        }
        catch (InvalidOperationException)
        {
            // A property name holding an escaped lone surrogate (such as \ud800) cannot be read as a string.
            throw new InvalidDataException($"{SettingsStore.FileName} has a property name that cannot be read.");
        }
    }

    private static SettingValues FromDocument(SettingsRegistry registry, JsonElement root)
    {
        if (HasDuplicateProperty(root))
        {
            throw new InvalidDataException($"{SettingsStore.FileName} has a property more than once.");
        }

        if (root.ValueKind != JsonValueKind.Object
            || root.EnumerateObject().Count() != 1
            || !root.TryGetProperty(SettingsProperty, out JsonElement settings)
            || settings.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{SettingsStore.FileName} does not hold {{\"settings\":{{…}}}}.");
        }

        List<KeyValuePair<string, object>> entries = [];
        foreach (JsonProperty property in settings.EnumerateObject())
        {
            object value = registry.TryGet(property.Name, out Setting setting)
                ? Read(setting, property.Value)
                : property.Value.Clone();
            entries.Add(new KeyValuePair<string, object>(property.Name, value));
        }

        return new SettingValues(registry, entries);
    }

    private static object Read(Setting setting, JsonElement value)
    {
        object? read = setting.Kind switch
        {
            SettingKind.OnOff => value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            },
            SettingKind.Number => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : null,
            SettingKind.Choice => value.ValueKind == JsonValueKind.String ? ReadString(value) : null,
            _ => null,
        };

        if (read is null || !setting.Accepts(read))
        {
            throw new InvalidDataException($"{SettingsStore.FileName} has a wrong value for \"{setting.Name}\".");
        }

        return read;
    }

    // An escaped lone surrogate such as \ud800 cannot be turned into a string: no option matches it.
    private static string? ReadString(JsonElement value)
    {
        try
        {
            return value.GetString();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool HasDuplicateProperty(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray().Any(HasDuplicateProperty);
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name) || HasDuplicateProperty(property.Value))
            {
                return true;
            }
        }

        return false;
    }

    private Setting Find(string name)
    {
        if (!registry.TryGet(name, out Setting setting))
        {
            throw new ArgumentException($"There is no setting '{name}'.", nameof(name));
        }

        return setting;
    }

    private string Expect(string name, SettingKind kind)
    {
        Setting setting = Find(name);
        if (setting.Kind != kind)
        {
            throw new ArgumentException(
                $"Setting '{name}' is {setting.Kind.ToString().ToLower(CultureInfo.InvariantCulture)}, not {kind.ToString().ToLower(CultureInfo.InvariantCulture)}.",
                nameof(name));
        }

        return name;
    }
}

/// <summary>
/// Reads and writes the user's settings, <c>&lt;folder&gt;/settings.json</c>, next to the day files,
/// as defensively as <see cref="ProjectStore"/>: a regular final link target only (checked before it
/// is opened), at most <see cref="MaxFileBytes"/> bytes through a capped read. Reading never creates
/// the folder or the file. Only <see cref="Save"/> writes, and only into an absolute folder.
/// </summary>
internal sealed class SettingsStore
{
    /// <summary>The file name inside the data folder. It cannot clash with a <c>yyyy-MM-dd.json</c> day file.</summary>
    public const string FileName = "settings.json";

    /// <summary>The settings are tiny. Anything bigger is not ours and is not read.</summary>
    internal const long MaxFileBytes = 64 * 1024;

    public SettingsStore(string folder)
    {
        Folder = folder;
    }

    public string Folder { get; }

    public string FilePath => Path.Combine(Folder, FileName);

    /// <summary>
    /// Loads the values of <paramref name="registry"/>'s settings. A missing file or folder gives
    /// <see cref="SettingValues.Defaults"/> and creates nothing. Throws
    /// <see cref="InvalidDataException"/> if the file cannot be read as settings (see
    /// <see cref="SettingValues.FromJson"/> and <see cref="CappedFile.Read"/>), and
    /// <see cref="InvalidOperationException"/> if <see cref="Folder"/> is not valid.
    /// </summary>
    public SettingValues Load(SettingsRegistry registry)
    {
        EnsureValidFolder();
        byte[]? bytes = CappedFile.Read(FilePath, FileName, MaxFileBytes);
        if (bytes is null)
        {
            return SettingValues.Defaults(registry);
        }

        try
        {
            using StreamReader reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8);
            return SettingValues.FromJson(registry, reader.ReadToEnd());
        }
        catch (JsonException)
        {
            throw new InvalidDataException($"{FileName} is not valid JSON.");
        }
    }

    /// <summary>
    /// Saves <paramref name="values"/>. Creates the folder if it is missing, writes a temp file next to
    /// the target and moves it over the target, UTF-8 without BOM. Throws
    /// <see cref="InvalidOperationException"/> and writes nothing if <see cref="Folder"/> is not valid.
    /// </summary>
    public void Save(SettingValues values)
    {
        EnsureValidFolder();
        string json = values.ToJson();
        Directory.CreateDirectory(Folder);
        string temp = Path.Combine(Folder, $".{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private void EnsureValidFolder()
    {
        if (!DayStore.IsValidFolder(Folder))
        {
            throw new InvalidOperationException("The data folder is not valid.");
        }
    }
}
