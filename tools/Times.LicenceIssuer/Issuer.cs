using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Times.LicenceIssuer;

/// <summary>
/// The developer-only Suite Pro licence issuer. <c>create-key --kid &lt;id&gt;</c> makes a P-256 signing key in
/// the store and prints only its public PEM (paste it into <c>LicencePublicKeys.cs</c>);
/// <c>issue --kid &lt;id&gt; --name &lt;n&gt; --until &lt;yyyy-MM-dd&gt; [--ledger &lt;abs path&gt;]</c> signs a
/// <c>CTX1-</c> token, appends <c>lid, name, until, issued, kid</c> to the ledger and prints the token.
/// Output is plain text (no markup or wrapping) so a token or PEM copies as one piece. All signing code
/// lives here, never in Times.
/// </summary>
internal sealed class Issuer
{
    public const string LedgerFolderName = "SuiteProIssuer";
    public const string LedgerFileName = "ledger.tsv";

    private const string Usage =
        "Usage: create-key --kid <id> | issue --kid <id> --name <licensee> --until <yyyy-MM-dd> [--ledger <absolute path>]";

    private readonly TextWriter output;
    private readonly TextWriter error;
    private readonly ISigningKeyStore keys;
    private readonly RandomNumberGenerator random;
    private readonly TimeProvider time;
    private readonly string defaultLedger;

    public Issuer(TextWriter output, TextWriter error, ISigningKeyStore keys, RandomNumberGenerator random,
        TimeProvider time, string defaultLedger)
    {
        this.output = output;
        this.error = error;
        this.keys = keys;
        this.random = random;
        this.time = time;
        this.defaultLedger = defaultLedger;
    }

    /// <summary><c>&lt;ApplicationData&gt;/SuiteProIssuer/ledger.tsv</c>.</summary>
    public static string DefaultLedger(string applicationData) =>
        Path.Combine(applicationData, LedgerFolderName, LedgerFileName);

    /// <summary>Runs one command. 0 on success, 1 when refused, 2 for a usage error.</summary>
    public int Run(string[] args)
    {
        if (args.Length == 0)
        {
            return Fail(2, Usage);
        }

        if (!TryOptions(args[1..], out Dictionary<string, string> options, out string? problem))
        {
            return Fail(2, problem!);
        }

        return args[0] switch
        {
            "create-key" => CreateKey(options),
            "issue" => Issue(options),
            _ => Fail(2, Usage),
        };
    }

    private int CreateKey(Dictionary<string, string> options)
    {
        if (!Only(options, "--kid") || !options.TryGetValue("--kid", out string? kid))
        {
            return Fail(2, "create-key takes only --kid <id>.");
        }

        if (!LicenceKey.IsValidKeyId(kid))
        {
            return Fail(1, "The key id must be 1-16 characters of a-z, 0-9 and -.");
        }

        if (!keys.IsSupported || keys.WriteRefusal is not null)
        {
            return Fail(1, keys.WriteRefusal ?? UnsupportedSigningKeyStore.NotAvailable);
        }

        try
        {
            if (keys.Exists(kid))
            {
                return Fail(1, $"A signing key named {kid} already exists; it was left as it is.");
            }

            using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            byte[] pkcs8 = key.ExportPkcs8PrivateKey();
            try
            {
                keys.Write(kid, pkcs8);
            }
            finally
            {
                Array.Clear(pkcs8);
            }

            output.WriteLine(key.ExportSubjectPublicKeyInfoPem());
            return 0;
        }
        catch (TokenStoreException ex)
        {
            return Fail(1, ex.Message);
        }
    }

