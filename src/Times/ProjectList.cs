using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Times;

/// <summary>One project on the quicklist: an Azure DevOps work item number and a title.</summary>
internal sealed record Project
{
    public Project(int workItem, string title)
    {
        WorkItem = workItem;
        Title = title;
    }

    public int WorkItem { get; }

    public string Title { get; }
}

/// <summary>
/// The project quicklist, in the order the projects were added. A project's quicklist number is its
/// 1-based position and is not stored. Saved as
/// <c>{"projects":[{"workItem":1234,"title":"Website relaunch"},…]}</c>. Work item numbers are
/// positive and unique; titles are non-blank, at most <see cref="MaxTitleLength"/> characters and
/// hold no control characters, invisible characters or broken (unpaired) surrogates.
/// </summary>
internal sealed record ProjectList
{
    /// <summary>The longest title a project may have.</summary>
    public const int MaxTitleLength = 200;

    private const string ProjectsProperty = "projects";
    private const string WorkItemProperty = "workItem";
    private const string TitleProperty = "title";

    private readonly Project[] projects;

    public ProjectList()
        : this([])
    {
    }

    public ProjectList(IEnumerable<Project> projects)
    {
        this.projects = projects.ToArray();
    }

    public static ProjectList Empty { get; } = new();

    /// <summary>The projects in quicklist order. Project number <c>n</c> is <c>Projects[n - 1]</c>.</summary>
    public IReadOnlyList<Project> Projects => projects;

    public bool IsEmpty => projects.Length == 0;

    /// <summary>
    /// True if <paramref name="title"/> can be a project title: not blank, at most
    /// <see cref="MaxTitleLength"/> characters, free of control characters and line or paragraph
    /// separators, so it can never break up a one-line hint or a table row, and free of invisible
    /// code points (formatting characters, default-ignorable code points such as Hangul fillers and
    /// variation selectors, the braille blank) and unpaired surrogates (see
    /// <see cref="HasInvisibleOrBrokenCharacters"/>), so what is shown never differs from what is
    /// stored. U+FE0E and U+FE0F are allowed right after a visible code point, so emoji such as
    /// <c>❤️</c> stay valid.
    /// </summary>
    public static bool IsValidTitle(string title) =>
        !string.IsNullOrWhiteSpace(title)
        && title.Length <= MaxTitleLength
        && !HasControlCharacters(title)
        && !HasInvisibleOrBrokenCharacters(title);

    /// <summary>True if <paramref name="text"/> holds a control character or a line or paragraph separator.</summary>
    public static bool HasControlCharacters(string text) =>
        text.Any(c => char.IsControl(c)
            || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator);

    /// <summary>
    /// The Unicode <c>Default_Ignorable_Code_Point</c> ranges (Unicode 16, DerivedCoreProperties.txt),
    /// inclusive. Most are also category Format (Cf); the table matters for those that are not:
    /// U+034F combining grapheme joiner, U+115F/U+1160/U+3164/U+FFA0 Hangul fillers, U+17B4/U+17B5
    /// Khmer inherent vowels, U+180B..U+180D and U+180F Mongolian free variation selectors,
    /// U+FE00..U+FE0F and U+E0100..U+E01EF variation selectors, and the unassigned U+2065,
    /// U+FFF0..U+FFF8 and U+E0000..U+E0FFF. U+FE0E and U+FE0F are let through by the emoji
    /// exception in <see cref="HasInvisibleOrBrokenCharacters"/>.
    /// </summary>
    private static readonly (int First, int Last)[] DefaultIgnorableRanges =
    [
        (0x00AD, 0x00AD),
        (0x034F, 0x034F),
        (0x061C, 0x061C),
        (0x115F, 0x1160),
        (0x17B4, 0x17B5),
        (0x180B, 0x180F),
        (0x200B, 0x200F),
        (0x202A, 0x202E),
        (0x2060, 0x206F),
        (0x3164, 0x3164),
        (0xFE00, 0xFE0F),
        (0xFEFF, 0xFEFF),
        (0xFFA0, 0xFFA0),
        (0xFFF0, 0xFFF8),
        (0x1BCA0, 0x1BCA3),
        (0x1D173, 0x1D17A),
        (0xE0000, 0xE0FFF),
    ];

    /// <summary>U+2800 BRAILLE PATTERN BLANK: a symbol (So), not ignorable, yet it shows as blank.</summary>
    private const int BrailleBlank = 0x2800;

    /// <summary>U+FE0E VARIATION SELECTOR-15 (text presentation).</summary>
    private const int TextPresentationSelector = 0xFE0E;

    /// <summary>U+FE0F VARIATION SELECTOR-16 (emoji presentation).</summary>
    private const int EmojiPresentationSelector = 0xFE0F;

    /// <summary>
    /// True if <paramref name="text"/> holds a high or low surrogate that is not part of a valid pair,
    /// or an invisible code point: category Format (Cf), such as a bidi override, a zero-width
    /// character, a soft hyphen or a tag character; a <c>Default_Ignorable_Code_Point</c> (see
    /// <see cref="DefaultIgnorableRanges"/>), such as a Hangul filler or a variation selector; or
    /// U+2800 braille blank. The check works on code points, so supplementary ones are tested as
    /// pairs. Valid pairs (e.g. emoji) are fine. Emoji exception: U+FE0E or U+FE0F is allowed when it
    /// directly follows a code point that is neither invisible nor a variation selector (so
    /// <c>❤️</c> is fine), but not at the start or after another selector.
    /// </summary>
    public static bool HasInvisibleOrBrokenCharacters(string text)
    {
        bool previousIsVisible = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            int codePoint;
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                {
                    return true;
                }

                codePoint = char.ConvertToUtf32(c, text[i + 1]);
                i++;
            }
            else if (char.IsLowSurrogate(c))
            {
                return true;
            }
            else
            {
                codePoint = c;
            }

