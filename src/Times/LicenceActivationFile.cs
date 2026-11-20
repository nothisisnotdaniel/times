using System.Text;
using System.Text.Json;

namespace Times;

/// <summary>What <c>activation.json</c> says about the activated licence. Shown only; Pro never trusts it.</summary>
internal sealed class LicenceActivation
{
    public LicenceActivation(string licenceId, string licensee, DateOnly updatesUntil)
    {
        LicenceId = licenceId;
        Licensee = licensee;
        UpdatesUntil = updatesUntil;
    }

    public string LicenceId { get; }

    public string Licensee { get; }

    public DateOnly UpdatesUntil { get; }
}

/// <summary>
/// <c>&lt;ApplicationData&gt;/SuitePro/activation.json</c>, shared with Context:
/// <c>{"format":1,"licenceId":…,"licensee":…,"updatesUntil":"yyyy-MM-dd"}</c>. On Unix the folder is
/// <c>0700</c> and the file <c>0600</c>. It is written to a temp file and moved into place, and read through
/// <see cref="CappedFile"/> with a 4 KiB cap. The folder is injected; <c>TIMES_DATA_DIR</c> does not move it.
/// The gate (<see cref="Pro.IsUnlocked"/>) reads the stored key, never this file.
/// </summary>
internal sealed class LicenceActivationFile
{
    public const string FolderName = "SuitePro";
    public const string FileName = "activation.json";
    public const int Format = 1;
    public const long MaxBytes = 4 * 1024;

    private const UnixFileMode FolderMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileMode600 = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public LicenceActivationFile(string folder)
    {
        Folder = folder;
    }

    /// <summary>The folder the file lives in.</summary>
    public string Folder { get; }

    public string FilePath => Path.Combine(Folder, FileName);

    /// <summary><c>SuitePro</c> under the user's application data folder (never the Times data folder).</summary>
    public static string DefaultFolder(string applicationData) => Path.Combine(applicationData, FolderName);

    /// <summary>Writes the file for <paramref name="licence"/>, replacing an earlier one. Throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>.</summary>
    public void Write(Licence licence)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(Folder);
        }
        else
        {
            Directory.CreateDirectory(Folder, FolderMode);
            File.SetUnixFileMode(Folder, FolderMode);
        }

        byte[] bytes = Serialize(licence);
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

            File.Move(temp, FilePath, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>Deletes the file. False if there was none.</summary>
    public bool Delete()
    {
        if (!File.Exists(FilePath))
        {
            return false;
        }

        File.Delete(FilePath);
        return true;
    }

    /// <summary>
    /// The file's contents, or <c>null</c> when it is missing. Throws <see cref="InvalidDataException"/>
    /// when it is not a regular file, too large or not in the format.
    /// </summary>
    public LicenceActivation? Read()
    {
        byte[]? bytes = CappedFile.Read(FilePath, FileName, MaxBytes);
        if (bytes is null)
        {
            return null;
        }

        return Parse(bytes) ?? throw new InvalidDataException($"{FileName} is not readable.");
    }

    /// <summary>The exact bytes written for <paramref name="licence"/> (UTF-8, no BOM).</summary>
    internal static byte[] Serialize(Licence licence)
    {
        using MemoryStream buffer = new MemoryStream();
        using (Utf8JsonWriter writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("format", Format);
            writer.WriteString("licenceId", licence.LicenceId);
            writer.WriteString("licensee", licence.Name);
            writer.WriteString("updatesUntil", LicenceKey.FormatDate(licence.Until));
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    // Strict: one object with exactly the four properties, each once.
    private static LicenceActivation? Parse(byte[] bytes)
    {
        try
        {
            _ = new UTF8Encoding(false, true).GetString(bytes);
            Utf8JsonReader reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 2 });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return null;
            }

            int? format = null;
            string? licenceId = null;
            string? licensee = null;
            string? until = null;
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                string property = reader.GetString()!;
                if (!seen.Add(property) || !reader.Read())
                {
                    return null;
                }

                if (property == "format")
                {
                    if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out int number))
                    {
                        return null;
                    }

                    format = number;
                    continue;
                }

                if (reader.TokenType != JsonTokenType.String)
                {
                    return null;
                }

                string value = reader.GetString()!;
                switch (property)
                {
                    case "licenceId":
                        licenceId = value;
                        break;
                    case "licensee":
                        licensee = value;
                        break;
                    case "updatesUntil":
                        until = value;
                        break;
                    default:
                        return null;
                }
            }

            if (reader.TokenType != JsonTokenType.EndObject || reader.Read())
            {
                return null;
            }

            if (format != Format || !LicenceKey.IsValidLicenceId(licenceId) || !LicenceKey.IsValidName(licensee)
                || !LicenceKey.TryParseDate(until, out DateOnly untilDate))
            {
                return null;
            }

            return new LicenceActivation(licenceId!, licensee!, untilDate);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or DecoderFallbackException)
        {
            return null;
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
