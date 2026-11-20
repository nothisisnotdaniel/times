using System.Text;

namespace Times.Tests;

// Job 327, AC-4: teams.json, the private pairing file. Each test has its own temp folder.
public sealed class TeamsPairingTests : IDisposable
{
    private const string Token = "SECRET-token-327";

    private readonly TempFolder data = new();
    private readonly TempFolder outside = new();
    private readonly TeamsPairing pairing;

    public TeamsPairingTests()
    {
        pairing = new TeamsPairing(data.Path);
    }

    public void Dispose()
    {
        data.Dispose();
        outside.Dispose();
    }

    private void WriteRaw(string text) => File.WriteAllText(pairing.FilePath, text, new UTF8Encoding(false));

    [Fact]
    public void Read_NoFile_IsOff()
    {
        Assert.Equal(TeamsPairingKind.Off, pairing.Read(out string? token));
        Assert.Null(token);
        Assert.False(pairing.Exists);
    }

    // AC-1, AC-4
    [Fact]
    public void Create_WritesANullToken_Privately()
    {
        pairing.Create();

        Assert.Equal("{\"token\":null}", File.ReadAllText(pairing.FilePath));
        Assert.Equal(TeamsPairingKind.NotPaired, pairing.Read(out string? token));
        Assert.Null(token);
        Assert.Empty(Directory.GetFiles(data.Path, "*.tmp"));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(pairing.FilePath));
        }
    }

    // AC-4
    [Fact]
    public void SaveToken_Valid_IsSavedPrivately_AndReadBack()
    {
        pairing.Create();

        Assert.True(pairing.SaveToken(Token));

        Assert.Equal(TeamsPairingKind.Paired, pairing.Read(out string? token));
        Assert.Equal(Token, token);
        Assert.Empty(Directory.GetFiles(data.Path, "*.tmp"));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(pairing.FilePath));
        }
    }

    // AC-4: on Unix a token saved over a file someone opened up is 0600 again (a new file is moved in).
    [Fact]
    public void SaveToken_OverAWideOpenFile_Is0600()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        pairing.Create();
        File.SetUnixFileMode(pairing.FilePath, (UnixFileMode)0b110_110_110);

        Assert.True(pairing.SaveToken(Token));

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(pairing.FilePath));
    }

    // AC-4
    [Theory]
    [InlineData("")]
    [InlineData("with space")]
    [InlineData("ctrl\u0001")]
    [InlineData("non-ascii-é")]
    public void SaveToken_Invalid_IsIgnored(string token)
    {
        pairing.Create();

        Assert.False(pairing.SaveToken(token));

        Assert.Equal("{\"token\":null}", File.ReadAllText(pairing.FilePath));
    }

    // AC-4
    [Fact]
    public void SaveToken_TooLong_IsIgnored()
    {
        pairing.Create();

        Assert.False(pairing.SaveToken(new string('x', 513)));
        Assert.True(pairing.SaveToken(new string('x', 512)));
    }

    // AC-1: after teams disconnect a late token never brings the file back.
    [Fact]
    public void SaveToken_WithoutAFile_CreatesNothing()
    {
        Assert.False(pairing.SaveToken(Token));

        Assert.False(File.Exists(pairing.FilePath));
    }

    // AC-4
    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"token\":42}")]
    [InlineData("{\"token\":\"has space\"}")]
    [InlineData("{\"token\":null,\"token\":null}")]
    [InlineData("{\"token\":null,\"other\":1}")]
    [InlineData("{\"token\":null} {}")]
    public void Malformed_IsBroken_AndNeverOverwrittenByAToken(string text)
    {
        WriteRaw(text);

        Assert.Equal(TeamsPairingKind.Broken, pairing.Read(out string? token));
        Assert.Null(token);
        Assert.False(pairing.SaveToken(Token));
        Assert.Equal(text, File.ReadAllText(pairing.FilePath));
    }

    // AC-4
    [Fact]
    public void Oversize_IsBroken_AndNeverOverwrittenByAToken()
    {
        string big = "{\"token\":null" + new string(' ', 70 * 1024) + "}";
        WriteRaw(big);

        Assert.Equal(TeamsPairingKind.Broken, pairing.Read(out _));
        Assert.False(pairing.SaveToken(Token));
        Assert.Equal(big.Length, new FileInfo(pairing.FilePath).Length);
    }

    // AC-4: a link (even to a good file) counts as not paired and is never written through.
    [Fact]
    public void Link_IsBroken_AndItsTargetIsNeverWritten()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string target = Path.Combine(outside.Path, "target.json");
        File.WriteAllText(target, "{\"token\":\"other-token\"}");
        File.CreateSymbolicLink(pairing.FilePath, target);

        Assert.Equal(TeamsPairingKind.Broken, pairing.Read(out string? token));
        Assert.Null(token);
        Assert.False(pairing.SaveToken(Token));
        Assert.Equal("{\"token\":\"other-token\"}", File.ReadAllText(target));
    }

    // AC-4
    [Fact]
    public void Fifo_IsBroken_WithoutBlocking()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        SpecialFiles.MakeFifo(pairing.FilePath);
        TeamsPairingKind kind = TeamsPairingKind.Off;

        Assert.Null(SpecialFiles.RunWithin(FakeTeams.Bound, () => kind = pairing.Read(out _)));
        Assert.Equal(TeamsPairingKind.Broken, kind);
        Assert.False(pairing.SaveToken(Token));
        Assert.True(SpecialFiles.IsFifo(pairing.FilePath));
    }

    // AC-4: a folder named teams.json is broken; disconnect cannot delete it.
    [Fact]
    public void Directory_IsBroken_AndDeleteThrows()
    {
        Directory.CreateDirectory(pairing.FilePath);

        Assert.Equal(TeamsPairingKind.Broken, pairing.Read(out _));
        Assert.Throws<IOException>(() => pairing.Delete());
        Assert.True(Directory.Exists(pairing.FilePath));
    }

    // AC-4: teams connect replaces a broken file (a link itself, never its target).
    [Fact]
    public void Create_ReplacesABrokenFile_AndALinkItself()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string target = Path.Combine(outside.Path, "target.json");
        File.WriteAllText(target, "keep");
        File.CreateSymbolicLink(pairing.FilePath, target);

        pairing.Create();

        Assert.Null(new FileInfo(pairing.FilePath).LinkTarget);
        Assert.Equal(TeamsPairingKind.NotPaired, pairing.Read(out _));
        Assert.Equal("keep", File.ReadAllText(target));
    }

    // AC-1
    [Fact]
    public void Delete_RemovesTheFile_AndALinkNotItsTarget()
    {
        pairing.Create();
        Assert.True(pairing.Delete());
        Assert.False(pairing.Exists);
        Assert.False(pairing.Delete());

        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string target = Path.Combine(outside.Path, "target.json");
        File.WriteAllText(target, "keep");
        File.CreateSymbolicLink(pairing.FilePath, target);

        Assert.True(pairing.Delete());

        Assert.False(pairing.Exists);
        Assert.Equal("keep", File.ReadAllText(target));
    }
}
