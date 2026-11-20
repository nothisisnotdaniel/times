using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Times.Tray;

/// <summary>
/// The macOS shell: an <c>NSStatusItem</c> in the menu bar whose <c>NSMenu</c> shows <see cref="TrayController.Model"/>,
/// built through the Objective-C runtime (<see cref="ObjC"/>), no package. The app is an accessory (no Dock icon, no
/// window). The info rows are disabled items, then Arrive, Break, Break end and Leave, then Refresh and Quit. All logic
/// lives in <see cref="TrayController"/>; this class only maps the model and forwards clicks, on the main thread.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacTray
{
    private const long AccessoryPolicy = 1;
    private const double VariableLength = -1;
    private const long RefreshTag = 100;
    private const long QuitTag = 101;
    private const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";
    private const string Osascript = "/usr/bin/osascript";

    private static MacTray? current;

    private readonly TrayController controller;
    private readonly IntPtr application;
    private readonly IntPtr target;
    private readonly IntPtr menu;
    private readonly IntPtr button;
    private readonly IntPtr inIcon;
    private readonly IntPtr outIcon;
    private readonly List<IntPtr> infoItems = [];
    private readonly Dictionary<TrayAction, IntPtr> actionItems = [];

    private MacTray(TrayController controller)
    {
        this.controller = controller;
        application = ObjC.Send(ObjC.Class("NSApplication"), "sharedApplication");
        ObjC.SendLong(application, "setActivationPolicy:", AccessoryPolicy);

        target = ObjC.Send(ObjC.Send(TargetClass(), "alloc"), "init");
        inIcon = Image(TrayIconState.In);
        outIcon = Image(TrayIconState.Out);

        menu = ObjC.Send(ObjC.Send(ObjC.Class("NSMenu"), "alloc"), "init");
        ObjC.SendBool(menu, "setAutoenablesItems:", false);
        ObjC.Send(menu, "setDelegate:", target);
        ObjC.Send(menu, "addItem:", ObjC.Send(ObjC.Class("NSMenuItem"), "separatorItem"));
        for (int i = 0; i < TrayMenuModel.Actions.Count; i++)
        {
            KeyValuePair<TrayAction, string> action = TrayMenuModel.Actions[i];
            IntPtr item = Item(action.Value, i + 1);
            actionItems.Add(action.Key, item);
            ObjC.Send(menu, "addItem:", item);
        }

        ObjC.Send(menu, "addItem:", ObjC.Send(ObjC.Class("NSMenuItem"), "separatorItem"));
        ObjC.Send(menu, "addItem:", Item("Refresh", RefreshTag));
        ObjC.Send(menu, "addItem:", Item("Quit", QuitTag));

        IntPtr statusBar = ObjC.Send(ObjC.Class("NSStatusBar"), "systemStatusBar");
        IntPtr statusItem = ObjC.Send(ObjC.SendDouble(statusBar, "statusItemWithLength:", VariableLength), "retain");
        ObjC.Send(statusItem, "setMenu:", menu);
        button = ObjC.Send(statusItem, "button");
    }

    /// <summary>Runs the menu bar app on the main thread until Quit.</summary>
    public static int Run()
    {
        // The Objective-C runtime finds AppKit's classes only once the framework is loaded into the process.
        if (!NativeLibrary.TryLoad(AppKit, out _) || ObjC.Class("NSApplication") == IntPtr.Zero)
        {
            Console.Error.WriteLine("Times.Tray could not load AppKit.");
            return 1;
        }

        ProcessRunner runner = new ProcessRunner();
        using TrayController controller = new TrayController(runner, TmLocator.Find, TimeProvider.System, new MacIdleSource());
        IntPtr pool = ObjC.PoolPush();
        MacTray tray = new MacTray(controller);
        current = tray;
        controller.Changed += (_, _) => tray.PostApply();
        controller.ReminderDue += (_, _) => Notify(runner);
        tray.Apply();
        controller.Start();
        ObjC.PoolPop(pool);

        ObjC.Send(tray.application, "run");
        return 0;
    }

    // Shows the check-in notice through osascript: a fixed script as one argument, no shell, no tm text. A click on it
    // cannot book (that needs a signed app bundle), so the text points to the menu. Its result is not needed, but a
    // fault is observed so it never surfaces as an unobserved task exception.
    private static void Notify(ProcessRunner runner) =>
        runner.RunAsync(Osascript, CheckInReminder.MacScriptArguments())
            .ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);

    // Changed comes on a worker thread; AppKit may only be touched on the main thread.
    private void PostApply()
    {
        IntPtr pool = ObjC.PoolPush();
        ObjC.Send(target, "performSelectorOnMainThread:withObject:waitUntilDone:", ObjC.Selector("apply:"), IntPtr.Zero, IntPtr.Zero);
        ObjC.PoolPop(pool);
    }

    // Puts the current model on the menu: as many disabled info items as rows, the enabled actions, icon and tooltip.
    private void Apply()
    {
        TrayMenuModel model = controller.Model;
        while (infoItems.Count < model.InfoRows.Count)
        {
            IntPtr item = Item(string.Empty, 0);
            ObjC.SendBool(item, "setEnabled:", false);
            ObjC.SendObjectLong(menu, "insertItem:atIndex:", item, infoItems.Count);
            infoItems.Add(item);
        }

        while (infoItems.Count > model.InfoRows.Count)
        {
            ObjC.Send(menu, "removeItem:", infoItems[^1]);
            ObjC.Send(infoItems[^1], "release");
            infoItems.RemoveAt(infoItems.Count - 1);
        }

        for (int i = 0; i < infoItems.Count; i++)
        {
            ObjC.Send(infoItems[i], "setTitle:", ObjC.String(model.InfoRows[i]));
        }

        foreach (KeyValuePair<TrayAction, IntPtr> pair in actionItems)
        {
            ObjC.SendBool(pair.Value, "setEnabled:", model.CanRun(pair.Key));
        }

        ObjC.Send(button, "setImage:", model.Icon == TrayIconState.In ? inIcon : outIcon);
        ObjC.Send(button, "setToolTip:", ObjC.String(model.Tooltip));
    }

    private void Clicked(long tag)
    {
        if (tag == QuitTag)
        {
            controller.Dispose();
            ObjC.Send(application, "terminate:", IntPtr.Zero);
        }
        else if (tag == RefreshTag)
        {
            controller.Refresh();
        }
        else if (tag >= 1 && tag <= TrayMenuModel.Actions.Count)
        {
            controller.Run(TrayMenuModel.Actions[(int)tag - 1].Key);
        }
    }

    private IntPtr Item(string title, long tag)
    {
        IntPtr item = ObjC.Send(
            ObjC.Send(ObjC.Class("NSMenuItem"), "alloc"),
            "initWithTitle:action:keyEquivalent:",
            ObjC.String(title),
            ObjC.Selector("itemClicked:"),
            ObjC.String(string.Empty));
        ObjC.Send(item, "setTarget:", target);
        ObjC.SendLong(item, "setTag:", tag);
        return item;
    }

    // A template image (macOS tints it for light and dark menu bars), drawn at 18 points from the 36 px PNG.
    private static IntPtr Image(TrayIconState state)
    {
        IntPtr image = ObjC.Send(ObjC.Send(ObjC.Class("NSImage"), "alloc"), "initWithData:", ObjC.Data(Program.IconBytes(state)));
        ObjC.SendSize(image, "setSize:", new ObjC.Size { Width = 18, Height = 18 });
        ObjC.SendBool(image, "setTemplate:", true);
        return image;
    }

    // The click target and menu delegate: an NSObject subclass whose three methods call back into this class.
    private static unsafe IntPtr TargetClass()
    {
        IntPtr cls = ObjC.AllocateClassPair(ObjC.Class("NSObject"), "TimesTrayTarget", IntPtr.Zero);
        ObjC.AddMethod(cls, ObjC.Selector("itemClicked:"), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&OnItemClicked, "v@:@");
        ObjC.AddMethod(cls, ObjC.Selector("menuWillOpen:"), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&OnMenuWillOpen, "v@:@");
        ObjC.AddMethod(cls, ObjC.Selector("apply:"), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&OnApply, "v@:@");
        ObjC.RegisterClassPair(cls);
        return cls;
    }

    // No exception may cross back into AppKit, so each callback stops it here; the next refresh shows the state again.
    [UnmanagedCallersOnly]
    private static void OnItemClicked(IntPtr self, IntPtr selector, IntPtr sender) =>
        Guard(() => current?.Clicked(ObjC.SendForLong(sender, "tag")));

    [UnmanagedCallersOnly]
    private static void OnMenuWillOpen(IntPtr self, IntPtr selector, IntPtr sender) =>
        Guard(() => current?.controller.MenuOpened());

    [UnmanagedCallersOnly]
    private static void OnApply(IntPtr self, IntPtr selector, IntPtr argument) => Guard(() => current?.Apply());

    private static void Guard(Action callback)
    {
        try
        {
            callback();
        }
        catch (Exception)
        {
            // Dropped on purpose: see above.
        }
    }
}
