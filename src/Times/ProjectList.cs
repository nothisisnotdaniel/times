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
/// hold no control characters, invisible formatting characters or broken (unpaired) surrogates.
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
    /// separators, so it can never break up a one-line hint or a table row, and free of formatting
    /// characters and unpaired surrogates (see <see cref="HasFormatOrBrokenCharacters"/>), so what is
    /// shown never differs from what is stored.
    /// </summary>
    public static bool IsValidTitle(string title) =>
        !string.IsNullOrWhiteSpace(title)
        && title.Length <= MaxTitleLength
        && !HasControlCharacters(title)
        && !HasFormatOrBrokenCharacters(title);

    /// <summary>True if <paramref name="text"/> holds a control character or a line or paragraph separator.</summary>
    public static bool HasControlCharacters(string text) =>
        text.Any(c => char.IsControl(c)
            || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator);

    /// <summary>
    /// True if <paramref name="text"/> holds a code point of Unicode category Format (Cf), such as a
    /// bidi override, a zero-width character, a soft hyphen or a tag character, or a high or low
    /// surrogate that is not part of a valid pair. Valid pairs (e.g. emoji) are fine.
    /// </summary>
    public static bool HasFormatOrBrokenCharacters(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                {
                    return true;
                }

                if (CharUnicodeInfo.GetUnicodeCategory(char.ConvertToUtf32(c, text[i + 1])) == UnicodeCategory.Format)
                {
                    return true;
                }

                i++;
            }
            else if (char.IsLowSurrogate(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format)
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
