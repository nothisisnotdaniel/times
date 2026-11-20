using System.Text;
using System.Text.Json;

namespace Times;

/// <summary>What <c>teams.json</c> says.</summary>
internal enum TeamsPairingKind
{
    /// <summary>No <c>teams.json</c>: Teams tracking is off.</summary>
    Off,

    /// <summary>A readable file with a null token: on, waiting to be paired.</summary>
    NotPaired,

    /// <summary>A readable file with a valid token.</summary>
    Paired,

    /// <summary>
    /// A link, directory or other non-regular entry, a file over the cap, or one not in the format. Counts as on
    /// and not paired, and is never overwritten except by <c>teams connect</c> or <c>teams disconnect</c>.
    /// </summary>
    Broken,
}

/// <summary>
/// <c>teams.json</c> in the data folder: <c>{"token":null}</c> or <c>{"token":"…"}</c>. Its presence turns Teams
/// tracking on; the token is the pairing Teams gave. Read through <see cref="CappedFile"/> (64 KiB cap) after an
/// lstat-style check, so a link counts as broken; written to a temp file and moved into place, 0600 on Unix. The
/// token is never shown, logged or put into an exception message.
/// </summary>
internal sealed class TeamsPairing
{
    public const string FileName = "teams.json";
    public const long MaxBytes = 64 * 1024;

    private const UnixFileMode FileMode600 = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public TeamsPairing(string folder)
    {
        Folder = folder;
    }

    public string Folder { get; }

    public string FilePath => Path.Combine(Folder, FileName);

    /// <summary>True when anything (even a broken entry) is at <see cref="FilePath"/>.</summary>
    public bool Exists => FileKind.OfEntry(FilePath) != FileKindResult.Missing;

    /// <summary>What the file says now. Never throws; the token is set only for <see cref="TeamsPairingKind.Paired"/>.</summary>
    public TeamsPairingKind Read(out string? token)
    {
        token = null;
        FileKindResult entry = FileKind.OfEntry(FilePath);
        if (entry == FileKindResult.Missing)
        {
            return TeamsPairingKind.Off;
        }

        if (entry != FileKindResult.Regular)
        {
            return TeamsPairingKind.Broken;
        }

        byte[]? bytes;
        try
        {
            bytes = CappedFile.Read(FilePath, FileName, MaxBytes);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return TeamsPairingKind.Broken;
        }

        if (bytes is null)
        {
            return TeamsPairingKind.Off;
        }

        if (!TryParse(bytes, out string? stored))
        {
            return TeamsPairingKind.Broken;
        }

        token = stored;
        return stored is null ? TeamsPairingKind.NotPaired : TeamsPairingKind.Paired;
    }

    /// <summary>
    /// <c>teams connect</c>: writes <c>{"token":null}</c>, replacing whatever entry is there (a broken file or a
    /// link itself, never what a link points to). Throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>.
    /// </summary>
    public void Create() => Write(null);

    /// <summary>
    /// Saves a token Teams sent, only when it is valid and the file is still a readable <c>teams.json</c>
    /// (not broken, not removed). Returns true if it was saved. Never throws.
    /// </summary>
    public bool SaveToken(string? token)
    {
        if (!TeamsProtocol.IsValidToken(token))
        {
            return false;
        }

        TeamsPairingKind kind = Read(out _);
        if (kind is not (TeamsPairingKind.NotPaired or TeamsPairingKind.Paired))
        {
            return false;
        }

        try
        {
            Write(token);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// <c>teams disconnect</c>: deletes the entry (a link itself, not its target). False if there was none.
    /// Throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> (for example for a directory).
    /// </summary>
    public bool Delete()
    {
        if (!Exists)
        {
            return false;
        }

        if (Directory.Exists(FilePath) && new FileInfo(FilePath).LinkTarget is null)
        {
            throw new IOException($"{FileName} is a folder.");
        }

        File.Delete(FilePath);
        return true;
    }

    /// <summary>The exact bytes written for <paramref name="token"/> (UTF-8, no BOM).</summary>
    internal static byte[] Serialize(string? token)
    {
        using MemoryStream buffer = new MemoryStream();
        using (Utf8JsonWriter writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (token is null)
            {
                writer.WriteNull("token");
            }
            else
            {
                writer.WriteString("token", token);
            }

            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    // Strict: one object holding exactly "token", once, as null or a valid token; strict UTF-8.
    internal static bool TryParse(byte[] bytes, out string? token)
    {
        token = null;
        try
        {
            _ = new UTF8Encoding(false, true).GetString(bytes);
            Utf8JsonReader reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 2 });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            bool seen = false;
            string? value = null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (seen || reader.GetString() != "token" || !reader.Read())
                {
                    return false;
                }

                seen = true;
                if (reader.TokenType == JsonTokenType.Null)
                {
                    value = null;
                }
                else if (reader.TokenType == JsonTokenType.String)
                {
                    value = reader.GetString();
                    if (!TeamsProtocol.IsValidToken(value))
                    {
                        return false;
                    }
                }
                else
                {
                    return false;
                }
            }

            if (!seen || reader.TokenType != JsonTokenType.EndObject || reader.Read())
            {
                return false;
            }

            token = value;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or DecoderFallbackException)
        {
            return false;
        }
    }

    private void Write(string? token)
    {
        Directory.CreateDirectory(Folder);
        byte[] bytes = Serialize(token);
        string temp = Path.Combine(Folder, $".{FileName}.{Guid.NewGuid():N}.tmp");
        FileStreamOptions options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = FileMode600;
        }

        try
        {
            using (FileStream stream = new FileStream(temp, options))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (Directory.Exists(FilePath) && new FileInfo(FilePath).LinkTarget is null)
            {
                throw new IOException($"{FileName} is a folder.");
            }

            File.Move(temp, FilePath, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a leftover temp file is never read.
        }
    }
}
