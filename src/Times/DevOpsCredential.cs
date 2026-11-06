using System.Text.RegularExpressions;

namespace Times;

/// <summary>
/// An Azure DevOps organisation and the personal access token (PAT) for it, both validated. The
/// organisation goes into the request URL, so only a plain organisation name is accepted. The PAT
/// is never part of <see cref="ToString"/>, so it cannot leak through logging or string formatting.
/// </summary>
internal sealed partial class DevOpsCredential
{
    /// <summary>The longest PAT accepted, in characters.</summary>
    public const int MaxPatLength = 200;

    private DevOpsCredential(string organization, string pat)
    {
        Organization = organization;
        Pat = pat;
    }

    /// <summary>The organisation, as in <c>https://dev.azure.com/&lt;organization&gt;</c>.</summary>
    public string Organization { get; }

    /// <summary>The token. Only <see cref="AzureDevOpsClient"/> reads it, to build the auth header.</summary>
    internal string Pat { get; }

    /// <summary>
    /// True if <paramref name="organization"/> is 1 to 50 ASCII letters, digits and hyphens that
    /// starts and ends with a letter or digit.
    /// </summary>
    public static bool IsValidOrganization(string? organization) =>
        organization is not null && OrganizationPattern().IsMatch(organization);

    /// <summary>True if <paramref name="pat"/> is 1 to <see cref="MaxPatLength"/> printable ASCII characters with no whitespace.</summary>
    public static bool IsValidPat(string? pat)
    {
        if (pat is null || pat.Length == 0 || pat.Length > MaxPatLength)
        {
            return false;
        }

        foreach (char c in pat)
        {
            if (c < '!' || c > '~')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The credential, or <c>null</c> if either part fails validation.</summary>
    public static DevOpsCredential? TryCreate(string? organization, string? pat) =>
        IsValidOrganization(organization) && IsValidPat(pat) ? new DevOpsCredential(organization!, pat!) : null;

    /// <summary>The organisation only; never the token.</summary>
    public override string ToString() => $"Azure DevOps organization {Organization}";

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,48}[A-Za-z0-9])?\z", RegexOptions.CultureInvariant)]
    private static partial Regex OrganizationPattern();
}
