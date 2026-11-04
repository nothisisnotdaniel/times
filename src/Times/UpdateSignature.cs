using System.Security.Cryptography;

namespace Times;

/// <summary>
/// Checks the detached <c>.sig</c> of a downloaded package: an ECDSA P-256 signature over SHA-256 in
/// DER form (<c>openssl dgst -sha256 -sign</c>), against every embedded public key. This is the only
/// trust anchor of an update. With no usable key nothing verifies, so updates fail closed.
/// </summary>
internal sealed class UpdateSignature
{
    /// <summary>The manifest name of the embedded <c>src/Times/update-key.pem</c>.</summary>
    public const string ResourceName = "Times.update-key.pem";

    /// <summary>The OID of the NIST P-256 curve.</summary>
    private const string P256Oid = "1.2.840.10045.3.1.7";

    private const int MaxPemChars = 16 * 1024;

    private readonly List<ECParameters> keys;

    private UpdateSignature(List<ECParameters> keys)
    {
        this.keys = keys;
    }

    /// <summary>True if at least one P-256 public key was loaded.</summary>
    public bool HasKeys => keys.Count > 0;

    public int KeyCount => keys.Count;

    /// <summary>A verifier with no keys: nothing verifies.</summary>
    public static UpdateSignature None { get; } = new([]);

    /// <summary>The key compiled into this build, or <see cref="None"/> when the build has none or it cannot be parsed.</summary>
    public static UpdateSignature Embedded()
    {
        using Stream? stream = typeof(UpdateSignature).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            return None;
        }

        byte[] bytes = CappedFile.ReadAtMost(stream, MaxPemChars + 1);
        if (bytes.Length > MaxPemChars)
        {
            return None;
        }

        try
        {
            return FromPem(new System.Text.UTF8Encoding(false, true).GetString(bytes));
        }
        catch (ArgumentException)
        {
            return None;
        }
    }

    /// <summary>
    /// Every <c>PUBLIC KEY</c> block of <paramref name="pem"/> that is a P-256 key. Blocks with other
    /// labels and keys on other curves are ignored. A text that holds a <c>PRIVATE KEY</c> block gives
    /// no keys at all: a committed private key is a leaked key, so updates fail closed.
    /// </summary>
    public static UpdateSignature FromPem(string? pem)
    {
        if (string.IsNullOrEmpty(pem) || pem.Length > MaxPemChars || pem.Contains("PRIVATE KEY", StringComparison.Ordinal))
        {
            return None;
        }

        List<ECParameters> found = [];
        ReadOnlySpan<char> rest = pem;
        while (PemEncoding.TryFind(rest, out PemFields fields))
        {
            ReadOnlySpan<char> label = rest[fields.Label];
            if (label.SequenceEqual("PUBLIC KEY") && TryImport(rest[fields.Location], out ECParameters key))
            {
                found.Add(key);
            }

            rest = rest[fields.Location.End..];
        }

        return found.Count == 0 ? None : new UpdateSignature(found);
    }

    /// <summary>True if <paramref name="signature"/> verifies <paramref name="data"/> with any key.</summary>
    public bool Verify(byte[] data, byte[] signature)
    {
        foreach (ECParameters parameters in keys)
        {
            using ECDsa key = ECDsa.Create(parameters);
            try
            {
                if (key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
                {
                    return true;
                }
            }
            catch (CryptographicException)
            {
                // A malformed signature verifies with no key.
            }
        }

        return false;
    }

    private static bool TryImport(ReadOnlySpan<char> block, out ECParameters parameters)
    {
        parameters = default;
        try
        {
            using ECDsa key = ECDsa.Create();
            key.ImportFromPem(block);
            ECParameters exported = key.ExportParameters(includePrivateParameters: false);
            if (exported.Curve.Oid?.Value != P256Oid && exported.Curve.Oid?.FriendlyName is not ("nistP256" or "ECDSA_P256" or "prime256v1"))
            {
                return false;
            }

            parameters = exported;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            return false;
        }
    }
}
