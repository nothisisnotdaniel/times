using System.Security.Cryptography;
using System.Text;

namespace Times.Tests;

public sealed class LicenceKeyTests : IDisposable
{
    private readonly TestLicences licences = new();

    public void Dispose() => licences.Dispose();

    private LicenceCheck Verify(string? token, params string[] revoked) =>
        LicenceKey.TryVerify(token, licences.Keys, new LicenceRevocations(revoked));

    // AC-1
    [Fact]
    public void ValidToken_RoundTrips_WithEveryField()
    {
        LicenceCheck check = Verify(licences.Token(name: "Zoë Ünal ❤️", until: "2027-06-30", issued: "2026-10-01"));

        Assert.True(check.IsValid, check.Problem);
        Assert.Null(check.Problem);
        Licence licence = check.Licence!;
        Assert.Equal(TestLicences.KeyId, licence.KeyId);
        Assert.Equal(TestLicences.LicenceId, licence.LicenceId);
        Assert.Equal("Zoë Ünal ❤️", licence.Name);
        Assert.Equal(new DateOnly(2027, 6, 30), licence.Until);
        Assert.Equal(new DateOnly(2026, 10, 1), licence.Issued);
    }

    // AC-1: one changed byte anywhere in the payload or signature fails, as a fixed reason.
    [Fact]
    public void ChangedPayloadOrSignatureByte_Fails()
    {
        string token = licences.Token();
        Assert.True(LicenceKey.TrySplit(token, out byte[] payload, out byte[] signature));

        for (int i = 0; i < payload.Length; i++)
        {
            byte[] changed = (byte[])payload.Clone();
            changed[i] ^= 0x01;
            string tampered = LicenceKey.Prefix + LicenceKey.Encode(changed) + "." + LicenceKey.Encode(signature);
            Assert.Equal(LicenceKey.BadSignature, Verify(tampered).Problem);
        }

        for (int i = 0; i < signature.Length; i++)
        {
            byte[] changed = (byte[])signature.Clone();
            changed[i] ^= 0x80;
            string tampered = LicenceKey.Prefix + LicenceKey.Encode(payload) + "." + LicenceKey.Encode(changed);
            Assert.Equal(LicenceKey.BadSignature, Verify(tampered).Problem);
        }
    }

