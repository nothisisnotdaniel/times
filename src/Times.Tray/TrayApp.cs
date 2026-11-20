using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace Times.Tray;

/// <summary>
/// The thin shell: one <see cref="TrayIcon"/> whose <see cref="NativeMenu"/> shows <see cref="TrayController.Model"/>.
/// The info rows are disabled items, then Arrive, Break, Break end and Leave, then Refresh and Quit. All logic lives in
/// <see cref="TrayController"/>; this class only maps the model and forwards clicks.
/// </summary>
internal sealed class TrayApp : Application
{
    private readonly List<NativeMenuItem> infoItems = [];
    private readonly Dictionary<TrayAction, NativeMenuItem> actionItems = [];
    private TrayController? controller;
    private TrayIcon? tray;
    private NativeMenu? menu;
    private WindowIcon? inIcon;
    private WindowIcon? outIcon;

    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        inIcon = LoadIcon("in");
        outIcon = LoadIcon("out");
        controller = new TrayController(new ProcessRunner(), TmLocator.Find, TimeProvider.System);

        menu = new NativeMenu();
        menu.Add(new NativeMenuItemSeparator());
        AddAction(menu, TrayAction.Arrive, "Arrive");
        AddAction(menu, TrayAction.Break, "Break");
        AddAction(menu, TrayAction.BreakEnd, "Break end");
        AddAction(menu, TrayAction.Leave, "Leave");
        menu.Add(new NativeMenuItemSeparator());
        NativeMenuItem refresh = new NativeMenuItem("Refresh");
        refresh.Click += (_, _) => controller.Refresh();
        menu.Add(refresh);
        NativeMenuItem quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) => desktop.Shutdown();
        menu.Add(quit);
        menu.Opening += (_, _) => controller.MenuOpened();

        tray = new TrayIcon { Menu = menu, IsVisible = true };
        MacOSProperties.SetIsTemplateIcon(tray, true);
        TrayIcon.SetIcons(this, [tray]);

        controller.Changed += (_, _) => Dispatcher.UIThread.Post(Apply);
        desktop.Exit += (_, _) =>
        {
            controller.Dispose();
            tray.Dispose();
        };
        Apply();
        controller.Start();
    }

    private void AddAction(NativeMenu target, TrayAction action, string header)
    {
        NativeMenuItem item = new NativeMenuItem(header);
        item.Click += (_, _) => controller?.Run(action);
        actionItems.Add(action, item);
        target.Add(item);
    }

    // Puts the current model on the menu: as many disabled info items as rows, the enabled actions, icon and tooltip.
    private void Apply()
    {
        if (controller is null || menu is null || tray is null)
        {
            return;
        }

        TrayMenuModel model = controller.Model;
        while (infoItems.Count < model.InfoRows.Count)
        {
            NativeMenuItem item = new NativeMenuItem { IsEnabled = false };
            menu.Items.Insert(infoItems.Count, item);
            infoItems.Add(item);
        }

        while (infoItems.Count > model.InfoRows.Count)
        {
            menu.Items.Remove(infoItems[^1]);
            infoItems.RemoveAt(infoItems.Count - 1);
        }

        for (int i = 0; i < infoItems.Count; i++)
        {
            infoItems[i].Header = model.InfoRows[i];
        }

        foreach (KeyValuePair<TrayAction, NativeMenuItem> pair in actionItems)
        {
            pair.Value.IsEnabled = model.CanRun(pair.Key);
        }

        tray.Icon = model.Icon == TrayIconState.In ? inIcon : outIcon;
        tray.ToolTipText = model.Tooltip;
    }

    private static WindowIcon LoadIcon(string name)
    {
        using Stream stream = typeof(TrayApp).Assembly.GetManifestResourceStream($"Times.Tray.{name}.png")
            ?? throw new InvalidOperationException($"The {name} icon is not embedded.");
        return new WindowIcon(stream);
    }
}
