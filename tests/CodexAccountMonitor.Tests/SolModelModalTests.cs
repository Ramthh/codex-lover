using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using CodexAccountMonitor.Infrastructure;

namespace CodexAccountMonitor.Tests;

public sealed class SolModelModalTests
{
    [Fact]
    public Task NestedDialogScopesHideBothNativeWindowsAndRestoreAfterTheLastScope() => OnStaAsync(() =>
    {
        using var fixture = new Fixture();
        fixture.Model.ShowModel();
        fixture.Model.ShowBubble(new("Synthetic model", "Initial message", false));
        fixture.AssertVisible(model: true, bubble: true);

        var outer = fixture.Model.SuspendForDialog();
        fixture.AssertVisible(model: false, bubble: false);
        var inner = fixture.Model.SuspendForDialog();
        fixture.Model.ShowModel();
        fixture.Model.ShowBubble(new("Synthetic model", "Timer update while modal", false));
        fixture.AssertVisible(model: false, bubble: false);
        inner.Dispose();
        inner.Dispose();
        fixture.AssertVisible(model: false, bubble: false);
        outer.Dispose();
        fixture.AssertVisible(model: true, bubble: true);
    });

    [Fact]
    public Task HidingModelDuringSettingsDoesNotRestoreItWhenTheModalScopeEnds() => OnStaAsync(() =>
    {
        using var fixture = new Fixture();
        fixture.Model.ShowModel();
        var scope = fixture.Model.SuspendForDialog();
        fixture.Model.HideModel();
        fixture.Model.ShowBubble(new("Synthetic model", "Pending timer update", false));
        scope.Dispose();
        fixture.AssertVisible(model: false, bubble: false);
    });

    [Fact]
    public Task ShowingModelDuringAModalWaitsForItsScopeAndExpiredBubbleStaysHidden() => OnStaAsync(() =>
    {
        using var fixture = new Fixture();
        var scope = fixture.Model.SuspendForDialog();
        fixture.Model.ShowModel();
        fixture.Model.ShowBubble(new("Synthetic model", "Pending timer update", false));
        fixture.AssertVisible(model: false, bubble: false);
        fixture.Model.HideBubble();
        scope.Dispose();
        fixture.AssertVisible(model: true, bubble: false);
    });

    [Fact]
    public Task DisposingTheModelWhileSuppressedCannotResurrectEitherWindow() => OnStaAsync(() =>
    {
        using var fixture = new Fixture();
        fixture.Model.ShowModel();
        fixture.Model.ShowBubble(new("Synthetic model", "Initial message", false));
        var modelHandle = new WindowInteropHelper(fixture.Model).Handle;
        var bubbleHandle = new WindowInteropHelper(fixture.Bubble).Handle;
        var scope = fixture.Model.SuspendForDialog();
        fixture.Model.Dispose();
        scope.Dispose();
        scope.Dispose();
        fixture.Model.ShowModel();
        fixture.Model.ShowBubble(new("Synthetic model", "After shutdown", false));
        Assert.False(IsWindow(modelHandle));
        Assert.False(IsWindow(bubbleHandle));
    });

    [Fact]
    public Task ActualNestedModalWindowsKeepTheModelHiddenAndExceptionUnwindsItsScope() => OnStaAsync(() =>
    {
        using var fixture = new Fixture();
        fixture.Model.ShowModel();
        fixture.Model.ShowBubble(new("Synthetic model", "Initial message", false));
        var outer = HiddenDialog();
        Exception? modalError = null;
        outer.Loaded += (_, _) =>
        {
            try
            {
                fixture.AssertVisible(model: false, bubble: false);
                using var nestedScope = fixture.Model.SuspendForDialog();
                var inner = HiddenDialog();
                inner.Owner = outer;
                inner.Loaded += (_, _) =>
                {
                    try { fixture.AssertVisible(model: false, bubble: false); }
                    catch (Exception exception) { modalError = exception; }
                    finally { inner.Close(); }
                };
                inner.ShowDialog();
                fixture.AssertVisible(model: false, bubble: false);
            }
            catch (Exception exception) { modalError = exception; }
            finally { outer.Close(); }
        };
        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using var scope = fixture.Model.SuspendForDialog();
            outer.ShowDialog();
            fixture.AssertVisible(model: false, bubble: false);
            throw new InvalidOperationException("Synthetic modal failure");
        }));
        if (modalError is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(modalError).Throw();
        fixture.AssertVisible(model: true, bubble: true);
    });

    private static Window HiddenDialog() => new()
    {
        Width = 200, Height = 100, Opacity = 0, ShowActivated = false, ShowInTaskbar = false,
        WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000
    };

    private static Task OnStaAsync(Action test)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(() =>
            {
                try { test(); completion.TrySetResult(); }
                catch (Exception exception) { completion.TrySetException(exception); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true, Name = "Model modal visibility regression" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private sealed class Fixture : IDisposable
    {
        public SolModelWindow Model { get; } = new() { Opacity = 0, AnimationsEnabled = false };
        public Window Bubble { get; }
        public Fixture()
        {
            // Real HWND visibility is checked without drawing a pet over the user's desktop.
            Bubble = (Window)typeof(SolModelWindow).GetField("_bubble", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Model)!;
            Bubble.Opacity = 0;
        }
        public void AssertVisible(bool model, bool bubble)
        {
            Assert.Equal(model, Model.IsVisible);
            Assert.Equal(bubble, Bubble.IsVisible);
            Assert.Equal(model, NativeVisible(Model));
            Assert.Equal(bubble, NativeVisible(Bubble));
        }
        private static bool NativeVisible(Window window)
        {
            var handle = new WindowInteropHelper(window).Handle;
            return handle != IntPtr.Zero && IsWindowVisible(handle);
        }
        public void Dispose() => Model.Dispose();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);
}
