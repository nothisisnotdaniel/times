namespace Times.Tests;

public sealed class ToolInstallTests
{
    private static string Root => OperatingSystem.IsWindows() ? @"C:\" : "/";

    private static string P(params string[] parts) => Path.Combine([Root, .. parts]);

    // AC-8: a global tool sits under the global tools folder's .store and updates with -g.
    [Fact]
    public void Detect_GlobalTool_UsesG()
    {
        string tools = P("home", "daniel", ".dotnet", "tools");
        string dll = P("home", "daniel", ".dotnet", "tools", ".store", "times", "1.2.0", "times", "1.2.0", "tools", "net10.0", "any", "Times.dll");

        ToolInstall? install = ToolInstall.Detect(dll, tools);

        Assert.NotNull(install);
        Assert.True(install.Global);
        Assert.Equal(["-g"], install.Arguments);
        Assert.Equal(tools, install.Folder);
    }

    // AC-8: a global tools folder given with a trailing separator still matches.
    [Fact]
    public void Detect_GlobalFolderWithTrailingSeparator_StillGlobal()
    {
        string tools = P("home", "daniel", ".dotnet", "tools") + Path.DirectorySeparatorChar;
        string dll = P("home", "daniel", ".dotnet", "tools", ".store", "times", "1.2.0", "times", "1.2.0", "tools", "net10.0", "any", "Times.dll");

        Assert.True(ToolInstall.Detect(dll, tools)?.Global);
    }

    // AC-8: any other tools folder updates with --tool-path <dir>.
    [Fact]
    public void Detect_ToolPath_UsesToolPath()
    {
        string folder = P("opt", "my tools");
        string dll = P("opt", "my tools", ".store", "times", "0.1.1-develop.39", "times", "0.1.1-develop.39", "tools", "net10.0", "any", "Times.dll");

        ToolInstall? install = ToolInstall.Detect(dll, P("home", "daniel", ".dotnet", "tools"));

        Assert.NotNull(install);
        Assert.False(install.Global);
        Assert.Equal(["--tool-path", folder], install.Arguments);
        Assert.Equal(folder, install.Folder);
    }

    // AC-8: with no global folder known, a store install is a --tool-path one.
    [Fact]
    public void Detect_NoGlobalFolder_IsToolPath()
    {
        string dll = P("t", ".store", "times", "1.0.0", "times", "1.0.0", "tools", "net10.0", "any", "Times.dll");

        Assert.Equal(["--tool-path", P("t")], ToolInstall.Detect(dll, null)?.Arguments);
    }

    // AC-8: dotnet run, tests, a single-file or relative location: not an installed tool.
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Times.dll")]
    [InlineData("bin/Debug/net10.0/Times.dll")]
    public void Detect_NotATool_IsNull(string? location)
    {
        Assert.Null(ToolInstall.Detect(location, P("home", ".dotnet", "tools")));
    }

    // AC-8
    [Fact]
    public void Detect_BuildOutputOrOtherStores_IsNull()
    {
        Assert.Null(ToolInstall.Detect(P("src", "Times", "bin", "Debug", "net10.0", "Times.dll"), null));
        Assert.Null(ToolInstall.Detect(P("t", ".store", "other", "1.0.0", "Times.dll"), null));
        Assert.Null(ToolInstall.Detect(P("t", "store", "times", "1.0.0", "Times.dll"), null));
    }

    // AC-8: the test host's copy (not an installed tool) is not detected.
    [Fact]
    public void Current_InTests_IsNull()
    {
        Assert.Null(ToolInstall.Current());
    }

    // AC-8: DOTNET_CLI_HOME wins over the user profile; relative values are ignored.
    [Fact]
    public void GlobalToolsFolder_FromCliHomeOrProfile()
    {
        Assert.Equal(Path.Combine(P("cli"), ".dotnet", "tools"), ToolInstall.GlobalToolsFolder(P("cli"), P("home")));
        Assert.Equal(Path.Combine(P("home"), ".dotnet", "tools"), ToolInstall.GlobalToolsFolder(null, P("home")));
        Assert.Equal(Path.Combine(P("home"), ".dotnet", "tools"), ToolInstall.GlobalToolsFolder("relative", P("home")));
        Assert.Null(ToolInstall.GlobalToolsFolder(null, ""));
    }
}
