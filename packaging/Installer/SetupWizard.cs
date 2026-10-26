using System.Drawing.Drawing2D;
using System.Reflection;

namespace CodexAccountMonitor.Setup;

internal sealed record SetupSelection(string InstallRoot, bool StartWithWindows);
internal sealed record SetupProgress(int Percent, string Message);
internal sealed record SetupResult(int ExitCode, string Message);

internal sealed class SetupWizard : Form
{
    private static readonly Color Ink = Color.FromArgb(28, 27, 42);
    private static readonly Color Muted = Color.FromArgb(105, 102, 121);
    private static readonly Color Purple = Color.FromArgb(111, 72, 216);
    private readonly Func<string, string?> _validateRoot;
    private readonly Func<SetupSelection, IProgress<SetupProgress>, Task<SetupResult>> _install;
    private readonly Label _step = MakeLabel(string.Empty, 9, Purple, true);
    private readonly Label _heading = MakeLabel(string.Empty, 25, Ink, true);
    private readonly Label _subtitle = MakeLabel(string.Empty, 10.5f, Muted);
    private readonly Panel _pageHost = new() { Dock = DockStyle.Fill, Margin = new Padding(0, 22, 0, 18) };
    private readonly TextBox _folder = new() { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 10), Margin = new Padding(0, 8, 10, 8) };
    private readonly CheckBox _startup = new() { Text = "Start Codex Lover when I sign in to Windows", AutoSize = true, Margin = new Padding(0, 18, 0, 0) };
    private readonly Label _validation = MakeLabel(string.Empty, 9, Color.FromArgb(177, 47, 68));
    private readonly Label _progressMessage = MakeLabel("Preparing installation…", 11, Ink);
    private readonly Label _progressPercent = MakeLabel("0%", 10, Muted);
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Top, Height = 12, Style = ProgressBarStyle.Continuous, Margin = new Padding(0, 12, 0, 16), AccessibleName = "Installation progress" };
    private readonly Label _completionMessage = MakeLabel(string.Empty, 11, Muted);
    private readonly Label _completionSymbol = MakeLabel("✓", 36, Color.FromArgb(39, 135, 104), true);
    private readonly WizardButton _back = new("Back", false);
    private readonly WizardButton _primary = new("Next", true);
    private readonly WizardButton _cancel = new("Cancel", false);
    private readonly Control _welcomePage;
    private readonly Control _optionsPage;
    private readonly Control _progressPage;
    private readonly Control _completionPage;
    private Page _page;
    private bool _installing;
    private bool _completedSuccessfully;

    internal int ExitCode { get; private set; }
    internal SetupSelection? Selection { get; private set; }

    internal SetupWizard(string defaultRoot, bool startup, Func<string, string?> validateRoot,
        Func<SetupSelection, IProgress<SetupProgress>, Task<SetupResult>> install)
    {
        _validateRoot = validateRoot;
        _install = install;
        Text = "Install Codex Lover";
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(840, 570);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Color.White;
        Font = new Font("Segoe UI", 10);
        _folder.Text = defaultRoot;
        _folder.AccessibleName = "Installation folder";
        _startup.Checked = startup;
        _validation.Margin = new Padding(0, 8, 0, 0);
        _validation.MaximumSize = new Size(530, 0);
        _folder.TextChanged += (_, _) => _validation.Text = string.Empty;

        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("setup-logo.ico"))
        {
            if (stream is not null)
            {
                using var sourceIcon = new Icon(stream, new Size(128, 128));
                Icon = (Icon)sourceIcon.Clone();
            }
        }

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 216));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(BuildBrandPanel(), 0, 0);

        var main = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5,
            Padding = new Padding(34, 32, 34, 24), Margin = Padding.Empty
        };
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _step.Margin = new Padding(0, 0, 0, 18);
        _heading.Margin = new Padding(0, 0, 0, 12);
        _heading.MaximumSize = new Size(550, 0);
        _subtitle.MaximumSize = new Size(550, 0);
        _subtitle.Margin = Padding.Empty;
        main.Controls.Add(_step, 0, 0);
        main.Controls.Add(_heading, 0, 1);
        main.Controls.Add(_subtitle, 0, 2);
        main.Controls.Add(_pageHost, 0, 3);
        main.Controls.Add(BuildFooter(), 0, 4);
        layout.Controls.Add(main, 1, 0);
        Controls.Add(layout);

        _welcomePage = BuildWelcomePage();
        _optionsPage = BuildOptionsPage();
        _progressPage = BuildProgressPage();
        _completionPage = BuildCompletionPage();
        _pageHost.Controls.AddRange([_welcomePage, _optionsPage, _progressPage, _completionPage]);
        _back.Click += (_, _) => ShowPage(Page.Welcome);
        _cancel.Click += (_, _) => Close();
        _primary.Click += async (_, _) => await OnPrimaryAsync();
        AcceptButton = _primary;
        CancelButton = _cancel;
        ShowPage(Page.Welcome);
    }

    private Control BuildBrandPanel()
    {
        var brand = new BrandPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(26, 34, 22, 26) };
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, BackColor = Color.Transparent };
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.Controls.Add(new CodexLoverLogo { Size = new Size(72, 72), AccessibleName = "Codex Lover logo" }, 0, 0);
        var name = MakeLabel("Codex\nLover", 25, Color.White, true);
        name.Margin = new Padding(0, 0, 0, 20);
        content.Controls.Add(name, 0, 1);
        var tagline = MakeLabel("Your accounts.\nOne place.", 12, Color.FromArgb(231, 221, 255));
        tagline.Margin = Padding.Empty;
        content.Controls.Add(tagline, 0, 2);
        content.Controls.Add(MakeLabel("MADE FOR YOUR\nWINDOWS DESKTOP", 8, Color.FromArgb(219, 205, 252), true), 0, 4);
        brand.Controls.Add(content);
        return brand;
    }

    private Control BuildFooter()
    {
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 3; i++) footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var details = new LinkLabel
        {
            Text = "Installation details", AutoSize = true, LinkColor = Muted,
            ActiveLinkColor = Purple, VisitedLinkColor = Muted, LinkBehavior = LinkBehavior.HoverUnderline,
            Anchor = AnchorStyles.Left, Font = new Font("Segoe UI", 8.5f), Margin = new Padding(0, 0, 8, 0)
        };
        details.LinkClicked += (_, _) =>
        {
            var message = "Installs Codex Lover for your Windows account. Earlier releases and saved account data are preserved.";
#if UNSIGNED_BETA
            message += "\n\nThis installer is unsigned. Windows has not verified its publisher identity.";
#endif
            MessageBox.Show(this, message, "Codex Lover installation details", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
        footer.Controls.Add(details, 0, 0);
        footer.Controls.Add(_back, 1, 0);
        footer.Controls.Add(_cancel, 2, 0);
        footer.Controls.Add(_primary, 3, 0);
        return footer;
    }

    private Control BuildWelcomePage()
    {
        var list = VerticalPage();
        AddFeature(list, "Your usage, at a glance", "Keep account limits and reset times close by.");
        AddFeature(list, "Your accounts, together", "Manage saved accounts from one desktop widget.");
        AddFeature(list, "A setup that fits you", "Choose an installation folder and startup preference.");
        return list;
    }

    private static void AddFeature(FlowLayoutPanel list, string title, string description)
    {
        var card = new Panel { Width = 548, Height = 69, BackColor = Color.FromArgb(247, 245, 253), Margin = new Padding(0, 0, 0, 12) };
        var bar = new Panel { Width = 3, Dock = DockStyle.Left, BackColor = Color.FromArgb(199, 177, 244) };
        var text = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16, 10, 8, 4), FlowDirection = FlowDirection.TopDown, WrapContents = false };
        text.Controls.Add(MakeLabel(title, 10.5f, Ink, true));
        text.Controls.Add(MakeLabel(description, 9, Muted));
        card.Controls.Add(text);
        card.Controls.Add(bar);
        list.Controls.Add(card);
        list.SizeChanged += (_, _) => card.Width = Math.Max(200, list.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 2);
    }

    private Control BuildOptionsPage()
    {
        var options = VerticalPage();
        var label = MakeLabel("Installation folder", 10, Ink, true);
        label.Margin = Padding.Empty;
        options.Controls.Add(label);
        var row = new TableLayoutPanel { Width = 548, Height = 48, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var browse = new WizardButton("Browse…", false) { Margin = new Padding(0, 2, 0, 4) };
        browse.Click += (_, _) => BrowseFolder();
        row.Controls.Add(_folder, 0, 0);
        row.Controls.Add(browse, 1, 0);
        options.Controls.Add(row);
        var hint = MakeLabel("Choose a folder you can write to. Your saved accounts stay in their current location.", 9, Muted);
        hint.MaximumSize = new Size(520, 0);
        options.Controls.Add(hint);
        options.Controls.Add(_validation);
        options.Controls.Add(_startup);
        options.SizeChanged += (_, _) =>
        {
            var width = Math.Max(200, options.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 2);
            row.Width = width;
            hint.MaximumSize = new Size(width, 0);
            _validation.MaximumSize = new Size(width, 0);
        };
        return options;
    }

    private Control BuildProgressPage()
    {
        var content = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 3, Margin = Padding.Empty };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _progressMessage.MaximumSize = new Size(470, 0);
        _progressPercent.Anchor = AnchorStyles.Right;
        content.Controls.Add(_progressMessage, 0, 0);
        content.Controls.Add(_progressPercent, 1, 0);
        content.Controls.Add(_progress, 0, 1);
        content.SetColumnSpan(_progress, 2);
        var hint = MakeLabel("This window will let you know when everything is ready.", 9, Muted);
        hint.MaximumSize = new Size(540, 0);
        content.Controls.Add(hint, 0, 2);
        content.SetColumnSpan(hint, 2);
        var page = new Panel { Dock = DockStyle.Fill };
        page.Controls.Add(content);
        return page;
    }

    private Control BuildCompletionPage()
    {
        var content = VerticalPage();
        _completionSymbol.Margin = new Padding(0, 0, 0, 18);
        _completionMessage.MaximumSize = new Size(530, 0);
        content.Controls.Add(_completionSymbol);
        content.Controls.Add(_completionMessage);
        content.SizeChanged += (_, _) => _completionMessage.MaximumSize = new Size(Math.Max(200, content.ClientSize.Width - 20), 0);
        return content;
    }

    private void BrowseFolder()
    {
        using var browse = new FolderBrowserDialog
        {
            Description = "Choose where to install Codex Lover", UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };
        if (Directory.Exists(_folder.Text)) browse.SelectedPath = _folder.Text;
        if (browse.ShowDialog(this) == DialogResult.OK) _folder.Text = browse.SelectedPath;
    }

    private async Task OnPrimaryAsync()
    {
        if (_installing) return;
        if (_page == Page.Welcome) { ShowPage(Page.Options); return; }
        if (_page == Page.Complete)
        {
            if (_completedSuccessfully) { DialogResult = DialogResult.OK; Close(); }
            else ShowPage(Page.Options);
            return;
        }
        if (_page != Page.Options) return;
        var root = _folder.Text.Trim();
        string? problem;
        try { problem = _validateRoot(root); }
        catch (Exception exception) { problem = exception.Message; }
        if (!string.IsNullOrWhiteSpace(problem))
        {
            _validation.Text = problem;
            _folder.Focus();
            return;
        }
        Selection = new SetupSelection(root, _startup.Checked);
        _installing = true;
        ControlBox = false;
        ShowPage(Page.Progress);
        // Created on the dialog thread so background installation progress is marshalled here.
        var progress = new Progress<SetupProgress>(value =>
        {
            if (IsDisposed || !_installing) return;
            _progress.Value = Math.Clamp(value.Percent, 0, 100);
            _progressPercent.Text = _progress.Value + "%";
            _progressMessage.Text = value.Message;
        });
        SetupResult result;
        try { result = await _install(Selection, progress); }
        catch (Exception exception) { result = new SetupResult(1, exception.Message); }
        _installing = false;
        ControlBox = true;
        ExitCode = result.ExitCode;
        _completedSuccessfully = result.ExitCode == 0;
        _completionMessage.Text = result.Message;
        _completionSymbol.Text = _completedSuccessfully ? "✓" : "!";
        _completionSymbol.ForeColor = _completedSuccessfully ? Color.FromArgb(39, 135, 104) : Color.FromArgb(179, 97, 24);
        ShowPage(Page.Complete);
    }

    private void ShowPage(Page page)
    {
        _page = page;
        _welcomePage.Visible = page == Page.Welcome;
        _optionsPage.Visible = page == Page.Options;
        _progressPage.Visible = page == Page.Progress;
        _completionPage.Visible = page == Page.Complete;
        _back.Visible = page == Page.Options;
        _back.Enabled = !_installing;
        _cancel.Enabled = !_installing;
        _primary.Enabled = !_installing;
        _cancel.Text = page == Page.Complete ? "Close" : "Cancel";
        _primary.Text = page switch { Page.Welcome => "Next", Page.Options => "Install", Page.Progress => "Installing…", _ => _completedSuccessfully ? "Finish" : "Retry" };
        _step.Text = page switch { Page.Welcome => "01  WELCOME", Page.Options => "02  YOUR SETUP", Page.Progress => "03  INSTALLATION", _ => _completedSuccessfully ? "03  COMPLETE" : "03  INSTALLATION" };
        _heading.Text = page switch { Page.Welcome => "Welcome to Codex Lover", Page.Options => "Choose your setup", Page.Progress => "Making things ready", _ => _completedSuccessfully ? "You're all set." : "Let's try that again" };
        _subtitle.Text = page switch
        {
            Page.Welcome => "Your Codex accounts, beautifully together.",
            Page.Options => "Install for your Windows account, just the way you like.",
            Page.Progress => "Installing Codex Lover in your selected folder.",
            _ => _completedSuccessfully ? "Codex Lover is ready for you." : "Setup could not finish. Review the details below."
        };
        if (page == Page.Progress) { _progress.Value = 0; _progressPercent.Text = "0%"; _progressMessage.Text = "Preparing installation…"; }
        if (page == Page.Options) _folder.Focus();
        else if (page != Page.Progress) _primary.Focus();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_installing) e.Cancel = true;
        base.OnFormClosing(e);
    }

    protected override void OnShown(EventArgs e)
    {
        var workingArea = Screen.FromControl(this).WorkingArea;
        FitWorkingArea(workingArea.Size);
        if (StartPosition != FormStartPosition.Manual)
            Location = new Point(workingArea.Left + (workingArea.Width - Width) / 2,
                workingArea.Top + (workingArea.Height - Height) / 2);
        base.OnShown(e);
    }

    internal void FitWorkingArea(Size available)
    {
        Size = new Size(Math.Min(Width, Math.Max(400, available.Width - 20)),
            Math.Min(Height, Math.Max(400, available.Height - 20)));
        var scale = _primary.Width / 110f;
        var layout = (TableLayoutPanel)Controls[0];
        var brand = layout.GetControlFromPosition(0, 0)!;
        var main = (TableLayoutPanel)layout.GetControlFromPosition(1, 0)!;
        var compact = ClientSize.Width < 800 * scale;
        brand.Visible = !compact;
        layout.ColumnStyles[0].Width = compact ? 0 : 216 * scale;
        if (compact) main.Padding = new Padding((int)(20 * scale), (int)(18 * scale), (int)(20 * scale), (int)(18 * scale));
        layout.PerformLayout();
        var textWidth = Math.Max(200, main.ClientSize.Width - main.Padding.Horizontal);
        _heading.MaximumSize = new Size(textWidth, 0);
        _subtitle.MaximumSize = new Size(textWidth, 0);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Icon?.Dispose();
        base.Dispose(disposing);
    }

    private static FlowLayoutPanel VerticalPage() => new()
    {
        Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
        AutoScroll = true, Margin = Padding.Empty, Padding = Padding.Empty
    };

    private static Label MakeLabel(string text, float size, Color color, bool bold = false) => new()
    {
        Text = text, AutoSize = true, ForeColor = color, BackColor = Color.Transparent,
        Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular), Margin = new Padding(0, 0, 0, 4)
    };

    private enum Page { Welcome, Options, Progress, Complete }

    private sealed class CodexLoverLogo : Control
    {
        internal CodexLoverLogo()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint |
                ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Color.Transparent;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            // The existing codexlover.com favicon, rendered as vectors for sharp scaling.
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.ScaleTransform(Width / 64f, Height / 64f);
            using var tile = new GraphicsPath();
            tile.AddArc(0, 0, 36, 36, 180, 90);
            tile.AddArc(28, 0, 36, 36, 270, 90);
            tile.AddArc(28, 28, 36, 36, 0, 90);
            tile.AddArc(0, 28, 36, 36, 90, 90);
            tile.CloseFigure();
            using var fill = new SolidBrush(Color.FromArgb(101, 82, 232));
            e.Graphics.FillPath(fill, tile);
            using var stroke = new Pen(Color.White, 5) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            e.Graphics.DrawLines(stroke, [new PointF(17, 20), new PointF(29, 31), new PointF(17, 42)]);
            e.Graphics.DrawLine(stroke, 35, 42, 46, 42);
        }
    }

    private sealed class BrandPanel : Panel
    {
        internal BrandPanel() => DoubleBuffered = true;
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using var gradient = new LinearGradientBrush(ClientRectangle, Color.FromArgb(83, 47, 163), Color.FromArgb(139, 82, 221), 75f);
            e.Graphics.FillRectangle(gradient, ClientRectangle);
        }
    }

    private sealed class WizardButton : Button
    {
        private readonly bool _accent;
        private bool _hover;
        internal WizardButton(string text, bool accent)
        {
            _accent = accent;
            Text = text;
            Size = new Size(accent ? 110 : 84, 42);
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Margin = new Padding(8, 0, 0, 0);
            Cursor = Cursors.Hand;
            DoubleBuffered = true;
        }
        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent?.BackColor ?? Color.White);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new RectangleF(1, 1, Width - 2, Height - 2);
            var radius = Math.Min(10f * DeviceDpi / 96f, Height / 3f);
            using var shape = new GraphicsPath();
            shape.AddArc(bounds.Left, bounds.Top, radius, radius, 180, 90);
            shape.AddArc(bounds.Right - radius, bounds.Top, radius, radius, 270, 90);
            shape.AddArc(bounds.Right - radius, bounds.Bottom - radius, radius, radius, 0, 90);
            shape.AddArc(bounds.Left, bounds.Bottom - radius, radius, radius, 90, 90);
            shape.CloseFigure();
            var fill = _accent ? (_hover ? Color.FromArgb(92, 55, 192) : Purple) : (_hover ? Color.FromArgb(242, 239, 250) : Color.White);
            if (!Enabled) fill = _accent ? Color.FromArgb(190, 174, 228) : Color.FromArgb(248, 247, 250);
            using var brush = new SolidBrush(fill);
            e.Graphics.FillPath(brush, shape);
            if (!_accent) { using var border = new Pen(Color.FromArgb(224, 220, 232)); e.Graphics.DrawPath(border, shape); }
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Enabled ? (_accent ? Color.White : Ink) : (_accent ? Color.White : Muted), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -5, -5), _accent ? Color.White : Purple, fill);
        }
    }
}
