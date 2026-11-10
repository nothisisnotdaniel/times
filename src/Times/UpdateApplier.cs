using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Times;

/// <summary>
/// A process to start. With <see cref="CommandLine"/> set (Windows <c>cmd</c>), it is passed verbatim;
/// otherwise every entry of <see cref="Arguments"/> is passed as one argument, never through a shell.
/// </summary>
internal sealed record ProcessCall
{
    public ProcessCall(string fileName, IReadOnlyList<string> arguments, string? commandLine = null)
    {
        FileName = fileName;
        Arguments = arguments;
        CommandLine = commandLine;
    }

    public string FileName { get; }

    public IReadOnlyList<string> Arguments { get; }

    public string? CommandLine { get; }
}

/// <summary>Starts the update helper. Tests inject a fake, so no test runs <c>dotnet tool update</c>.</summary>
internal interface IProcessLauncher
{
    /// <summary>Starts <paramref name="call"/> and returns at once, without waiting for it.</summary>
    public void Start(ProcessCall call);
}

/// <summary>Starts a detached process with no terminal: stdin, stdout and stderr are redirected and closed, no window.</summary>
internal sealed class ProcessLauncher : IProcessLauncher
{
    public void Start(ProcessCall call)
    {
        ProcessStartInfo info = new ProcessStartInfo(call.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (call.CommandLine is not null)
        {
            info.Arguments = call.CommandLine;
        }
        else
        {
            foreach (string argument in call.Arguments)
            {
                info.ArgumentList.Add(argument);
            }
        }

        using Process process = Process.Start(info) ?? throw new InvalidOperationException("The update helper did not start.");
        process.StandardInput.Close();
    }
}

/// <summary>
/// Installs a staged update after Times has quit, and reports how it went on the next start. At quit,
/// <see cref="Apply"/> writes <c>update-pending.json</c> to the data folder and starts one detached helper
/// that waits (at most 30 s) for this process to exit, runs <c>dotnet tool update</c> from the staged folder
/// with all output appended to <c>update.log</c>, and deletes the staged folder. On Unix the helper is
/// <c>nohup sh -c '&lt;fixed script&gt;' sh &lt;args…&gt;</c>, so paths are only ever positional arguments.
/// On Windows it is <c>cmd /c</c>, so a path with a character <c>cmd</c> treats specially is refused before staging.
/// </summary>
internal sealed class UpdateApplier
{
    public const string PendingFileName = "update-pending.json";

    public const string LogFileName = "update.log";

    /// <summary>Seconds the helper waits for Times to exit before it installs anyway.</summary>
    public const int WaitSeconds = 30;

    private const long MaxPendingBytes = 4096;

    /// <summary>
    /// The Unix helper. <c>$1</c> PID, <c>$2</c> log, <c>$3</c> staged folder, the rest the
    /// <c>dotnet tool update</c> arguments. Nothing is interpolated into it.
    /// </summary>
    public const string UnixScript =
        "exec >>\"$2\" 2>&1 </dev/null; " +
        "pid=\"$1\"; staged=\"$3\"; shift 3; " +
        "i=0; while kill -0 \"$pid\" 2>/dev/null && [ \"$i\" -lt 30 ]; do sleep 1; i=$((i+1)); done; " +
        "echo \"$(date): dotnet tool update $*\"; " +
        "dotnet tool update \"$@\"; rc=$?; " +
        "echo \"$(date): exit code $rc\"; " +
        "rm -rf -- \"$staged\"";

    /// <summary>The characters a path may not hold on Windows, where it goes into a <c>cmd</c> command line.</summary>
    public const string WindowsRefused = "\"%!^&|<>";

    private readonly IProcessLauncher launcher;
    private readonly bool windows;

    /// <param name="windows">True for the <c>cmd</c> helper; tests pass both.</param>
    public UpdateApplier(IProcessLauncher launcher, bool windows)
    {
        this.launcher = launcher;
        this.windows = windows;
    }

    /// <summary>
    /// Why the helper cannot take these paths, or <c>null</c> if it can. Only Windows refuses anything:
    /// <c>"</c> <c>%</c> <c>!</c> <c>^</c> <c>&amp;</c> <c>|</c> <c>&lt;</c> <c>&gt;</c> and control characters.
    /// </summary>
    public string? RefusePaths(params string[] paths)
    {
        if (!windows)
        {
            return null;
        }

        foreach (string path in paths)
        {
            if (path.Any(c => char.IsControl(c) || WindowsRefused.Contains(c, StringComparison.Ordinal)))
            {
                return "a path holds characters the Windows helper cannot take";
            }
        }

        return null;
    }

