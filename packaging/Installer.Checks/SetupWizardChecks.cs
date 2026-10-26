using System.Reflection;
using System.Runtime.ExceptionServices;
using CodexAccountMonitor.Setup;

internal static class SetupWizardChecks
{
    internal static void Run(Action<string, Action> check)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                using var context = new ApplicationContext();
                using var dispatcher = new Control();
                _ = dispatcher.Handle;
                dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        foreach (var scale in new[] { 1f, 2f })
                            check($"setup wizard navigation, validation, async progress and completion at {scale * 100:0}% scale" + (scale == 2 ? " on a 1366x768 display" : ""), () => Verify(scale));
                    }
                    catch (Exception exception) { failure = exception; }
                    finally { context.ExitThread(); }
                });
                Application.Run(context);
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void Verify(float scale)
    {
        var callbackCount = 0;
        SetupSelection? selection = null;
        IProgress<SetupProgress>? progress = null;
        var result = new TaskCompletionSource<SetupResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var wizard = new SetupWizard(@"D:\Apps\CodexLover", true,
            path => path == @"D:\Apps\CodexLover" ? null : "Choose the synthetic test installation folder.",
            (chosen, reporter) => { callbackCount++; selection = chosen; progress = reporter; return result.Task; });
        wizard.ShowInTaskbar = false;
        _ = wizard.Handle;
        wizard.StartPosition = FormStartPosition.Manual;
        wizard.Location = new Point(-32000, -32000);
        wizard.Opacity = 0;
        wizard.Show();
        Application.DoEvents();
        if (scale != 1)
        {
            var fonts = Descendants(wizard).ToDictionary(control => control, control => control.Font);
            wizard.Scale(new SizeF(scale, scale));
            foreach (var pair in fonts) pair.Key.Font = new Font(pair.Value.FontFamily, pair.Value.Size * scale, pair.Value.Style);
            wizard.FitWorkingArea(new Size(1366, 728)); // Reserve 40 pixels for the taskbar.
            Assert(wizard.Width <= 1366 && wizard.Height <= 728, "Scaled wizard exceeds the constrained working area.");
        }
        Layout(wizard);
        Assert(Page(wizard) == "Welcome" && wizard.ExitCode == 0, "Wizard must open on Welcome without a failure result.");
        Assert(!Get<Button>(wizard, "_back").Visible, "Welcome must not show Back.");
        Render(wizard, scale, "welcome");
        Advance(wizard).GetAwaiter().GetResult();
        Assert(Page(wizard) == "Options", "Next did not open installation options.");
        var folder = Get<TextBox>(wizard, "_folder");
        folder.Text = "invalid";
        Advance(wizard).GetAwaiter().GetResult();
        Assert(callbackCount == 0 && Page(wizard) == "Options", "An invalid folder started installation.");
        Assert(Get<Label>(wizard, "_validation").Text.Contains("synthetic"), "Invalid-folder feedback was not shown.");
        folder.Text = @"D:\Apps\CodexLover";
        Assert(Get<CheckBox>(wizard, "_startup").Checked, "The initial startup selection was not preserved.");
        Layout(wizard);
        AssertVisibleWithinClient(wizard, Get<Button>(wizard, "_primary"));
        AssertVisibleWithinClient(wizard, folder);
        Render(wizard, scale, "options");
        Get<CheckBox>(wizard, "_startup").Checked = false;
        var installation = Advance(wizard);
        Assert(!installation.IsCompleted && callbackCount == 1 && Page(wizard) == "Progress", "The wizard did not await its installation callback.");
        Assert(selection is { InstallRoot: @"D:\Apps\CodexLover", StartWithWindows: false }, "Chosen folder or startup preference was lost.");
        Assert(!wizard.ControlBox && !Get<Button>(wizard, "_cancel").Enabled && !Get<Button>(wizard, "_primary").Enabled, "Installation left close/action controls enabled.");
        var closing = new FormClosingEventArgs(CloseReason.UserClosing, false);
        typeof(SetupWizard).GetMethod("OnFormClosing", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(wizard, [closing]);
        Assert(closing.Cancel, "Window close was not blocked during installation.");
        Task.Run(() => progress!.Report(new SetupProgress(56, "Installing app files…"))).GetAwaiter().GetResult();
        PumpUntil(() => Get<ProgressBar>(wizard, "_progress").Value == 56);
        Render(wizard, scale, "progress");
        result.SetResult(new SetupResult(0, "Codex Lover is ready. You can open it from the Start menu."));
        PumpUntil(() => installation.IsCompleted);
        installation.GetAwaiter().GetResult();
        Assert(Page(wizard) == "Complete" && wizard.ExitCode == 0 && wizard.ControlBox, "A successful install did not restore the completion controls.");
        Assert(Get<Button>(wizard, "_primary").Text == "Finish", "Successful installation did not offer Finish.");
        Assert(Get<Label>(wizard, "_completionMessage").Visible, "Completion details are not visible.");
        AssertVisibleWithinClient(wizard, Get<Label>(wizard, "_completionMessage"));
        Render(wizard, scale, "complete");

        using var failed = new SetupWizard(@"D:\Apps\CodexLover", false, _ => null,
            (_, _) => Task.FromResult(new SetupResult(1, "Synthetic installation failure.")));
        Advance(failed).GetAwaiter().GetResult();
        Advance(failed).GetAwaiter().GetResult();
        Assert(failed.ExitCode == 1 && Get<Button>(failed, "_primary").Text == "Retry", "A failed installation did not offer Retry.");
        Advance(failed).GetAwaiter().GetResult();
        Assert(Page(failed) == "Options", "Retry did not return to editable installation options.");
    }

    private static Task Advance(SetupWizard wizard) => (Task)typeof(SetupWizard)
        .GetMethod("OnPrimaryAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(wizard, null)!;
    private static T Get<T>(SetupWizard wizard, string name) => (T)typeof(SetupWizard)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(wizard)!;
    private static string Page(SetupWizard wizard) => Get<object>(wizard, "_page").ToString()!;
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private static void PumpUntil(Func<bool> condition)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Synthetic wizard callback did not complete.");
            Application.DoEvents();
            Thread.Sleep(1);
        }
        Application.DoEvents();
    }

    private static void Layout(Control control)
    {
        control.PerformLayout();
        foreach (Control child in control.Controls) Layout(child);
    }

    private static IEnumerable<Control> Descendants(Control control)
    {
        yield return control;
        foreach (Control child in control.Controls)
            foreach (var descendant in Descendants(child)) yield return descendant;
    }

    private static void AssertVisibleWithinClient(SetupWizard wizard, Control control)
    {
        var rectangle = wizard.RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
        Assert(wizard.ClientRectangle.Contains(rectangle), "An essential setup control is clipped outside the dialog.");
    }

    private static void Render(SetupWizard wizard, float scale, string name)
    {
        Layout(wizard);
        var directory = Environment.GetEnvironmentVariable("CODEX_LOVER_UI_PREVIEW");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        using var bitmap = new Bitmap(wizard.Width, wizard.Height);
        wizard.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(Path.Combine(directory, $"setup-{name}-{scale * 100:0}percent.png"), System.Drawing.Imaging.ImageFormat.Png);
    }
}
