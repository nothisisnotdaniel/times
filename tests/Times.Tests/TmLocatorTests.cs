using Times.Tray;

namespace Times.Tests;

// Job 306, AC-3. The environment and profile are injected; the real PATH is never read.
public sealed class TmLocatorTests : IDisposable
{
    private static readonly bool Windows = OperatingSystem.IsWindows();
    private static readonly string FileName = Windows ? "tm.exe" : "tm";
    private static readonly char Separator = Windows ? ';' : ':';

    private readonly TempFolder root = new();

    public void Dispose() => root.Dispose();

    private string Folder(string name)
    {
        string path = Path.Combine(root.Path, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private string Tm(string folder)
    {
        string path = Path.Combine(Folder(folder), FileName);
        File.WriteAllText(path, "x");
        return path;
    }

    private static string? Find(Dictionary<string, string> environment, string profile) =>
        TmLocator.Find(name => environment.TryGetValue(name, out string? value) ? value : null, profile, Windows);

    [Fact]
    public void Variable_ComesFirst()
    {
        string fromVariable = Tm("variable");
        string profile = Folder("home");
        Tm(Path.Combine("home", ".dotnet", "tools"));
        string onPath = Path.GetDirectoryName(Tm("bin"))!;

        string? found = Find(new() { ["TIMES_TM"] = fromVariable, ["PATH"] = onPath }, profile);

        Assert.Equal(fromVariable, found);
    }

    [Fact]
    public void DotnetTools_BeforePath()
    {
        string profile = Folder("home");
        string tool = Tm(Path.Combine("home", ".dotnet", "tools"));
        string onPath = Path.GetDirectoryName(Tm("bin"))!;

        Assert.Equal(tool, Find(new() { ["PATH"] = onPath }, profile));
    }

    [Fact]
    public void Path_FirstAbsoluteEntryWithAFile()
    {
        string empty = Folder("empty");
        string first = Tm("first");
        Tm("second");
        string path = string.Join(Separator, empty, Path.GetDirectoryName(first)!, Path.Combine(root.Path, "second"));

        Assert.Equal(first, Find(new() { ["PATH"] = path }, Folder("home")));
    }

    [Fact]
    public void RelativeVariable_RelativePathEntries_AndDirectories_AreSkipped()
    {
        string profile = Folder("home");
        Folder(Path.Combine("home", ".dotnet", "tools", FileName));
        string directory = Folder(Path.Combine("dirbin", FileName));
        string relative = Path.Combine("rel", "bin");
        string last = Tm("last");
        string path = string.Join(Separator, relative, ".", Path.GetDirectoryName(directory)!, Path.GetDirectoryName(last)!);

        string? found = Find(new() { ["TIMES_TM"] = Path.Combine("bin", FileName), ["PATH"] = path }, profile);

        Assert.Equal(last, found);
    }

    [Fact]
    public void VariableNamingADirectory_IsSkipped()
    {
        string folder = Folder("adir");

        Assert.Null(Find(new() { ["TIMES_TM"] = folder }, Folder("home")));
    }

    [Fact]
    public void NothingMatches_Null()
    {
        Assert.Null(Find(new(), Folder("home")));
        Assert.Null(Find(new() { ["PATH"] = Folder("bin"), ["TIMES_TM"] = string.Empty }, string.Empty));
        Assert.Null(Find(new() { ["TIMES_TM"] = Path.Combine(root.Path, "missing", FileName) }, Folder("home")));
    }
}
