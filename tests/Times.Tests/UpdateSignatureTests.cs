using System.Security.Cryptography;

namespace Times.Tests;

public sealed class UpdateSignatureTests
{
    private static readonly byte[] Data = [1, 2, 3, 4, 5];

    // AC-5: a DER ECDSA P-256/SHA-256 signature (what openssl dgst -sha256 -sign makes) verifies.
    [Fact]
    public void Verify_AcceptsTheKeysSignature_AndRefusesAFlippedByte()
    {
        using TestSigner signer = new TestSigner();
        UpdateSignature signature = signer.Signature;
        byte[] sig = signer.Sign(Data);

        Assert.True(signature.HasKeys);
        Assert.True(signature.Verify(Data, sig));

        byte[] flippedData = Data.ToArray();
        flippedData[2] ^= 1;
        Assert.False(signature.Verify(flippedData, sig));

        byte[] flippedSig = sig.ToArray();
        flippedSig[^1] ^= 1;
        Assert.False(signature.Verify(Data, flippedSig));
    }

    // AC-5: an IEEE P1363 signature (the other format) is not accepted.
    [Fact]
    public void Verify_RefusesOtherSignatureFormats_AndGarbage()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        UpdateSignature signature = UpdateSignature.FromPem(key.ExportSubjectPublicKeyInfoPem());

        Assert.False(signature.Verify(Data, key.SignData(Data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
        Assert.False(signature.Verify(Data, []));
        Assert.False(signature.Verify(Data, [0x30, 0x02, 0x01]));
    }

    // AC-5: every key is tried, so a second key (rotation) verifies too.
    [Fact]
    public void Verify_TriesEveryKey()
    {
        using TestSigner first = new TestSigner();
        using TestSigner second = new TestSigner();
        using TestSigner other = new TestSigner();
        UpdateSignature both = UpdateSignature.FromPem(first.PublicPem + "\n" + second.PublicPem);

        Assert.Equal(2, both.KeyCount);
        Assert.True(both.Verify(Data, first.Sign(Data)));
        Assert.True(both.Verify(Data, second.Sign(Data)));
        Assert.False(both.Verify(Data, other.Sign(Data)));
    }

    // AC-13: no key and an invalid PEM give no keys, and then nothing verifies.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a pem")]
    [InlineData("-----BEGIN PUBLIC KEY-----\nAAAA\n-----END PUBLIC KEY-----\n")]
    [InlineData("-----BEGIN CERTIFICATE-----\nAAAA\n-----END CERTIFICATE-----\n")]
    public void FromPem_NoOrInvalidKey_HasNoKeys(string? pem)
    {
        using TestSigner signer = new TestSigner();
        UpdateSignature signature = UpdateSignature.FromPem(pem);

        Assert.False(signature.HasKeys);
        Assert.False(signature.Verify(Data, signer.Sign(Data)));
    }

    // AC-13, security: a private key in the PEM (a leaked key) fails closed, even next to a public one.
    [Fact]
    public void FromPem_WithAPrivateKey_HasNoKeys()
    {
        using TestSigner signer = new TestSigner();

        Assert.False(UpdateSignature.FromPem(signer.PrivatePem).HasKeys);
        Assert.False(UpdateSignature.FromPem(signer.PublicPem + signer.PrivatePem).HasKeys);
        Assert.False(UpdateSignature.FromPem(signer.PrivatePem).Verify(Data, signer.Sign(Data)));
    }

    // AC-5: only P-256 keys count.
    [Fact]
    public void FromPem_OtherCurvesAndRsa_AreIgnored()
    {
        using ECDsa p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        using RSA rsa = RSA.Create(2048);

        Assert.False(UpdateSignature.FromPem(p384.ExportSubjectPublicKeyInfoPem()).HasKeys);
        Assert.False(UpdateSignature.FromPem(rsa.ExportSubjectPublicKeyInfoPem()).HasKeys);
    }

    // AC-13: the None verifier has no keys.
    [Fact]
    public void None_HasNoKeys()
    {
        Assert.False(UpdateSignature.None.HasKeys);
        Assert.Equal(0, UpdateSignature.None.KeyCount);
    }

    // AC-13: the embedded key is read without failing, whether or not update-key.pem exists (its content is never asserted).
    [Fact]
    public void Embedded_NeverThrows()
    {
        UpdateSignature embedded = UpdateSignature.Embedded();

        Assert.True(embedded.KeyCount >= 0);
    }
}