    /// <summary>
    /// The <c>dotnet tool update</c> arguments after <c>dotnet tool update</c>. <c>--configfile</c> points at the
    /// staged <c>nuget.config</c>, which clears every other source, so only the verified package can be installed.
    /// The exact <c>--version</c> also names a prerelease, so <c>--prerelease</c> is never added:
    /// <c>dotnet tool update</c> refuses it next to <c>--version</c>.
    /// </summary>
    public static List<string> UpdateArguments(StagedUpdate staged, ToolInstall install)
    {
        List<string> arguments = ["Times"];
        arguments.AddRange(install.Arguments);
        arguments.AddRange(["--configfile", staged.ConfigPath, "--add-source", staged.Folder, "--version", staged.Version.ToString()]);
        return arguments;
    }

    /// <summary>The helper call for this OS.</summary>
    public ProcessCall HelperCall(StagedUpdate staged, ToolInstall install, string dataFolder, int processId)
    {
        string log = Path.Combine(dataFolder, LogFileName);
        string pid = processId.ToString(CultureInfo.InvariantCulture);
        List<string> update = UpdateArguments(staged, install);
        if (!windows)
        {
            List<string> arguments = ["sh", "-c", UnixScript, "sh", pid, log, staged.Folder];
            arguments.AddRange(update);
            return new ProcessCall("nohup", arguments);
        }

        // The paths were checked by RefusePaths; quoting them is enough once none of "%!^&|<> is in them.
        string quotedLog = Quote(log);
        string updateLine = string.Join(' ', update.Select(a => a.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-') ? a : Quote(a)));
        string wait = $"for /l %i in (1,1,{WaitSeconds.ToString(CultureInfo.InvariantCulture)}) do @(tasklist /fi \"PID eq {pid}\" /nh | find \" {pid} \" >nul && ping -n 2 127.0.0.1 >nul)";
        string script =
            $"({wait}) <nul >>{quotedLog} 2>&1 & " +
            $"echo dotnet tool update {updateLine} >>{quotedLog} & " +
            $"dotnet tool update {updateLine} <nul >>{quotedLog} 2>&1 && echo update done >>{quotedLog} || echo update failed >>{quotedLog} & " +
            $"rmdir /s /q {Quote(staged.Folder)}";
        string commandLine = $"/d /s /c \"{script}\"";
        return new ProcessCall("cmd", ["/d", "/s", "/c", script], commandLine);
    }

    private static string Quote(string value) => "\"" + value + "\"";

    /// <summary>
    /// Writes <c>update-pending.json</c> (target version and time) to <paramref name="dataFolder"/>, then
    /// starts the helper once and returns without waiting. Returns the error text if the helper could
    /// not be started, or <c>null</c>.
    /// </summary>
    public string? Apply(StagedUpdate staged, ToolInstall install, string dataFolder, int processId, DateTimeOffset now)
    {
        try
        {
            WritePending(dataFolder, staged.Version, now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still install: only the report on the next start is lost.
        }

        try
        {
            launcher.Start(HelperCall(staged, install, dataFolder, processId));
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return ex.Message;
        }
    }

    private static void WritePending(string dataFolder, SemVersion version, DateTimeOffset now)
    {
        Directory.CreateDirectory(dataFolder);
        string json = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["version"] = version.ToString(),
            ["time"] = now.ToString("O", CultureInfo.InvariantCulture),
        });
        string path = Path.Combine(dataFolder, PendingFileName);
        string temp = path + ".tmp";
        File.WriteAllText(temp, json, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// The hint for the update a previous run started, or <c>null</c> if none is pending. The pending file
    /// is read through <see cref="CappedFile"/> and deleted. Grey <c>Updated to X.</c> if
    /// <paramref name="currentVersion"/> is the target, otherwise red. A broken file is deleted and ignored.
    /// </summary>
    public static string? ReadOutcome(string dataFolder, string currentVersion)
    {
        string path = Path.Combine(dataFolder, PendingFileName);
        string? target;
        try
        {
            byte[]? bytes = CappedFile.Read(path, PendingFileName, MaxPendingBytes);
            if (bytes is null)
            {
                return null;
            }

            target = ParseTarget(bytes);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            target = null;
        }

        try
        {
            // Deletes a file or a link (never its target); a missing file is no error.
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Shown again next time; the outcome stays the same.
        }

        if (target is null)
        {
            return null;
        }

        string shown = Spectre.Console.Markup.Escape(target);
        return string.Equals(currentVersion, target, StringComparison.Ordinal)
            ? $"[grey]Updated to {shown}.[/]"
            : $"[red]Update to {shown} failed; see update.log in the data folder.[/]";
    }

    // The target version, or null if the file is not {"version":"<SemVer>",...}.
    private static string? ParseTarget(byte[] bytes)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("version", out JsonElement version)
                && version.ValueKind == JsonValueKind.String
                && SemVersion.TryParse(version.GetString(), out SemVersion parsed))
            {
                return parsed.ToString();
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            return null;
        }

        return null;
    }
}
