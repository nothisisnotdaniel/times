using System.Security.Cryptography;

namespace Times.LicenceIssuer;

/// <summary>Wires the real Keychain, random source, clock and default ledger; the logic is in <see cref="Issuer"/>.</summary>
internal static class IssuerProgram
{
    public static int Main(string[] args)
    {
        using RandomNumberGenerator random = RandomNumberGenerator.Create();
        Issuer issuer = new Issuer(Console.Out, Console.Error, SigningKeyStore.ForCurrentPlatform(), random,
            TimeProvider.System, Issuer.DefaultLedger(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)));
        return issuer.Run(args);
    }
}