            if (codePoint is TextPresentationSelector or EmojiPresentationSelector)
            {
                if (!previousIsVisible)
                {
                    return true;
                }

                previousIsVisible = false;
                continue;
            }

            if (IsInvisible(codePoint))
            {
                return true;
            }

            previousIsVisible = true;
        }

        return false;
    }

    private static bool IsInvisible(int codePoint)
    {
        if (codePoint == BrailleBlank || CharUnicodeInfo.GetUnicodeCategory(codePoint) == UnicodeCategory.Format)
        {
            return true;
        }

        foreach ((int first, int last) in DefaultIgnorableRanges)
        {
            if (codePoint >= first && codePoint <= last)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The quicklist number of the project with <paramref name="workItem"/>, or <c>null</c> if there is none.</summary>
    public int? NumberOf(int workItem)
    {
        int index = Array.FindIndex(projects, p => p.WorkItem == workItem);
        return index < 0 ? null : index + 1;
    }

    /// <summary>
    /// Returns a new list with <paramref name="project"/> added at the end. Throws
    /// <see cref="ArgumentException"/> if its work item is already listed, the work item is not
    /// positive, or the title is not valid (see <see cref="IsValidTitle"/>).
    /// </summary>
    public ProjectList Add(Project project)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(project.WorkItem, 1);
        if (!IsValidTitle(project.Title))
        {
            throw new ArgumentException("The title is not a valid project title.", nameof(project));
        }

        if (NumberOf(project.WorkItem) is not null)
        {
            throw new ArgumentException("The work item is already on the list.", nameof(project));
        }

        return new ProjectList(projects.Append(project));
    }

    public bool Equals(ProjectList? other) =>
        other is not null && projects.SequenceEqual(other.projects);

    public override int GetHashCode()
    {
        HashCode hash = new HashCode();
        foreach (Project project in projects)
        {
            hash.Add(project);
        }

        return hash.ToHashCode();
    }

    public string ToJson()
    {
        using MemoryStream stream = new MemoryStream();
        using (Utf8JsonWriter writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartArray(ProjectsProperty);
            foreach (Project project in projects)
            {
                writer.WriteStartObject();
                writer.WriteNumber(WorkItemProperty, project.WorkItem);
                writer.WriteString(TitleProperty, project.Title);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Reads the contents of <c>projects.json</c>. Throws <see cref="JsonException"/> if they are not
    /// a valid project list; never returns a partial list. Unknown properties are ignored, as in
    /// <see cref="DayRecord.FromJson"/>.
    /// </summary>
    public static ProjectList FromJson(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("The project file holds no project list.");
        }

        JsonElement? list = null;
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
        {
            string propertyName = ReadPropertyName(property);
            if (!seen.Add(propertyName))
            {
                throw new JsonException($"The project file has \"{propertyName}\" more than once.");
            }

            if (property.NameEquals(ProjectsProperty))
            {
                list = property.Value;
            }
        }

        if (list is not { } items)
        {
            throw new JsonException("The project file has no projects.");
        }

        if (items.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("The project file's projects are not a list.");
        }

        List<Project> result = [];
        HashSet<int> workItems = [];
        foreach (JsonElement item in items.EnumerateArray())
        {
            Project project = ReadProject(item);
            if (!workItems.Add(project.WorkItem))
            {
                throw new JsonException("The project file lists a work item more than once.");
            }

            result.Add(project);
        }

        return new ProjectList(result);
    }

    private static Project ReadProject(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("A project in the project file is not an object.");
        }

        int? workItem = null;
        string? title = null;
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in item.EnumerateObject())
        {
            string propertyName = ReadPropertyName(property);
            if (!seen.Add(propertyName))
            {
                throw new JsonException($"A project in the project file has \"{propertyName}\" more than once.");
            }

            switch (propertyName)
            {
                case WorkItemProperty:
                    workItem = ReadWorkItem(property.Value);
                    break;
                case TitleProperty:
                    title = ReadTitle(property.Value);
                    break;
                default:
                    break;
            }
        }

        if (workItem is not { } w || title is null)
        {
            throw new JsonException("A project in the project file needs a work item and a title.");
        }

        return new Project(w, title);
    }

    private static int ReadWorkItem(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int workItem) || workItem < 1)
        {
            throw new JsonException("A work item in the project file is not a positive whole number.");
        }

        return workItem;
    }

    private static string ReadTitle(JsonElement value)
    {
        string? title;
        try
        {
            title = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (InvalidOperationException ex)
        {
            // An escaped lone surrogate such as \ud800 cannot be turned into a string.
            throw new JsonException("A title in the project file is empty, too long or holds control, formatting or broken characters.", ex);
        }

        if (title is null || !IsValidTitle(title))
        {
            throw new JsonException("A title in the project file is empty, too long or holds control, formatting or broken characters.");
        }

        return title;
    }

    /// <summary>
    /// The property's name. An escaped lone surrogate such as <c>\ud800</c> cannot be turned into a
    /// string; that becomes a <see cref="JsonException"/>, never an <see cref="InvalidOperationException"/>.
    /// </summary>
    private static string ReadPropertyName(JsonProperty property)
    {
        try
        {
            return property.Name;
        }
        catch (InvalidOperationException ex)
        {
            throw new JsonException("A property name in the project file holds broken characters.", ex);
        }
    }
}
