using System.Security.Cryptography;
using System.Text;

namespace Times.Tests;

/// <summary>
/// A P-256 key pair made for one test, its key table (key id <see cref="KeyId"/>) and helpers that sign
/// licence tokens with it, including payloads the issuer would never write. Never the real key.
/// </summary>
internal sealed class TestLicences : IDisposable
{
    public const string KeyId = "test";
    public const string LicenceId = "AAECAwQFBgcICQoLDA0ODw";
    public const string Name = "Ada Lovelace";

    private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public TestLicences()
    {
        Pem = key.ExportSubjectPublicKeyInfoPem();
        Keys = LicencePublicKeys.FromTable([(KeyId, Pem)]);
    }

    public string Pem { get; }

    /// <summary>A table holding only this key, as <see cref="KeyId"/>.</summary>
    public LicencePublicKeys Keys { get; }

    public void Dispose() => key.Dispose();

    /// <summary>The JSON of a well-formed payload.</summary>
    public static string PayloadJson(string kid = KeyId, string lid = LicenceId, string name = Name,
        string until = "2027-12-31", string issued = "2026-10-27") =>
        $"{{\"v\":1,\"kid\":\"{kid}\",\"lid\":\"{lid}\",\"name\":\"{name}\",\"ed\":\"suite-pro\",\"until\":\"{until}\",\"issued\":\"{issued}\"}}";

    /// <summary>A valid token for the given fields.</summary>
    public string Token(string kid = KeyId, string lid = LicenceId, string name = Name, string until = "2027-12-31",
        string issued = "2026-10-27") =>
        Sign(PayloadJson(kid, lid, name, until, issued));

    /// <summary>Signs any payload text exactly as given.</summary>
    public string Sign(string payloadJson) => Sign(Encoding.UTF8.GetBytes(payloadJson));

    public string Sign(byte[] payload)
    {
        byte[] signature = key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return LicenceKey.Prefix + LicenceKey.Encode(payload) + "." + LicenceKey.Encode(signature);
    }

    /// <summary>The build date the unlocked and expired helpers check against.</summary>
    public static readonly DateOnly BuildDate = new(2026, 10, 27);

    /// <summary>
    /// A <see cref="Pro"/> that is unlocked: a fresh test key (until 2027-12-31) in a fake store, on a table
    /// holding only that key, checked against <see cref="BuildDate"/>.
    /// </summary>
    public static Pro Unlocked() => WithStored(until: "2027-12-31");

    /// <summary>A <see cref="Pro"/> whose stored, verifying key ended before <see cref="BuildDate"/>: locked.</summary>
    public static Pro Expired() => WithStored(until: "2026-10-26");

    /// <summary>A <see cref="Pro"/> with nothing stored: locked.</summary>
    public static Pro Locked(FakeLicenceStore? store = null)
    {
        using TestLicences licences = new TestLicences();
        return licences.Pro(store ?? new FakeLicenceStore(), BuildDate);
    }

    // Disposing the TestLicences only frees its private signing key: the Pro keeps the public key table, which is
    // plain data built from the PEM, so it still verifies the token after the using block ends.
    private static Pro WithStored(string until)
    {
        using TestLicences licences = new TestLicences();
        return licences.Pro(new FakeLicenceStore(stored: licences.Token(until: until)), BuildDate);
    }

    /// <summary>A <see cref="Pro"/> on this key table, with the given revocations and build date.</summary>
    public Pro Pro(ILicenceStore store, DateOnly? buildDate, params string[] revoked) =>
        new(store, Keys, new LicenceRevocations(revoked), buildDate);
}

/// <summary>An in-memory licence store: supported or not, with an optional refusal, and failing on demand.</summary>
internal sealed class FakeLicenceStore : ILicenceStore
{
    public const string Failure = "The test licence store failed.";

    public FakeLicenceStore(bool supported = true, string? stored = null)
    {
        IsSupported = supported;
        Stored = stored;
    }

    public bool IsSupported { get; set; }

    public string? WriteRefusal { get; set; }

    public string? Stored { get; set; }

    /// <summary>Makes every call throw a <see cref="TokenStoreException"/>.</summary>
    public bool Throws { get; set; }

    /// <summary>Makes only <see cref="Delete"/> throw.</summary>
    public bool DeleteThrows { get; set; }

    public int Reads { get; private set; }

    public List<string> Writes { get; } = [];

    public int Deletes { get; private set; }

    public int Calls => Reads + Writes.Count + Deletes;

    public string? Read()
    {
        Reads++;
        ThrowIfSet();
        return Stored;
    }

    public void Write(string key)
    {
        Writes.Add(key);
        ThrowIfSet();
        Stored = key;
    }

    public bool Delete()
    {
        Deletes++;
        ThrowIfSet();
        if (DeleteThrows)
        {
            throw new TokenStoreException(Failure);
        }

        bool had = Stored is not null;
        Stored = null;
        return had;
    }

    private void ThrowIfSet()
    {
        if (Throws)
        {
            throw new TokenStoreException(Failure);
        }
    }
}
