using System.Text.Json;

namespace Times;

/// <summary>
/// What one message from Teams said. Every part is optional: <c>null</c> when the message did not hold it.
/// A plain class (not a record), so the token never shows up in a generated <c>ToString</c>.
/// </summary>
internal sealed class TeamsMessage
{
    public TeamsMessage(bool? inMeeting, bool? canPair, string? token)
    {
        InMeeting = inMeeting;
        CanPair = canPair;
        Token = token;
    }

    /// <summary><c>meetingUpdate.meetingState.isInMeeting</c>, or <c>null</c>.</summary>
    public bool? InMeeting { get; }

    /// <summary><c>meetingUpdate.meetingPermissions.canPair</c>, or <c>null</c>.</summary>
    public bool? CanPair { get; }

    /// <summary>The raw <c>tokenRefresh</c> value, or <c>null</c>. Not checked yet: see <see cref="TeamsProtocol.IsValidToken"/>.</summary>
    public string? Token { get; }

    public override string ToString() => "TeamsMessage";
}

/// <summary>
/// Every wire detail of the new Teams app's local third-party API (protocol 2.0.0) in one place: where to
/// connect, how to read a message and what to send for pairing. Times only listens and asks for pairing; it
/// never sends anything that changes Teams (mute, video, hand, blur, recording).
/// </summary>
internal static class TeamsProtocol
{
    /// <summary>The literal loopback address: never a host name, so no resolver or proxy is involved.</summary>
    public const string Host = "127.0.0.1";

    public const int Port = 8124;

    public const string ProtocolVersion = "2.0.0";

    /// <summary>What Times calls itself for <c>manufacturer</c>, <c>device</c> and <c>app</c>.</summary>
    public const string AppName = "Times";

    /// <summary>The largest message Times reads; a longer one closes the connection.</summary>
    public const int MaxMessageBytes = 64 * 1024;

    /// <summary>The longest pairing token Times keeps.</summary>
    public const int MaxTokenLength = 512;

    /// <summary>The one message Times sends: asks Teams to pair (Teams then asks the user).</summary>
    public const string PairRequest = "{\"action\":\"pair\",\"parameters\":{},\"requestId\":1}";

    /// <summary>
    /// <c>ws://127.0.0.1:8124/?token=…&amp;protocol-version=2.0.0&amp;manufacturer=Times&amp;device=Times&amp;app=Times&amp;app-version=…</c>,
    /// with the token (empty while not paired) and the version escaped.
    /// </summary>
    public static Uri BuildUri(string? token, string appVersion)
    {
        string query =
            $"token={Uri.EscapeDataString(token ?? string.Empty)}" +
            $"&protocol-version={Uri.EscapeDataString(ProtocolVersion)}" +
            $"&manufacturer={AppName}&device={AppName}&app={AppName}" +
            $"&app-version={Uri.EscapeDataString(appVersion)}";
        return new Uri($"ws://{Host}:{Port}/?{query}");
    }

    /// <summary>True only for <c>ws://127.0.0.1:8124/…</c>: the one place Times ever connects to.</summary>
    public static bool IsAllowed(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == "ws" && uri.HostNameType == UriHostNameType.IPv4 && uri.Host == Host
        && uri.Port == Port && uri.AbsolutePath == "/";

    /// <summary>1 to <see cref="MaxTokenLength"/> printable ASCII characters (<c>!</c> to <c>~</c>).</summary>
    public static bool IsValidToken(string? token)
    {
        if (token is null || token.Length < 1 || token.Length > MaxTokenLength)
        {
            return false;
        }

        foreach (char c in token)
        {
            if (c < '!' || c > '~')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Reads one message. Returns <c>null</c> for anything that is not one JSON object, holds a duplicate property
    /// (at any depth) or has a known property of the wrong type; unknown properties are ignored. Never throws.
    /// </summary>
    public static TeamsMessage? Parse(string text)
    {
        try
        {
            JsonDocumentOptions options = new JsonDocumentOptions { AllowDuplicateProperties = false, MaxDepth = 16 };
            using JsonDocument document = JsonDocument.Parse(text, options);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            bool? inMeeting = null;
            bool? canPair = null;
            string? token = null;
            if (root.TryGetProperty("tokenRefresh", out JsonElement refresh))
            {
                if (refresh.ValueKind != JsonValueKind.String)
                {
                    return null;
                }

                token = refresh.GetString();
            }

            if (root.TryGetProperty("meetingUpdate", out JsonElement update))
            {
                if (update.ValueKind != JsonValueKind.Object
                    || !TryReadBool(update, "meetingState", "isInMeeting", out inMeeting)
                    || !TryReadBool(update, "meetingPermissions", "canPair", out canPair))
                {
                    return null;
                }
            }

            return new TeamsMessage(inMeeting, canPair, token);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    // Reads parent.group.name as a bool: true with null when the group or the name is missing, false when either
    // has the wrong type.
    private static bool TryReadBool(JsonElement parent, string group, string name, out bool? value)
    {
        value = null;
        if (!parent.TryGetProperty(group, out JsonElement inner))
        {
            return true;
        }

        if (inner.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!inner.TryGetProperty(name, out JsonElement flag))
        {
            return true;
        }

        if (flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = flag.GetBoolean();
        return true;
    }
}