    private int Issue(Dictionary<string, string> options)
    {
        if (!Only(options, "--kid", "--name", "--until", "--ledger")
            || !options.TryGetValue("--kid", out string? kid)
            || !options.TryGetValue("--name", out string? name)
            || !options.TryGetValue("--until", out string? untilText))
        {
            return Fail(2, "issue needs --kid <id> --name <licensee> --until <yyyy-MM-dd>, and takes an optional --ledger <absolute path>.");
        }

        if (!LicenceKey.IsValidKeyId(kid))
        {
            return Fail(1, "The key id must be 1-16 characters of a-z, 0-9 and -.");
        }

        if (!LicenceKey.IsValidName(name))
        {
            return Fail(1, $"The licensee name must be visible text of at most {LicenceKey.MaxNameLength} characters.");
        }

        if (!LicenceKey.TryParseDate(untilText, out DateOnly until))
        {
            return Fail(1, "The until date must be a real date as yyyy-MM-dd.");
        }

        string ledger = options.TryGetValue("--ledger", out string? given) ? given : defaultLedger;
        if (LedgerProblem(ledger) is { } ledgerProblem)
        {
            return Fail(1, ledgerProblem);
        }

        if (!keys.IsSupported)
        {
            return Fail(1, UnsupportedSigningKeyStore.NotAvailable);
        }

        byte[]? pkcs8;
        try
        {
            pkcs8 = keys.Read(kid);
        }
        catch (TokenStoreException ex)
        {
            return Fail(1, ex.Message);
        }

        if (pkcs8 is null)
        {
            return Fail(1, $"There is no signing key named {kid}. Run create-key --kid {kid} first.");
        }

        DateOnly issued = DateOnly.FromDateTime(time.GetLocalNow().DateTime);
        string licenceId = NewLicenceId();
        string token;
        try
        {
            token = Sign(pkcs8, kid, licenceId, name, until, issued);
        }
        catch (CryptographicException)
        {
            return Fail(1, $"The signing key named {kid} cannot be used.");
        }
        finally
        {
            Array.Clear(pkcs8);
        }

        try
        {
            AppendLedgerLine(ledger, string.Join('\t', licenceId, name, LicenceKey.FormatDate(until), LicenceKey.FormatDate(issued), kid) + "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(1, "The ledger could not be written, so no key was issued.");
        }

        output.WriteLine(token);
        return 0;
    }

    /// <summary>
    /// Why <paramref name="ledger"/> cannot be used, or <c>null</c>: it must be an absolute path, and no folder
    /// it sits in may hold <c>.git</c> (the ledger names licensees and must never be committed).
    /// </summary>
    internal static string? LedgerProblem(string ledger)
    {
        if (!Path.IsPathFullyQualified(ledger))
        {
            return "The ledger path must be absolute.";
        }

        string? folder = Path.GetDirectoryName(Path.GetFullPath(ledger));
        while (folder is not null)
        {
            string git = Path.Combine(folder, ".git");
            if (Directory.Exists(git) || File.Exists(git))
            {
                return "The ledger must not be inside a git repository.";
            }

            folder = Path.GetDirectoryName(folder);
        }

        return null;
    }

    /// <summary>
    /// Appends <paramref name="line"/> to the ledger. It names licensees, so on Unix a folder it creates is
    /// <c>0700</c> and a new ledger <c>0600</c>.
    /// </summary>
    private static void AppendLedgerLine(string ledger, string line)
    {
        string? folder = Path.GetDirectoryName(ledger);
        if (folder is not null)
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(folder);
            }
            else
            {
                Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        FileStreamOptions options = new FileStreamOptions
        {
            Mode = FileMode.Append,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        byte[] bytes = new UTF8Encoding(false).GetBytes(line);
        using FileStream stream = new FileStream(ledger, options);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    // 16 random bytes as 22 base64url characters.
    private string NewLicenceId()
    {
        byte[] bytes = new byte[16];
        random.GetBytes(bytes);
        return LicenceKey.Encode(bytes);
    }

    /// <summary>The <c>CTX1-</c> token, checked against the key's own public half before it is handed out.</summary>
    internal static string Sign(byte[] pkcs8, string kid, string licenceId, string name, DateOnly until, DateOnly issued)
    {
        byte[] payload = Payload(kid, licenceId, name, until, issued);
        using ECDsa key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(pkcs8, out _);
        byte[] signature = key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        string token = LicenceKey.Prefix + LicenceKey.Encode(payload) + "." + LicenceKey.Encode(signature);

        LicencePublicKeys own = LicencePublicKeys.FromTable([(kid, key.ExportSubjectPublicKeyInfoPem())]);
        if (!LicenceKey.TryVerify(token, own, LicenceRevocations.None).IsValid)
        {
            throw new CryptographicException("The issued token does not verify.");
        }

        return token;
    }

    /// <summary>The payload JSON in the fixed property order: v, kid, lid, name, ed, until, issued.</summary>
    internal static byte[] Payload(string kid, string licenceId, string name, DateOnly until, DateOnly issued)
    {
        using MemoryStream buffer = new MemoryStream();
        using (Utf8JsonWriter writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", LicenceKey.Version);
            writer.WriteString("kid", kid);
            writer.WriteString("lid", licenceId);
            writer.WriteString("name", name);
            writer.WriteString("ed", LicenceKey.Edition);
            writer.WriteString("until", LicenceKey.FormatDate(until));
            writer.WriteString("issued", LicenceKey.FormatDate(issued));
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    // --name value pairs, each name once.
    private static bool TryOptions(string[] args, out Dictionary<string, string> options, out string? problem)
    {
        options = new Dictionary<string, string>(StringComparer.Ordinal);
        problem = null;
        for (int i = 0; i < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
            {
                problem = Usage;
                return false;
            }

            if (!options.TryAdd(args[i], args[i + 1]))
            {
                problem = $"{args[i]} is given twice.";
                return false;
            }
        }

        return true;
    }

    private static bool Only(Dictionary<string, string> options, params string[] allowed) =>
        options.Keys.All(allowed.Contains);

    private int Fail(int code, string message)
    {
        error.WriteLine(message);
        return code;
    }
}
