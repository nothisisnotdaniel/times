using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Times.Tray;

/// <summary>
/// The time since the last keyboard or mouse input on macOS, from CoreGraphics'
/// <c>CGEventSourceSecondsSinceLastEventType</c> (the framework loaded by its absolute path).
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed partial class MacIdleSource : IIdleSource
{
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    // kCGEventSourceStateCombinedSessionState and kCGAnyInputEventType.
    private const int CombinedSessionState = 0;
    private const uint AnyInputEventType = uint.MaxValue;

    public TimeSpan? SinceLastInput()
    {
        try
        {
            double seconds = SecondsSinceLastEventType(CombinedSessionState, AnyInputEventType);
            return double.IsFinite(seconds) && seconds >= 0 && seconds < TimeSpan.MaxValue.TotalSeconds
                ? TimeSpan.FromSeconds(seconds)
                : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [LibraryImport(CoreGraphics, EntryPoint = "CGEventSourceSecondsSinceLastEventType")]
    private static partial double SecondsSinceLastEventType(int stateId, uint eventType);
}
