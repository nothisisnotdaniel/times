using System.Text;

namespace Times.Tests;

public sealed class LicenceActivationFileTests : IDisposable
{
    private readonly TempFolder root = new();

    public void Dispose() => root.Dispose();

    private static Licence Sample(string name = "Ada Lovelace") =>
        new Licence("test", TestLicences.LicenceId, name, new DateOnly(2027, 12, 31), new DateOnly(2026, 10, 27));

    private string Folder => Path.Combine(root.Path, LicenceActivationFile.FolderName);

    // AC-3
    [Fact]
    public void Write_CreatesTheFolderAndTheExactJson()
    {
        LicenceActivationFile file = new LicenceActivationFile(Folder);

        file.Write(Sample());

        Assert.Equal(Path.Combine(Folder, "activation.json"), file.FilePath);
        Assert.Equal(
            "{\"format\":1,\"licenceId\":\"AAECAwQFBgcICQoLDA0ODw\",\"licensee\":\"Ada Lovelace\",\"updatesUntil\":\"2027-12-31\"}",
            File.ReadAllText(file.FilePath, Encoding.UTF8));
        Assert.Single(Directory.GetFiles(Folder));
    }

    // AC-3
    [Fact]
    public void Write_OnUnix_Folder0700_File0600()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(Folder);
        File.SetUnixFileMode(Folder, (UnixFileMode)0b111_101_101);
        LicenceActivationFile file = new LicenceActivationFile(Folder);

        file.Write(Sample());
        file.Write(Sample("Grace Hopper"));

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Folder));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file.FilePath));
    }

    [Fact]
    public void Read_RoundTrips_MissingIsNull()
    {
        LicenceActivationFile file = new LicenceActivationFile(Folder);
        Assert.Null(file.Read());

        file.Write(Sample("Zoë"));
        LicenceActivation read = file.Read()!;

        Assert.Equal(TestLicences.LicenceId, read.LicenceId);
        Assert.Equal("Zoë", read.Licensee);
        Assert.Equal(new DateOnly(2027, 12, 31), read.UpdatesUntil);
    }

    [Theory]
    [InlineData("{ corrupt")]
    [InlineData("{\"format\":2,\"licenceId\":\"AAECAwQFBgcICQoLDA0ODw\",\"licensee\":\"Ada\",\"updatesUntil\":\"2027-12-31\"}")]
    [InlineData("{\"format\":1,\"licenceId\":\"AAECAwQFBgcICQoLDA0ODw\",\"licensee\":\"Ada\",\"updatesUntil\":\"2027-12-31\",\"x\":1}")]
    [InlineData("{\"format\":1,\"format\":1,\"licenceId\":\"AAECAwQFBgcICQoLDA0ODw\",\"licensee\":\"Ada\",\"updatesUntil\":\"2027-12-31\"}")]
    [InlineData("{\"format\":1,\"licenceId\":\"AAECAwQFBgcICQoLDA0ODw\",\"licensee\":\"Ada\"}")]
    public void Read_BrokenFile_Throws(string json)
    {
        Directory.CreateDirectory(Folder);
        LicenceActivationFile file = new LicenceActivationFile(Folder);
        File.WriteAllText(file.FilePath, json);

        Assert.Throws<InvalidDataException>(() => file.Read());
    }

    [Fact]
    public void Read_IsCappedAt4KiB()
    {
        Directory.CreateDirectory(Folder);
        LicenceActivationFile file = new LicenceActivationFile(Folder);
        File.WriteAllText(file.FilePath, new string(' ', 4097));

        Assert.Throws<InvalidDataException>(() => file.Read());
    }

    // AC-10
    [Fact]
    public void Delete_RemovesTheFile_FalseWhenNone()
    {
        LicenceActivationFile file = new LicenceActivationFile(Folder);
        Assert.False(file.Delete());

        file.Write(Sample());
        Assert.True(file.Delete());
        Assert.False(File.Exists(file.FilePath));
    }

    [Fact]
    public void DefaultFolder_IsSuiteProUnderApplicationData()
    {
        string appData = Path.Combine(root.Path, "AppData");

        Assert.Equal(Path.Combine(appData, "SuitePro"), LicenceActivationFile.DefaultFolder(appData));
    }
}