    // AC-1
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("CTX1-")]
    [InlineData("CTX1-.")]
    [InlineData("CTX1-abc")]
    [InlineData("CTX1-abc.def.ghi")]
    [InlineData("CTX1-ab!c.def")]
    [InlineData("CTX1-abc .def")]
    [InlineData("CTX1-a.def")]
    public void Malformed_Fails(string? token)
    {
        LicenceCheck check = Verify(token);

        Assert.False(check.IsValid);
        Assert.Equal(LicenceKey.Malformed, check.Problem);
    }

    // AC-1
    [Theory]
    [InlineData("ctx1-")]
    [InlineData("CTX2-")]
    [InlineData("CTX-")]
    [InlineData(" CTX1-")]
    public void WrongPrefix_Fails(string prefix)
    {
        string token = prefix + licences.Token()[LicenceKey.Prefix.Length..];

        Assert.Equal(LicenceKey.Malformed, Verify(token).Problem);
    }

    // AC-1
    [Fact]
    public void Padding_Fails()
    {
        string token = licences.Token();
        Assert.True(LicenceKey.TrySplit(token, out byte[] payload, out byte[] signature));
        string paddedPayload = Convert.ToBase64String(payload).Replace('+', '-').Replace('/', '_');
        string paddedSignature = Convert.ToBase64String(signature).Replace('+', '-').Replace('/', '_');
        Assert.EndsWith("=", paddedSignature, StringComparison.Ordinal);

        Assert.Equal(LicenceKey.Malformed, Verify(LicenceKey.Prefix + LicenceKey.Encode(payload) + "." + paddedSignature).Problem);
        if (paddedPayload.EndsWith('='))
        {
            Assert.Equal(LicenceKey.Malformed, Verify(LicenceKey.Prefix + paddedPayload + "." + LicenceKey.Encode(signature)).Problem);
        }

        Assert.Equal(LicenceKey.Malformed, Verify(token + "=").Problem);
    }

    // AC-1: a signature in DER form (not the 64-byte IEEE P1363 form) is refused.
    [Fact]
    public void DerSignature_Fails()
    {
        byte[] payload = Encoding.UTF8.GetBytes(TestLicences.PayloadJson());
        using ECDsa other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] der = other.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        Assert.Equal(LicenceKey.Malformed, Verify(LicenceKey.Prefix + LicenceKey.Encode(payload) + "." + LicenceKey.Encode(der)).Problem);
    }

    // AC-1
    [Fact]
    public void TokenOver1024Chars_Fails_EvenWhenSigned()
    {
        string padded = TestLicences.PayloadJson() + new string(' ', 800);
        string token = licences.Sign(padded);
        Assert.True(token.Length > LicenceKey.MaxLength);

        Assert.Equal(LicenceKey.Malformed, Verify(token).Problem);
    }

    // AC-1: at exactly 1024 characters a token is still read (whitespace in the JSON is allowed).
    [Fact]
    public void TokenOfExactly1024Chars_Verifies()
    {
        string token = string.Empty;
        for (int spaces = 0; token.Length != LicenceKey.MaxLength && spaces < 1000; spaces++)
        {
            token = licences.Sign(TestLicences.PayloadJson() + new string(' ', spaces));
        }

        Assert.Equal(LicenceKey.MaxLength, token.Length);
        Assert.True(Verify(token).IsValid);
    }

    // AC-1: each content rule fails as the fixed contents reason, after the signature matched.
    [Theory]
    [InlineData("{\"v\":1,\"v\":1,\"kid\":\"test\",\"lid\":\"AAECAwQFBgcICQoLDA0ODw\",\"name\":\"Ada\",\"ed\":\"suite-pro\",\"until\":\"2027-12-31\",\"issued\":\"2026-10-27\"}")]
    [InlineData("{\"v\":1,\"kid\":\"test\",\"lid\":\"AAECAwQFBgcICQoLDA0ODw\",\"name\":\"Ada\",\"name\":\"Eve\",\"ed\":\"suite-pro\",\"until\":\"2027-12-31\",\"issued\":\"2026-10-27\"}")]
    [InlineData("{\"v\":1,\"kid\":\"test\",\"lid\":\"AAECAwQFBgcICQoLDA0ODw\",\"name\":\"Ada\",\"ed\":\"suite-pro\",\"until\":\"2027-12-31\",\"issued\":\"2026-10-27\",\"extra\":\"x\"}")]
    [InlineData("{\"v\":1,\"kid\":\"test\",\"lid\":\"AAECAwQFBgcICQoLDA0ODw\",\"name\":\"Ada\",\"ed\":\"suite-pro\",\"until\":\"2027-12-31\",\"issued\":\"2026-10-27\",\"x\":{}}")]
    [InlineData("{\"v\":2,\"kid\":\"test\",\"lid\":\"AAECAwQFBgcICQoLDA0ODw\",\"name\":\"Ada\",\"ed\":\"suite-pro\",\"until\":\"2027-12-31\",\"issued\":\"2026-10-27\"}")]
    [InlineData("{\"v\":\"1\",\"kid\":\"test\",\"lid\":\"AAECAwQFBgcICQoLDA0ODw\",\"name\":\"Ada\",\"ed\":\"suite-pro\",\"until\":\"2027-12-31\",\"issued\":\"2026-10-27\"}")]
    [InlineData("{\"v\":1,\"kid\":\"test\",\"lid\":\"AAECAwQFBgcICQoLDA0ODw\",\"name\":\"Ada\",\"ed\":\"suite\",\"until\":\"2027-12-31\",\"issued\":\"2026-10-27\"}")]
    [InlineData("{\"v\":1,\"kid\":\"test\",\"lid\":\"AAECAwQFBgcICQoLDA0ODw\",\"name\":\"Ada\",\"until\":\"2027-12-31\",\"issued\":\"2026-10-27\"}")]
    [InlineData("{\"v\":1,\"kid\":\"test\",\"lid\":\"AAECAwQFBgcICQoLDA0ODw\",\"name\":\"Ada\",\"ed\":\"suite-pro\",\"until\":\"2027-02-30\",\"issued\":\"2026-10-27\"}")]
    [InlineData("{\"v\":1,\"kid\":\"test\",\"lid\":\"AAECAwQFBgcICQoLDA0ODw\",\"name\":\"Ada\",\"ed\":\"suite-pro\",\"until\":\"2027-12-31\",\"issued\":\"27.10.2026\"}")]
    [InlineData("{\"v\":1,\"kid\":\"test\",\"lid\":\"AAECAwQFBgcICQoLDA0ODw\",\"name\":\"Ada\",\"ed\":\"suite-pro\",\"until\":\"2027-12-31T00:00\",\"issued\":\"2026-10-27\"}")]
    [InlineData("{\"v\":1,\"kid\":\"test\",\"lid\":\"AAECAwQFBgcICQoLDA0ODw\",\"name\":\"   \",\"ed\":\"suite-pro\",\"until\":\"2027-12-31\",\"issued\":\"2026-10-27\"}")]
    [InlineData("{\"v\":1,\"kid\":\"test\",\"lid\":\"AAECAwQFBgcICQoLDA0ODw\",\"name\":\"A\\u202Eda\",\"ed\":\"suite-pro\",\"until\":\"2027-12-31\",\"issued\":\"2026-10-27\"}")]
    [InlineData("{\"v\":1,\"kid\":\"test\",\"lid\":\"AAECAwQFBgcICQoLDA0ODw\",\"name\":\"A\\nda\",\"ed\":\"suite-pro\",\"until\":\"2027-12-31\",\"issued\":\"2026-10-27\"}")]
    [InlineData("{\"v\":1,\"kid\":\"test\",\"lid\":\"AAECAwQFBgcICQoLDA0ODw\",\"name\":\"\\ud800\",\"ed\":\"suite-pro\",\"until\":\"2027-12-31\",\"issued\":\"2026-10-27\"}")]
    [InlineData("{\"v\":1,\"kid\":\"test\",\"lid\":\"AAECAwQFBgcICQoLDA0OD\",\"name\":\"Ada\",\"ed\":\"suite-pro\",\"until\":\"2027-12-31\",\"issued\":\"2026-10-27\"}")]
    [InlineData("{\"v\":1,\"kid\":\"test\",\"lid\":\"AAECAwQFBgcICQoLDA0ODx\",\"name\":\"Ada\",\"ed\":\"suite-pro\",\"until\":\"2027-12-31\",\"issued\":\"2026-10-27\"}")]
    [InlineData("{\"v\":1,\"kid\":\"test\",\"lid\":\"AAECAwQFBgcICQoLDA0ODw\",\"name\":\"Ada\",\"ed\":\"suite-pro\",\"until\":\"2027-12-31\",\"issued\":\"2026-10-27\"} x")]
    [InlineData("[1]")]
    [InlineData("not json")]
    public void InvalidContents_Fail(string payload)
    {
        LicenceCheck check = Verify(licences.Sign(payload));

        Assert.False(check.IsValid);
        Assert.Equal(LicenceKey.BadContents, check.Problem);
    }

    // AC-1
    [Fact]
    public void NameOver100Chars_Fails_And100Passes()
    {
        Assert.True(Verify(licences.Token(name: new string('a', 100))).IsValid);
        Assert.Equal(LicenceKey.BadContents, Verify(licences.Token(name: new string('a', 101))).Problem);
    }

    // AC-1: invalid UTF-8 in the signed bytes fails without throwing.
    [Fact]
    public void InvalidUtf8_Fails()
    {
        byte[] payload = Encoding.UTF8.GetBytes(TestLicences.PayloadJson(name: "Ada X"));
        int x = Array.IndexOf(payload, (byte)'X');
        payload[x] = 0xFF;

        Assert.Equal(LicenceKey.BadContents, Verify(licences.Sign(payload)).Problem);
    }

    // AC-2
    [Fact]
    public void UnknownKid_FailsAsUnknownKey()
    {
        Assert.Equal(LicenceKey.UnknownKey, Verify(licences.Token(kid: "other")).Problem);
    }

    // AC-2: a token from a key that is not in the table never verifies.
    [Fact]
    public void TokenFromAnotherKey_DoesNotVerify()
    {
        using TestLicences stranger = new TestLicences();

        LicenceCheck check = Verify(stranger.Token());

        Assert.False(check.IsValid);
        Assert.Equal(LicenceKey.BadSignature, check.Problem);
        Assert.Equal(LicenceKey.UnknownKey,
            LicenceKey.TryVerify(stranger.Token(), LicencePublicKeys.None, LicenceRevocations.None).Problem);
    }

    // AC-2
    [Fact]
    public void RevokedLid_FailsAsRevoked()
    {
        string token = licences.Token();

        Assert.Equal(LicenceKey.Revoked, Verify(token, TestLicences.LicenceId).Problem);
        Assert.True(Verify(token, "BBECAwQFBgcICQoLDA0ODw").IsValid);
    }

    // AC-2
    [Fact]
    public void KeyTableWithAPrivateKey_LoadsNoKeys()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        LicencePublicKeys withPrivate = LicencePublicKeys.FromTable(
            [(TestLicences.KeyId, licences.Pem), ("leak", key.ExportPkcs8PrivateKeyPem())]);
        LicencePublicKeys ecPrivate = LicencePublicKeys.FromTable([("leak", key.ExportECPrivateKeyPem())]);

        Assert.Equal(0, withPrivate.Count);
        Assert.Equal(0, ecPrivate.Count);
        Assert.Equal(LicenceKey.UnknownKey, LicenceKey.TryVerify(licences.Token(), withPrivate, LicenceRevocations.None).Problem);
    }

    // AC-2: entries with a bad key id, a non-P-256 key or junk are left out; the rest still load.
    [Fact]
    public void KeyTable_SkipsUnusableEntries()
    {
        using ECDsa p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);

        LicencePublicKeys keys = LicencePublicKeys.FromTable(
        [
            (TestLicences.KeyId, licences.Pem),
            ("Bad Id", licences.Pem),
            ("p384", p384.ExportSubjectPublicKeyInfoPem()),
            ("junk", "-----BEGIN PUBLIC KEY-----\nAAAA\n-----END PUBLIC KEY-----"),
            ("two", licences.Pem + licences.Pem),
        ]);

        Assert.Equal(1, keys.Count);
        Assert.True(keys.Contains(TestLicences.KeyId));
    }

    // AC-2: the compiled table and revocation list start empty, so this build unlocks nothing yet.
    [Fact]
    public void CompiledTables_StartEmpty()
    {
        Assert.Equal(0, LicencePublicKeys.Embedded().Count);
        Assert.False(LicenceRevocations.Embedded().Contains(TestLicences.LicenceId));
    }

    // AC-1: never throws, whatever the input.
    [Fact]
    public void RandomInput_NeverThrows()
    {
        Random random = new Random(1234);
        for (int i = 0; i < 500; i++)
        {
            byte[] bytes = new byte[random.Next(0, 200)];
            random.NextBytes(bytes);
            LicenceCheck check = Verify(licences.Sign(bytes));
            Assert.False(check.IsValid);
            Assert.NotNull(check.Problem);
        }
    }

    [Theory]
    [InlineData("a", true)]
    [InlineData("manual-2026", true)]
    [InlineData("abcdefghijklmnop", true)]
    [InlineData("abcdefghijklmnopq", false)]
    [InlineData("", false)]
    [InlineData("Upper", false)]
    [InlineData("a_b", false)]
    [InlineData("abc\n", false)]
    public void KeyIdRule(string kid, bool valid)
    {
        Assert.Equal(valid, LicenceKey.IsValidKeyId(kid));
    }
}
