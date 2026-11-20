using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Times;

/// <summary>What one run of the Context command line gave back.</summary>
internal sealed class ContextRun
{
    public ContextRun(int exitCode, string stdout, bool timedOut)
    {
        ExitCode = exitCode;
        Stdout = stdout;
        TimedOut = timedOut;
    }

    /// <summary>The process exit code; meaningless when <see cref="TimedOut"/> is true.</summary>
    public int ExitCode { get; }

    /// <summary>Standard output, at most <see cref="ProcessContextCli.MaxOutputBytes"/> of it, decoded as UTF-8.</summary>
    public string Stdout { get; }

    /// <summary>True if the run took longer than <see cref="ProcessContextCli.Timeout"/> and was killed.</summary>
    public bool TimedOut { get; }

    /// <summary>True for a run that ended in time with exit code 0.</summary>
    public bool Succeeded => !TimedOut && ExitCode == 0;
}

/// <summary>Runs the optional Context command line (<c>context</c>), so tests can fake it.</summary>
internal interface IContextCli
{
    /// <summary>
    /// Runs <c>context</c> with exactly <paramref name="arguments"/>, or returns <c>null</c> when Context is
    /// not installed.
    /// </summary>
    public ContextRun? Run(IReadOnlyList<string> arguments);
}

/// <summary>
/// Runs the real <c>context</c> (<c>context.exe</c> on Windows), found as a full path in an absolute
/// <c>PATH</c> folder or in <c>~/.dotnet/tools</c>. It is started without a shell (each argument passed
/// through <see cref="ProcessStartInfo.ArgumentList"/>), with stdin closed, stdout capped at
/// <see cref="MaxOutputBytes"/> and stderr discarded; a run longer than <see cref="Timeout"/> is killed.
/// </summary>
internal sealed class ProcessContextCli : IContextCli
{
    /// <summary>The most stdout bytes kept; the rest is read and dropped.</summary>
    public const int MaxOutputBytes = 1024 * 1024;

    /// <summary>How long one run may take before it is killed.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly TimeProvider time;

    public ProcessContextCli()
        : this(TimeProvider.System)
    {
    }

    public ProcessContextCli(TimeProvider time)
    {
        this.time = time;
    }

    public ContextRun? Run(IReadOnlyList<string> arguments)
    {
        string? path = Find(Environment.GetEnvironmentVariable("PATH"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), OperatingSystem.IsWindows());
        if (path is null)
        {
            return null;
        }

        ProcessStartInfo info = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        Process? started;
        try
        {
            started = Process.Start(info);
        }
        catch (Win32Exception)
        {
            return null;
        }

        if (started is null)
        {
            return null;
        }

        using Process process = started;
        using CancellationTokenSource deadline = new CancellationTokenSource(Timeout, time);
        try
        {
            process.StandardInput.Close();
            Task drain = process.StandardError.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
            Task<byte[]> read = ReadCappedAsync(process.StandardOutput.BaseStream, deadline.Token);
            Task.WaitAll([read, process.WaitForExitAsync(deadline.Token), drain], deadline.Token);
            return new ContextRun(process.ExitCode, new UTF8Encoding(false, false).GetString(read.Result), timedOut: false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or AggregateException)
        {
            Kill(process);
            return new ContextRun(-1, string.Empty, timedOut: deadline.IsCancellationRequested);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            Kill(process);
            return new ContextRun(-1, string.Empty, timedOut: false);
        }
    }

    /// <summary>
    /// The full path of the Context executable: the first file named <c>context</c> (<c>context.exe</c> when
    /// <paramref name="windows"/>) in an absolute folder of <paramref name="pathVariable"/>, in order, then in
    /// <c>&lt;home&gt;/.dotnet/tools</c>; <c>null</c> if there is none. Relative and empty folders are skipped.
    /// </summary>
    internal static string? Find(string? pathVariable, string home, bool windows)
    {
        string name = windows ? "context.exe" : "context";
        List<string> folders = [];
        if (!string.IsNullOrEmpty(pathVariable))
        {
            folders.AddRange(pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        }

        if (!string.IsNullOrEmpty(home))
        {
            folders.Add(Path.Combine(home, ".dotnet", "tools"));
        }

        foreach (string folder in folders)
        {
            if (!Path.IsPathFullyQualified(folder))
            {
                continue;
            }

            string candidate = Path.Combine(folder, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    // Reads the stream to its end, keeping at most MaxOutputBytes, so a chatty process never blocks on a full pipe.
    private static async Task<byte[]> ReadCappedAsync(Stream stream, CancellationToken cancel)
    {
        using MemoryStream kept = new MemoryStream();
        byte[] buffer = new byte[81920];
        while (true)
        {
            int count = await stream.ReadAsync(buffer, cancel).ConfigureAwait(false);
            if (count == 0)
            {
                return kept.ToArray();
            }

            int room = MaxOutputBytes - (int)kept.Length;
            if (room > 0)
            {
                kept.Write(buffer, 0, Math.Min(room, count));
            }
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already gone, or it cannot be killed: nothing more to do.
        }
    }
}

/// <summary>One open todo from Context's <c>todos --json</c>.</summary>
internal sealed class ContextTodo
{
    public ContextTodo(DateOnly? due, TimeOnly? time, string text, string? note)
    {
        Due = due;
        Time = time;
        Text = text;
        Note = note;
    }

    public DateOnly? Due { get; }

    public TimeOnly? Time { get; }

    public string Text { get; }

    public string? Note { get; }
}

/// <summary>Reads Context's <c>todos --json</c> contract: <c>{"todos":[{"due","time","text","note"}]}</c>. Pure.</summary>
internal static class ContextTodos
{
    /// <summary>
    /// The todos in Context's order, or <c>null</c> when <paramref name="json"/> is not that contract: not JSON, no
    /// <c>todos</c> array, an item that is not an object, a missing or non-string <c>text</c>, or a <c>due</c>
    /// (<c>yyyy-MM-dd</c>), <c>time</c> (<c>HH:mm</c>) or <c>note</c> that is neither a valid string nor null.
    /// Missing <c>due</c>, <c>time</c> and <c>note</c> count as null.
    /// </summary>
    public static IReadOnlyList<ContextTodo>? Parse(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("todos", out JsonElement todos)
                || todos.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            List<ContextTodo> list = [];
            foreach (JsonElement item in todos.EnumerateArray())
            {
                if (ParseItem(item) is not { } todo)
                {
                    return null;
                }

                list.Add(todo);
            }

            return list;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static ContextTodo? ParseItem(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty("text", out JsonElement textElement)
            || textElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        if (!OptionalString(item, "due", out string? dueText)
            || !OptionalString(item, "time", out string? timeText)
            || !OptionalString(item, "note", out string? note))
        {
            return null;
        }

        DateOnly? due = null;
        if (dueText is not null)
        {
            if (!DateOnly.TryParseExact(dueText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly parsed))
            {
                return null;
            }

            due = parsed;
        }

        TimeOnly? at = null;
        if (timeText is not null)
        {
            if (!TimeOnly.TryParseExact(timeText, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly parsed))
            {
                return null;
            }

            at = parsed;
        }

        return new ContextTodo(due, at, textElement.GetString()!, note);
    }

    // A property that is missing or null (value null), or a string; false for any other kind.
    private static bool OptionalString(JsonElement item, string name, out string? value)
    {
        value = null;
        if (!item.TryGetProperty(name, out JsonElement element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString();
        return true;
    }
}
