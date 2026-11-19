using System.Globalization;
using System.Reflection;

namespace Times;

/// <summary>
/// The app's version, taken from the assembly's <see cref="AssemblyInformationalVersionAttribute"/>
/// (set by <c>&lt;Version&gt;</c> in the project file), without the SDK's <c>+&lt;commit&gt;</c> build metadata.
/// </summary>
internal static class AppVersion
{
    private const string Unknown = "unknown";

    /// <summary>The version of the Times assembly (not the entry assembly, which is the test host under <c>dotnet test</c>).</summary>
    internal static string Current =>
        Format(typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>Strips everything from the first <c>+</c> and trims; null, empty or whitespace gives <c>unknown</c>.</summary>
    internal static string Format(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return Unknown;
        }

        int plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        string version = (plus >= 0 ? informationalVersion[..plus] : informationalVersion).Trim();
        return version.Length == 0 ? Unknown : version;
    }

    /// <summary>The <c>AssemblyMetadata</c> key <c>Times.csproj</c> writes the UTC build date under.</summary>
    internal const string BuildDateKey = "BuildDate";

    /// <summary>The day this build was made (UTC), or <c>null</c> when the metadata is missing or not a <c>yyyy-MM-dd</c> date.</summary>
    internal static DateOnly? BuildDate =>
        ParseBuildDate(typeof(App).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == BuildDateKey)?.Value);

    /// <summary>Exactly <c>yyyy-MM-dd</c>, or <c>null</c>.</summary>
    internal static DateOnly? ParseBuildDate(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date)
            ? date
            : null;
}
