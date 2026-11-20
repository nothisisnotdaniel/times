using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Times.Tray;

/// <summary>
/// The few Objective-C runtime calls the macOS shell needs, with no package: classes, selectors, message sends with
/// the exact argument types of each AppKit method used, and autorelease pools.
/// </summary>
[SupportedOSPlatform("macos")]
internal static partial class ObjC
{
    private const string Runtime = "/usr/lib/libobjc.A.dylib";

    [StructLayout(LayoutKind.Sequential)]
    public struct Size
    {
        public double Width;
        public double Height;
    }

    public static IntPtr Class(string name) => GetClass(name);

    public static IntPtr Selector(string name) => RegisterName(name);

    /// <summary>Sends a message by name; the overloads below match the method's C signature exactly.</summary>
    public static IntPtr Send(IntPtr receiver, string selector) => Send(receiver, Selector(selector));

    public static IntPtr Send(IntPtr receiver, string selector, IntPtr argument) =>
        Send(receiver, Selector(selector), argument);

    public static IntPtr Send(IntPtr receiver, string selector, IntPtr first, IntPtr second) =>
        Send(receiver, Selector(selector), first, second);

    public static IntPtr Send(IntPtr receiver, string selector, IntPtr first, IntPtr second, IntPtr third) =>
        Send(receiver, Selector(selector), first, second, third);

    public static IntPtr SendLong(IntPtr receiver, string selector, long argument) =>
        SendLong(receiver, Selector(selector), argument);

    public static IntPtr SendDouble(IntPtr receiver, string selector, double argument) =>
        SendDouble(receiver, Selector(selector), argument);

    public static IntPtr SendBool(IntPtr receiver, string selector, bool argument) =>
        SendByte(receiver, Selector(selector), argument ? (byte)1 : (byte)0);

    public static IntPtr SendSize(IntPtr receiver, string selector, Size argument) =>
        SendSize(receiver, Selector(selector), argument);

    public static IntPtr SendObjectLong(IntPtr receiver, string selector, IntPtr first, long second) =>
        SendObjectLong(receiver, Selector(selector), first, second);

    public static long SendForLong(IntPtr receiver, string selector) => SendForLong(receiver, Selector(selector));

    /// <summary>A new autoreleased <c>NSString</c> holding the text.</summary>
    public static IntPtr String(string text)
    {
        IntPtr utf8 = Marshal.StringToCoTaskMemUTF8(text);
        try
        {
            return Send(Class("NSString"), "stringWithUTF8String:", utf8);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    /// <summary>A new autoreleased <c>NSData</c> holding a copy of the bytes.</summary>
    public static unsafe IntPtr Data(byte[] bytes)
    {
        fixed (byte* start = bytes)
        {
            return SendObjectLong(Class("NSData"), "dataWithBytes:length:", (IntPtr)start, bytes.LongLength);
        }
    }

    [LibraryImport(Runtime, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr GetClass(string name);

    [LibraryImport(Runtime, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr RegisterName(string name);

    [LibraryImport(Runtime, EntryPoint = "objc_allocateClassPair", StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr AllocateClassPair(IntPtr superclass, string name, IntPtr extraBytes);

    [LibraryImport(Runtime, EntryPoint = "objc_registerClassPair")]
    public static partial void RegisterClassPair(IntPtr cls);

    [LibraryImport(Runtime, EntryPoint = "class_addMethod", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool AddMethod(IntPtr cls, IntPtr selector, IntPtr implementation, string types);

    [LibraryImport(Runtime, EntryPoint = "objc_autoreleasePoolPush")]
    public static partial IntPtr PoolPush();

    [LibraryImport(Runtime, EntryPoint = "objc_autoreleasePoolPop")]
    public static partial void PoolPop(IntPtr pool);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    private static partial IntPtr Send(IntPtr receiver, IntPtr selector);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    private static partial IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr argument);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    private static partial IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr first, IntPtr second);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    private static partial IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr first, IntPtr second, IntPtr third);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    private static partial IntPtr SendLong(IntPtr receiver, IntPtr selector, long argument);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    private static partial IntPtr SendDouble(IntPtr receiver, IntPtr selector, double argument);

    // BOOL is one byte on both macOS architectures (signed char on x64, bool on arm64).
    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    private static partial IntPtr SendByte(IntPtr receiver, IntPtr selector, byte argument);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    private static partial IntPtr SendSize(IntPtr receiver, IntPtr selector, Size argument);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    private static partial IntPtr SendObjectLong(IntPtr receiver, IntPtr selector, IntPtr first, long second);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    private static partial long SendForLong(IntPtr receiver, IntPtr selector);
}
