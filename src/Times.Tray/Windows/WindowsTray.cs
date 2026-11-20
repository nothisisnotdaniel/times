using System.Drawing;
using System.Windows.Forms;

namespace Times.Tray;

/// <summary>
/// The Windows shell: a WinForms <see cref="NotifyIcon"/> in the notification area whose <see cref="ContextMenuStrip"/>
/// shows <see cref="TrayController.Model"/>; no window. The info rows are disabled items, then Arrive, Break, Break end
/// and Leave, then Refresh and Quit. All logic lives in <see cref="TrayController"/>; this class only maps the model and
/// forwards clicks, on the UI thread.
/// </summary>
internal sealed class WindowsTray : IDisposable
{
    private readonly TrayController controller;
    private readonly SynchronizationContext ui;
    private readonly NotifyIcon notifyIcon;
    private readonly ContextMenuStrip menu;
    private readonly Icon inIcon;
    private readonly Icon outIcon;
    private readonly List<ToolStripMenuItem> infoItems = [];
    private readonly Dictionary<TrayAction, ToolStripMenuItem> actionItems = [];
    private CheckInNotice? shownNotice;

    private WindowsTray(TrayController controller, SynchronizationContext ui)
    {
        this.controller = controller;
        this.ui = ui;
        inIcon = LoadIcon(TrayIconState.In);
        outIcon = LoadIcon(TrayIconState.Out);

        menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripSeparator());
        foreach (KeyValuePair<TrayAction, string> action in TrayMenuModel.Actions)
        {
            TrayAction which = action.Key;
            ToolStripMenuItem item = new ToolStripMenuItem(action.Value);
            item.Click += (_, _) => controller.Run(which);
            actionItems.Add(which, item);
            menu.Items.Add(item);
        }

        menu.Items.Add(new ToolStripSeparator());
        ToolStripMenuItem refresh = new ToolStripMenuItem("Refresh");
        refresh.Click += (_, _) => controller.Refresh();
        menu.Items.Add(refresh);
        ToolStripMenuItem quit = new ToolStripMenuItem("Quit");
        quit.Click += (_, _) => Application.ExitThread();
        menu.Items.Add(quit);
        menu.Opening += (_, _) => controller.MenuOpened();

        notifyIcon = new NotifyIcon { ContextMenuStrip = menu, Icon = outIcon, Visible = true };
        notifyIcon.BalloonTipClicked += (_, _) =>
        {
            if (shownNotice is { } notice)
            {
                controller.ArriveFromReminder(notice);
            }
        };
        notifyIcon.BalloonTipClosed += (_, _) => shownNotice = null;
    }

    /// <summary>Runs the notification-area app until Quit.</summary>
    public static int Run()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using WindowsFormsSynchronizationContext ui = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(ui);
        using TrayController controller = new TrayController(
            new ProcessRunner(), TmLocator.Find, TimeProvider.System, new WindowsIdleSource());
        using WindowsTray tray = new WindowsTray(controller, ui);
        controller.Changed += (_, _) => tray.ui.Post(_ => tray.Apply(), null);
        controller.ReminderDue += (_, notice) => tray.ui.Post(_ => tray.ShowReminder(notice), null);
        tray.Apply();
        controller.Start();
        Application.Run();
        return 0;
    }

    public void Dispose()
    {
        notifyIcon.Visible = false;
        notifyIcon.Dispose();
        menu.Dispose();
        inIcon.Dispose();
        outIcon.Dispose();
    }

    // Shows the check-in notice as a balloon; a click on it books the arrival at the notice's time.
    private void ShowReminder(CheckInNotice notice)
    {
        shownNotice = notice;
        notifyIcon.ShowBalloonTip(0, CheckInReminder.Title, CheckInReminder.Text(notice, canBook: true), ToolTipIcon.Info);
    }

    // Puts the current model on the menu: as many disabled info items as rows, the enabled actions, icon and tooltip.
    private void Apply()
    {
        TrayMenuModel model = controller.Model;
        while (infoItems.Count < model.InfoRows.Count)
        {
            ToolStripMenuItem item = new ToolStripMenuItem { Enabled = false };
            menu.Items.Insert(infoItems.Count, item);
            infoItems.Add(item);
        }

        while (infoItems.Count > model.InfoRows.Count)
        {
            ToolStripMenuItem last = infoItems[^1];
            menu.Items.Remove(last);
            last.Dispose();
            infoItems.RemoveAt(infoItems.Count - 1);
        }

        for (int i = 0; i < infoItems.Count; i++)
        {
            infoItems[i].Text = TrayText.WindowsMenuText(model.InfoRows[i]);
        }

        foreach (KeyValuePair<TrayAction, ToolStripMenuItem> pair in actionItems)
        {
            pair.Value.Enabled = model.CanRun(pair.Key);
        }

        notifyIcon.Icon = model.Icon == TrayIconState.In ? inIcon : outIcon;
        notifyIcon.Text = TrayText.WindowsTooltip(model.Tooltip);
    }

    private static Icon LoadIcon(TrayIconState state)
    {
        using MemoryStream stream = new MemoryStream(Program.IconBytes(state));
        using Bitmap bitmap = new Bitmap(stream);
        using Icon handle = Icon.FromHandle(bitmap.GetHicon());
        return (Icon)handle.Clone();
    }
}
