namespace Times.Tests;

/// <summary>Checks that the README install section tells users how to verify the downloaded package.</summary>
public sealed class ReadmeInstallTests
{
    private const string OpenSsl =
        "openssl dgst -sha256 -verify update-key.pem -signature Times.<version>.nupkg.sig Times.<version>.nupkg";

    private const string InstallCommand = "dotnet tool install --global --configfile";

    // AC-1
    [Fact]
    public void Install_DownloadsTheSignature() => Assert.Contains(".nupkg.sig", Section());

    // AC-2
    [Fact]
    public void Install_GivesChecksumCommandsAndDigestComparison()
    {
        string section = Section();

        Assert.Contains("shasum -a 256 Times.<version>.nupkg", section);
        Assert.Contains("certutil -hashfile Times.<version>.nupkg SHA256", section);
        Assert.Contains("Get-FileHash", section);
        Assert.Contains("digest", section);
        Assert.Contains("gh release view", section);
    }

    // AC-3
    [Fact]
    public void Install_GivesSignatureCheck()
    {
        string section = Section();

        Assert.Contains(OpenSsl, section);
        Assert.Contains("src/Times/update-key.pem", section);
        Assert.Contains("Verified OK", section);
    }

    // AC-4
    [Fact]
    public void Install_SaysNotToInstallOnFailure()
    {
        string section = Section();

        Assert.Contains("do not install", section);
        Assert.Contains("Install only when both checks pass", section);
    }

    // AC-6
    [Fact]
    public void Install_KeepsExistingContentAndChecksBeforeInstall()
    {
        string section = Section();

        Assert.Contains("<clear />", section);
        Assert.Contains("nuget.config", section);
        Assert.Contains("This is safe because NuGet only looks in your download folder", section);
        int install = section.IndexOf(InstallCommand, StringComparison.Ordinal);
        Assert.True(install >= 0);
        int check = section.IndexOf(OpenSsl, StringComparison.Ordinal);
        Assert.True(check >= 0);
        Assert.True(check < install);
    }

    // AC-7
    [Fact]
    public void Install_ProseLinesAreAtMost100Characters()
    {
        bool inCode = false;
        foreach (string line in Section().ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inCode = !inCode;
            }
            else if (!inCode)
            {
                Assert.True(line.Length <= 100, "Line too long: " + line);
            }
        }
    }

    private const string ReleasedVersions =
        "Versions of Times that are already released keep DevOps sync working as they are; only new releases need a Suite Pro key for it.";

    // Job 328 AC-8: README and MANUAL say which DevOps parts need Suite Pro, and that released versions keep working.
    [Theory]
    [InlineData("README.md")]
    [InlineData("MANUAL.md")]
    public void Docs_MarkDevOpsSyncAsPro_AndKeepReleasedVersionsWorking(string name)
    {
        string text = string.Join(' ', ReadRepoFile(name).ReplaceLineEndings("\n").Split('\n').Select(l => l.Trim()));

        Assert.Contains(ReleasedVersions, text);
        Assert.Contains("`sync`, `devops connect` and `devops import` need **Suite Pro**", text);
    }

    private static string Section()
    {
        string readme = ReadRepoFile("README.md").ReplaceLineEndings("\n");
        int start = readme.IndexOf("### Install from a GitHub release", StringComparison.Ordinal);
        Assert.True(start >= 0, "install heading missing");
        int end = readme.IndexOf("\n### ", start + 5, StringComparison.Ordinal);
        return end < 0 ? readme[start..] : readme[start..end];
    }

    private static string ReadRepoFile(string name)
    {
        DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Times.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "Times.slnx not found above " + AppContext.BaseDirectory);
        return File.ReadAllText(Path.Combine(directory.FullName, name));
    }
}
