using System.Runtime.InteropServices;

namespace Times.Tests;

public sealed class FileKindTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void RegularFile_IsRegular()
    {
        using TempFolder temp = new TempFolder();
        string path = Path.Combine(temp.Path, "a.json");
        File.WriteAllText(path, "{}");

        Assert.Equal(FileKindResult.Regular, FileKind.Of(path));
    }

    [Fact]
    public void EmptyRegularFile_IsRegular()
    {
        using TempFolder temp = new TempFolder();
        string path = Path.Combine(temp.Path, "a.json");
        File.WriteAllBytes(path, []);

        Assert.Equal(FileKindResult.Regular, FileKind.Of(path));
    }

    [Fact]
    public void Directory_IsOther()
    {
        using TempFolder temp = new TempFolder();

        Assert.Equal(FileKindResult.Other, FileKind.Of(temp.Path));
    }

    [Fact]
    public void MissingFile_IsMissing()
    {
        using TempFolder temp = new TempFolder();

        Assert.Equal(FileKindResult.Missing, FileKind.Of(Path.Combine(temp.Path, "nope.json")));
    }

    [Fact]
    public void MissingFolder_IsMissing()
    {
        using TempFolder temp = new TempFolder(create: false);

        Assert.Equal(FileKindResult.Missing, FileKind.Of(Path.Combine(temp.Path, "nope.json")));
    }

    [Fact]
    public void Fifo_IsOther_WithoutBlocking()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TempFolder temp = new TempFolder();
        string path = Path.Combine(temp.Path, "fifo");
        SpecialFiles.MakeFifo(path);
        FileKindResult kind = FileKindResult.Regular;

        Assert.Null(SpecialFiles.RunWithin(Timeout, () => kind = FileKind.Of(path)));

        Assert.Equal(FileKindResult.Other, kind);
    }

    [Fact]
    public void LinkToRegularFile_IsRegular()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TempFolder temp = new TempFolder();
        string target = Path.Combine(temp.Path, "target.json");
        File.WriteAllText(target, "{}");
        string link = Path.Combine(temp.Path, "link.json");
        File.CreateSymbolicLink(link, target);

        Assert.Equal(FileKindResult.Regular, FileKind.Of(link));
    }

    [Theory]
    [InlineData("/dev/null")]
    [InlineData("/dev/zero")]
    public void LinkToDevice_IsOther(string device)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TempFolder temp = new TempFolder();
        string link = Path.Combine(temp.Path, "link.json");
        File.CreateSymbolicLink(link, device);

        Assert.Equal(FileKindResult.Other, FileKind.Of(link));
    }

    [Fact]
    public void DanglingLink_IsOther()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TempFolder temp = new TempFolder();
        string link = Path.Combine(temp.Path, "link.json");
        File.CreateSymbolicLink(link, Path.Combine(temp.Path, "gone.json"));

        Assert.Equal(FileKindResult.Other, FileKind.Of(link));
    }

    [Fact]
    public void ModeOffset_IsKnown_OnlyForSupportedPlatforms()
    {
        if (OperatingSystem.IsMacOS())
        {
            Assert.Equal(4, FileKind.ModeOffset(Architecture.Arm64));
            Assert.Null(FileKind.ModeOffset(Architecture.X64));
        }
        else if (OperatingSystem.IsLinux())
        {
            Assert.Equal(24, FileKind.ModeOffset(Architecture.X64));
            Assert.Equal(16, FileKind.ModeOffset(Architecture.Arm64));
        }

        Assert.Null(FileKind.ModeOffset(Architecture.X86));
        Assert.Null(FileKind.ModeOffset(Architecture.Arm));
        Assert.Null(FileKind.ModeOffset(Architecture.RiscV64));
    }
}
