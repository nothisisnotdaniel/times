namespace Times.Tests;

/// <summary>A unique path under the system temp folder, deleted on dispose. Never the real app data.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder(bool create = true)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Times.Tests", Guid.NewGuid().ToString("N"));
        if (create)
        {
            Directory.CreateDirectory(Path);
        }
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
