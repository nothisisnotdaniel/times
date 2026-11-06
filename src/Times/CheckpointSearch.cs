using System.Text.Json;

namespace Times;

/// <summary>One checkpoint group on one day that matched a search.</summary>
internal sealed class SearchMatch
{
    public SearchMatch(DateOnly date, int? workItem, IReadOnlyList<string> names, TimeSpan total, bool open)
    {
        Date = date;
        WorkItem = workItem;
        Names = names;
        Total = total;
        Open = open;
    }

    /// <summary>The day the group was booked on.</summary>
    public DateOnly Date { get; }

    /// <summary>The group's work item, or <c>null</c> for a name group.</summary>
    public int? WorkItem { get; }

    /// <summary>The distinct checkpoint names in the group on that day, in first-booked order.</summary>
    public IReadOnlyList<string> Names { get; }

    /// <summary>The group's <see cref="CheckpointSummary"/> total on that day.</summary>
    public TimeSpan Total { get; }

    /// <summary>True if one of the group's checkpoints has no end on a day that is not today.</summary>
    public bool Open { get; }

    /// <summary>
    /// The match's title: the quicklist title of its work item from <paramref name="projects"/>
    /// (<c>null</c>: none), otherwise its names joined with <c>", "</c>, as <see cref="ReportRow.Title"/>.
    /// </summary>
    public string Title(ProjectList? projects)
    {
        string? title = CheckpointSearch.QuicklistTitle(WorkItem, projects);
        return title ?? string.Join(", ", Names);
    }
}

/// <summary>
/// The checkpoint groups, per day, whose names or quicklist project title contain a text (ordinal,
/// ignoring case), summed with <see cref="CheckpointSummary"/>. Computing it only reads: it never
/// creates or changes a file or folder.
/// </summary>
internal sealed class CheckpointSearch
{
    public CheckpointSearch(IReadOnlyList<SearchMatch> matches, int skippedFiles)
    {
        Matches = matches;
        SkippedFiles = skippedFiles;
    }

    /// <summary>The matches by date ascending, then in the group's order on that day.</summary>
    public IReadOnlyList<SearchMatch> Matches { get; }

    /// <summary>The number of day files that could not be read.</summary>
    public int SkippedFiles { get; }

    /// <summary>
    /// Searches every day <see cref="DayStore.Days"/> lists for checkpoint groups matching
    /// <paramref name="text"/>. A day equal to <paramref name="today"/> counts an open checkpoint up
    /// to <paramref name="now"/>. Day files that cannot be read are skipped and counted. Throws
    /// <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> if the folder cannot be listed.
    /// </summary>
    /// <param name="projects">The quicklist whose titles are searched too, or <c>null</c> to search names only.</param>
    /// <param name="now">The current local minute, seconds already dropped.</param>
    public static CheckpointSearch Calculate(DayStore store, ProjectList? projects, string text, DateOnly today, TimeOnly now)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<(DateOnly Day, int Index, SearchMatch Match)> found = [];
        int skipped = 0;
        foreach (DateOnly day in store.Days())
        {
            DayRecord record;
            try
            {
                record = store.Load(day);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException
                or InvalidOperationException or JsonException)
            {
                skipped++;
                continue;
            }

            IReadOnlyList<CheckpointGroup> groups = CheckpointSummary.Calculate(record, day == today, now);
            for (int i = 0; i < groups.Count; i++)
            {
                CheckpointGroup group = groups[i];
                if (IsMatch(group, projects, text))
                {
                    found.Add((day, i, new SearchMatch(day, group.WorkItem, group.Names, group.Total, group.Open)));
                }
            }
        }

        // Days() may list in any order; sort by date, keeping each day's group order.
        List<SearchMatch> ordered = found.OrderBy(f => f.Day).ThenBy(f => f.Index).Select(f => f.Match).ToList();
        return new CheckpointSearch(ordered, skipped);
    }

    /// <summary>The quicklist title of <paramref name="workItem"/> in <paramref name="projects"/>, or <c>null</c>.</summary>
    internal static string? QuicklistTitle(int? workItem, ProjectList? projects) =>
        workItem is { } item ? projects?.Projects.FirstOrDefault(p => p.WorkItem == item)?.Title : null;

    private static bool IsMatch(CheckpointGroup group, ProjectList? projects, string text)
    {
        if (group.Names.Any(n => n.Contains(text, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        string? title = QuicklistTitle(group.WorkItem, projects);
        return title is not null && title.Contains(text, StringComparison.OrdinalIgnoreCase);
    }
}
