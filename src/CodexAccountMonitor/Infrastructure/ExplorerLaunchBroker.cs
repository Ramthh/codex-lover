using System.Runtime.InteropServices;

namespace CodexAccountMonitor.Infrastructure;

/// <summary>Uses the existing Explorer desktop's automation object, not an in-process Shell.Application.</summary>
public sealed class ExplorerLaunchBroker
{
    public Task LaunchAsync(string executablePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(executablePath) || !File.Exists(executablePath))
            throw new InvalidOperationException("The installed Codex Lover executable could not be found.");
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { cancellationToken.ThrowIfCancellationRequested(); LaunchFromDesktop(executablePath, QuoteArguments(arguments), cancellationToken); result.TrySetResult(); }
            catch (Exception exception) { result.TrySetException(exception); }
        }) { IsBackground = true, Name = "Codex Lover Explorer launch" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task;
    }

    internal static string QuoteArguments(IEnumerable<string> arguments) => string.Join(" ", arguments.Select(QuoteArgument));

    private static string QuoteArgument(string value)
    {
        // Windows CommandLineToArgvW quoting, not shell interpolation.
        var result = new System.Text.StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"') result.Append('\\', slashes * 2 + 1).Append('"');
            else result.Append('\\', slashes).Append(character);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    private static void LaunchFromDesktop(string executablePath, string arguments, CancellationToken cancellationToken)
    {
        var objects = new List<object>();
        try
        {
            var windowsType = Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"), throwOnError: true)!;
            dynamic windows = Activator.CreateInstance(windowsType)!;
            objects.Add(windows);
            object desktopLocation = 0; // CSIDL_DESKTOP
            object? empty = null;
            int desktopHandle = 0;
            object desktop = windows.FindWindowSW(ref desktopLocation, ref empty, 8, out desktopHandle, 1); // SWC_DESKTOP / SWFO_NEEDDISPATCH
            objects.Add(desktop);
            var shellWindow = GetShellWindow();
            if (shellWindow == IntPtr.Zero) throw new InvalidOperationException("Windows Explorer desktop is unavailable.");
            GetWindowThreadProcessId(shellWindow, out var expectedShellPid);
            GetWindowThreadProcessId(new IntPtr(unchecked((long)(uint)desktopHandle)), out var desktopPid);
            if (expectedShellPid == 0 || desktopPid != expectedShellPid)
                throw new InvalidOperationException("Windows Explorer desktop could not be verified.");

            var serviceProvider = (IComServiceProvider)desktop;
            var topLevelBrowser = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
            var shellBrowserId = typeof(IShellBrowser).GUID;
            Marshal.ThrowExceptionForHR(serviceProvider.QueryService(ref topLevelBrowser, ref shellBrowserId, out var browserPointer));
            IShellBrowser browser;
            try { browser = (IShellBrowser)Marshal.GetObjectForIUnknown(browserPointer); }
            finally { Marshal.Release(browserPointer); }
            objects.Add(browser);
            Marshal.ThrowExceptionForHR(browser.QueryActiveShellView(out var view));
            objects.Add(view);
            var dispatchId = new Guid("00020400-0000-0000-C000-000000000046");
            Marshal.ThrowExceptionForHR(view.GetItemObject(0, ref dispatchId, out var folder)); // SVGIO_BACKGROUND
            objects.Add(folder);
            dynamic application = ((dynamic)folder).Application;
            objects.Add(application);
            // This interface lives inside Explorer. New process ownership and Windows
            // job lifetime come from Explorer rather than the Codex tool process.
            cancellationToken.ThrowIfCancellationRequested();
            application.ShellExecute(executablePath, arguments, Path.GetDirectoryName(executablePath)!, "open", 1);
        }
        finally
        {
            // Release each RCW once; different interfaces may share the same RCW identity.
            foreach (var instance in objects.Distinct(ReferenceEqualityComparer.Instance).Reverse())
                if (Marshal.IsComObject(instance)) Marshal.ReleaseComObject(instance);
        }
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IComServiceProvider
    {
        [PreserveSig] int QueryService(ref Guid service, ref Guid interfaceId, out IntPtr result);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        [PreserveSig] int GetWindow(out IntPtr window);
        [PreserveSig] int ContextSensitiveHelp(bool enterMode);
        [PreserveSig] int InsertMenusSB(IntPtr menu, IntPtr widths);
        [PreserveSig] int SetMenuSB(IntPtr menu, IntPtr oleMenu, IntPtr activeObject);
        [PreserveSig] int RemoveMenusSB(IntPtr menu);
        [PreserveSig] int SetStatusTextSB(IntPtr text);
        [PreserveSig] int EnableModelessSB(bool enable);
        [PreserveSig] int TranslateAcceleratorSB(IntPtr message, ushort id);
        [PreserveSig] int BrowseObject(IntPtr item, uint flags);
        [PreserveSig] int GetViewStateStream(uint mode, out IntPtr stream);
        [PreserveSig] int GetControlWindow(uint id, out IntPtr window);
        [PreserveSig] int SendControlMsg(uint id, uint message, IntPtr wParam, IntPtr lParam, out IntPtr result);
        [PreserveSig] int QueryActiveShellView(out IShellView view);
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        [PreserveSig] int GetWindow(out IntPtr window);
        [PreserveSig] int ContextSensitiveHelp(bool enterMode);
        [PreserveSig] int TranslateAccelerator(IntPtr message);
        [PreserveSig] int EnableModeless(bool enable);
        [PreserveSig] int UIActivate(uint state);
        [PreserveSig] int Refresh();
        [PreserveSig] int CreateViewWindow(IntPtr previous, IntPtr settings, IntPtr browser, IntPtr bounds, out IntPtr window);
        [PreserveSig] int DestroyViewWindow();
        [PreserveSig] int GetCurrentInfo(IntPtr settings);
        [PreserveSig] int AddPropertySheetPages(uint reserved, IntPtr callback, IntPtr parameter);
        [PreserveSig] int SaveViewState();
        [PreserveSig] int SelectItem(IntPtr item, uint flags);
        [PreserveSig] int GetItemObject(uint item, ref Guid interfaceId, [MarshalAs(UnmanagedType.IDispatch)] out object result);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
