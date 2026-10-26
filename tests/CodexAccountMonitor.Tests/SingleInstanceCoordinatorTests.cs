using System.Security.Principal;
using CodexAccountMonitor.Infrastructure;

namespace CodexAccountMonitor.Tests;

public sealed class SingleInstanceCoordinatorTests
{
    [Fact]
    public void FreshInstanceOwnsTheMutexUntilDisposed()
    {
        var suffix = Guid.NewGuid().ToString("N");
        using var primary = new SingleInstanceCoordinator(suffix);
        Assert.True(primary.IsPrimary);
        Assert.False(OnAnotherThread(() =>
        {
            using var candidate = new SingleInstanceCoordinator(suffix);
            return candidate.IsPrimary;
        }));
    }

    [Fact]
    public void RetainedUnownedMutexDoesNotHideTheNewPrimary()
    {
        var suffix = Guid.NewGuid().ToString("N");
        using var retainedHandle = new Mutex(false, MutexName(suffix));
        using var primary = new SingleInstanceCoordinator(suffix);
        Assert.True(primary.IsPrimary);
        Assert.False(OnAnotherThread(() =>
        {
            using var candidate = new SingleInstanceCoordinator(suffix);
            return candidate.IsPrimary;
        }));
    }

    [Fact]
    public void AbandonedPrimaryIsReplacedWhileItsHandleStillExists()
    {
        var suffix = Guid.NewGuid().ToString("N");
        using var retainedHandle = new Mutex(false, MutexName(suffix));
        var acquired = OnAnotherThread(() => retainedHandle.WaitOne(0));
        Assert.True(acquired); // The owning thread exits without releasing it.

        using var replacement = new SingleInstanceCoordinator(suffix);
        Assert.True(replacement.IsPrimary);
        Assert.False(OnAnotherThread(() =>
        {
            using var candidate = new SingleInstanceCoordinator(suffix);
            return candidate.IsPrimary;
        }));
    }

    [Fact]
    public void SecondaryCanActivateTheLivePrimaryWithoutTakingOwnership()
    {
        var suffix = Guid.NewGuid().ToString("N");
        using var primary = new SingleInstanceCoordinator(suffix);
        using var activation = EventWaitHandle.OpenExisting(ActivationName(suffix));
        Assert.False(OnAnotherThread(() =>
        {
            using var candidate = new SingleInstanceCoordinator(suffix);
            candidate.SignalPrimary();
            return candidate.IsPrimary;
        }));
        Assert.True(activation.WaitOne(TimeSpan.FromSeconds(1)));
        Assert.False(OnAnotherThread(() =>
        {
            using var candidate = new SingleInstanceCoordinator(suffix);
            return candidate.IsPrimary;
        }));
    }

    [Fact]
    public void ReleasedPrimaryIsReplacedEvenWhenAnotherHandleKeepsTheMutexAlive()
    {
        var suffix = Guid.NewGuid().ToString("N");
        using var retainedHandle = new Mutex(false, MutexName(suffix));
        using (var previous = new SingleInstanceCoordinator(suffix))
            Assert.True(previous.IsPrimary);

        Assert.True(OnAnotherThread(() =>
        {
            using var replacement = new SingleInstanceCoordinator(suffix);
            return replacement.IsPrimary;
        }));
    }

    private static string UserSid => WindowsIdentity.GetCurrent().User?.Value?.Replace('-', '_') ?? Environment.UserName;
    private static string MutexName(string suffix) => $"Local\\CodexAccountMonitor_{UserSid}_{suffix}";
    private static string ActivationName(string suffix) => $"Local\\CodexAccountMonitor_Activate_{UserSid}_{suffix}";

    private static T OnAnotherThread<T>(Func<T> operation)
    {
        T? result = default;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { result = operation(); }
            catch (Exception exception) { failure = exception; }
        }) { IsBackground = true };
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "The isolated mutex operation did not finish.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return result!;
    }
}
