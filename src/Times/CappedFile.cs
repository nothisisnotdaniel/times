namespace Times;

/// <summary>
/// Reads a small data file (such as <c>projects.json</c> or <c>settings.json</c>) defensively: the
/// path's final link target must be a regular file, checked with <see cref="FileKind"/> before it
/// is opened (so a FIFO or a device never blocks), and at most a given number of bytes are taken
/// in, whatever its reported size.
/// </summary>
internal static class CappedFile
{
    /// <summary>
    /// The bytes of the file at <paramref name="path"/>, or <c>null</c> if nothing is there. Throws
    /// <see cref="InvalidDataException"/> with <c>&lt;fileName&gt; is not a regular file.</c> or
    /// <c>&lt;fileName&gt; is too large.</c> when it is not a regular final link target or holds more
    /// than <paramref name="maxBytes"/> bytes (by its length or by what the capped read got).
    /// </summary>
    public static byte[]? Read(string path, string fileName, long maxBytes)
    {
        FileKindResult kind = FileKind.Of(path);
        if (kind == FileKindResult.Missing)
        {
            return null;
        }

        if (kind != FileKindResult.Regular)
        {
            throw new InvalidDataException($"{fileName} is not a regular file.");
        }

        FileInfo file = new FileInfo(path);
        FileSystemInfo target = file.LinkTarget is null ? file : file.ResolveLinkTarget(returnFinalTarget: true) ?? file;
        if (target is FileInfo targetFile && targetFile.Length > maxBytes)
        {
            throw new InvalidDataException($"{fileName} is too large.");
        }

        byte[] bytes;
        using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            bytes = ReadAtMost(stream, maxBytes + 1);
        }

        if (bytes.Length > maxBytes)
        {
            throw new InvalidDataException($"{fileName} is too large.");
        }

        return bytes;
    }

    /// <summary>
    /// Reads from <paramref name="stream"/> until it ends or <paramref name="limit"/> bytes have
    /// arrived, whichever comes first. Never asks the stream for more than <paramref name="limit"/> bytes.
    /// </summary>
    public static byte[] ReadAtMost(Stream stream, long limit)
    {
        using MemoryStream result = new MemoryStream();
        byte[] buffer = new byte[8192];
        while (result.Length < limit)
        {
            int wanted = (int)Math.Min(buffer.Length, limit - result.Length);
            int read = stream.Read(buffer, 0, wanted);
            if (read == 0)
            {
                break;
            }

            result.Write(buffer, 0, read);
        }

        return result.ToArray();
    }
}
