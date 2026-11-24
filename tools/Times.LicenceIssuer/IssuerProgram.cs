using System.Diagnostics;
using System.Security.Cryptography;

namespace Times.LicenceIssuer;

/// <summary>Wires the real Keychain, random source, clock and default ledger; the logic is in <see cref="Issuer"/>.</summary>
internal static class IssuerProgram
{
    public static int Main(string[] args)
    {
        if (OperatingSystem.IsMacOS() && args is [string operation, string path, MacKeychainSigningKeyStore.Service, string kid])
        {
            if (operation == MacKeychainItem.NativeCalls.ProbeArgument)
            {
                return MacKeychainItem.NativeCalls.Probe(path, MacKeychainSigningKeyStore.Service, kid);
            }
            if (operation == MacKeychainItem.NativeCalls.VerifyArgument && Console.IsInputRedirected)
            {
                byte[] expected = new byte[SHA256.HashSizeInBytes];
                try
                {
                    using Stream input = Console.OpenStandardInput();
                    input.ReadExactly(expected);
                    return MacKeychainItem.NativeCalls.Probe(path, MacKeychainSigningKeyStore.Service, kid, expected);
                }
                catch (IOException)
                {
                    return 1;
                }
            }
        }

        using RandomNumberGenerator random = RandomNumberGenerator.Create();
        Issuer issuer = new Issuer(Console.Out, Console.Error, SigningKeyStore.ForCurrentPlatform(), random,
            TimeProvider.System, Issuer.DefaultLedger(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)));
        return issuer.Run(args);
    }

    internal static ProcessStartInfo VerifierStartInfo()
    {
        // A shared dotnet host can verify metadata using the same installed assembly.
        // The parent alone makes the authorized password read.
        string assembly = typeof(IssuerProgram).Assembly.Location;
        string executable = Environment.ProcessPath ?? throw new TokenStoreException(MacKeychainItem.MigrationFailed);
        if (Path.GetFileName(executable) == "dotnet")
        {
            ProcessStartInfo shared = new(executable);
            shared.ArgumentList.Add(assembly);
            return shared;
        }
        return new ProcessStartInfo(System.Reflection.Assembly.GetEntryAssembly() == typeof(IssuerProgram).Assembly
            ? executable : Path.Combine(Path.GetDirectoryName(assembly)!, "Times.LicenceIssuer"));
    }
}
