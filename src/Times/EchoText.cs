namespace Times;

/// <summary>
/// The one way to put user-typed or file text into markup in a hint: the text without control or
/// invisible characters (<see cref="DataFolder.ForDisplay"/>), then escaped for markup. So a later fix
/// to escaping or cleaning reaches every hint at once.
/// </summary>
internal static class EchoText
{
    /// <summary>
    /// <paramref name="typed"/> without control characters, line or paragraph separators and Format (Cf)
    /// code points such as bidi overrides and zero-width characters, with markup characters escaped.
    /// </summary>
    internal static string Markup(string typed) => Spectre.Console.Markup.Escape(DataFolder.ForDisplay(typed));
}
