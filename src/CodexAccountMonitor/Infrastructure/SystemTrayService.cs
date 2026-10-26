namespace CodexAccountMonitor.Infrastructure;

public sealed class SystemTrayService : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.ToolStripMenuItem _clickThroughItem;
    private readonly System.Windows.Forms.ToolStripMenuItem _modelItem;
    private readonly System.Drawing.Icon? _icon;

    public SystemTrayService(
        Action show,
        Action refresh,
        Action toggleClickThrough,
        Action settings,
        Action exit,
        Action? toggleModel = null,
        Action? showModelLimits = null)
    {
        try { _icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }
        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = "Codex Account Monitor",
            Visible = true,
            Icon = _icon ?? System.Drawing.SystemIcons.Application
        };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Show monitor", null, (_, _) => show());
        menu.Items.Add("Refresh all accounts", null, (_, _) => refresh());
        _modelItem = new System.Windows.Forms.ToolStripMenuItem("Show model", null, (_, _) => toggleModel?.Invoke());
        menu.Items.Add(_modelItem);
        menu.Items.Add("Show model limits now", null, (_, _) => showModelLimits?.Invoke());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _clickThroughItem = new System.Windows.Forms.ToolStripMenuItem(
            "Click-through mode",
            null,
            (_, _) => toggleClickThrough());
        menu.Items.Add(_clickThroughItem);
        menu.Items.Add("Account settings", null, (_, _) => settings());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());
        _notifyIcon.ContextMenuStrip = menu;
        _notifyIcon.DoubleClick += (_, _) => show();
    }

    public void SetClickThroughChecked(bool enabled) => _clickThroughItem.Checked = enabled;
    public void SetModelVisible(bool visible) => _modelItem.Text = visible ? "Hide model" : "Show model";

    public void ShowNotification(string title, string message)
    {
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.ShowBalloonTip(3000);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _icon?.Dispose();
    }
}
