using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using CodexAccountMonitor.Core.Models;
using Microsoft.Win32.SafeHandles;

namespace CodexAccountMonitor.Infrastructure.AppServer;

public sealed record CodexAccountIdentity(string Type, string? Email, string? PlanType);

public sealed record LoginPrompt(
    string Type,
    string? LoginId,
    Uri? AuthorizationUri,
    Uri? VerificationUri,
    string? UserCode);

public sealed record LoginCompletion(string? LoginId, bool Success, string? SafeErrorCode);

public sealed class AppServerRpcException : Exception
{
    public AppServerRpcException(int code, bool isAuthenticationFailure, bool isRateLimited)
        : base("Codex App Server request failed.")
    {
        Code = code;
        IsAuthenticationFailure = isAuthenticationFailure;
        IsRateLimited = isRateLimited;
    }

    public int Code { get; }
    public bool IsAuthenticationFailure { get; }
    public bool IsRateLimited { get; }
}

public sealed class AppServerNotificationEventArgs : EventArgs
{
    public AppServerNotificationEventArgs(string method, JsonElement parameters)
    {
        Method = method;
        Parameters = parameters;
    }

    public string Method { get; }
    public JsonElement Parameters { get; }
}

public interface IAccountAppServerClient : IAsyncDisposable
{
    Task<CodexAccountIdentity?> ReadAccountAsync(bool refreshToken, CancellationToken cancellationToken = default);
    Task<RawRateLimitsResponse> ReadRateLimitsAsync(CancellationToken cancellationToken = default);
    Task<LoginPrompt> StartChatGptLoginAsync(bool deviceCode, CancellationToken cancellationToken = default);
    Task<LoginCompletion> WaitForLoginCompletionAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
    Task<string> ConsumeResetCreditAsync(string idempotencyKey, string? creditId = null, CancellationToken cancellationToken = default);
}

public sealed class CodexAppServerClient : IAccountAppServerClient
{
    private readonly Process _process;
    private readonly KillOnCloseJob _processJob;
    private readonly SafeJsonLogger _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationTokenSource _sendLifetime = new();
    private readonly object _terminalGate = new();
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private readonly TaskCompletionSource<LoginCompletion> _loginCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _readLoop;
    private Task? _errorDrain;
    private TaskCompletionSource<object?>? _disposeCompletion;
    private long _nextId;
    private bool _disposed;
    private bool _terminationStarted;
    private Exception? _terminalFailure;
    private int _hasRunningTurn;

    private CodexAppServerClient(Process process, KillOnCloseJob processJob, SafeJsonLogger logger)
    {
        _process = process;
        _processJob = processJob;
        _logger = logger;
    }

    public event EventHandler<AppServerNotificationEventArgs>? NotificationReceived;
    public event EventHandler? Terminated;

    public bool HasRunningTurn => Volatile.Read(ref _hasRunningTurn) != 0;

    public bool IsHealthy
    {
        get
        {
            lock (_terminalGate)
            {
                return !_disposed
                       && !_terminationStarted
                       && _terminalFailure is null
                       && !_process.HasExited;
            }
        }
    }

    public static async Task<CodexAppServerClient> StartAsync(
        string codexExecutable,
        string codexHome,
        SafeJsonLogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codexExecutable);
        ArgumentException.ThrowIfNullOrWhiteSpace(codexHome);

        var startInfo = new ProcessStartInfo
        {
            FileName = codexExecutable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Directory.GetParent(codexHome)?.FullName ?? codexHome
        };
        startInfo.ArgumentList.Add("app-server");
        startInfo.ArgumentList.Add("--listen");
        startInfo.ArgumentList.Add("stdio://");

        AppServerLaunchEnvironment.Configure(startInfo.Environment, codexHome);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            throw new InvalidOperationException("Codex App Server could not be started.");
        }

        KillOnCloseJob processJob;
        try
        {
            processJob = KillOnCloseJob.CreateAndAssign(process);
        }
        catch
        {
            StopUnownedProcess(process);
            throw;
        }

        var client = new CodexAppServerClient(process, processJob, logger);
        process.Exited += client.ProcessOnExited;
        client._readLoop = client.ReadLoopAsync(client._lifetime.Token);
        client._errorDrain = client.DrainStandardErrorAsync(client._lifetime.Token);
        if (process.HasExited)
        {
            // The stdout pipe can still contain a final response after Exited is raised.
            // Stop accepting new work, but let the read loop drain it before failing pending RPCs.
            client.BeginTermination();
        }

        try
        {
            var initializeResult = await client.CallAsync(
                "initialize",
                new
                {
                    clientInfo = new
                    {
                        name = "codex_account_monitor",
                        title = "Codex Account Monitor",
                        version = typeof(CodexAppServerClient).Assembly.GetName().Version?.ToString() ?? "1.0.0"
                    },
                    capabilities = new { experimentalApi = true }
                },
                cancellationToken).ConfigureAwait(false);
            if (!initializeResult.TryGetProperty("codexHome", out var reportedHome)
                || reportedHome.ValueKind != JsonValueKind.String
                || !PathsEqual(reportedHome.GetString(), codexHome))
            {
                throw new InvalidOperationException("Codex App Server started with an unexpected profile directory.");
            }
            await client.NotifyAsync("initialized", new { }, cancellationToken).ConfigureAwait(false);
            logger.Info("app_server_started", new Dictionary<string, object?> { ["processId"] = process.Id });
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    // No refresh token is transferred: the desktop owns its login lifecycle.
    public async Task UseExternalAccessTokenAsync(string accessToken, string accountId, CancellationToken cancellationToken = default)
    {
        await CallAsync("account/login/start", new
        {
            type = "chatgptAuthTokens", accessToken, chatgptAccountId = accountId
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CodexAccountIdentity?> ReadAccountAsync(
        bool refreshToken,
        CancellationToken cancellationToken = default)
    {
        var result = await CallAsync(
            "account/read",
            new { refreshToken },
            cancellationToken).ConfigureAwait(false);

        if (!result.TryGetProperty("account", out var account)
            || account.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return new CodexAccountIdentity(
            ReadString(account, "type") ?? "unknown",
            ReadString(account, "email"),
            ReadString(account, "planType"));
    }

    public async Task<RawRateLimitsResponse> ReadRateLimitsAsync(CancellationToken cancellationToken = default)
    {
        var result = await CallAsync("account/rateLimits/read", null, cancellationToken).ConfigureAwait(false);
        var buckets = new List<RawRateLimitBucket>();
        RawRateLimitBucket? defaultBucket = null;

        // Current App Servers return both the backward-compatible account-wide
        // view and a map of named/model-specific meters. Preserve both: the
        // account-wide value must not disappear merely because the map is present.
        if (result.TryGetProperty("rateLimits", out var single)
            && single.ValueKind == JsonValueKind.Object)
        {
            defaultBucket = ParseBucket(single, ReadString(single, "limitId") ?? "codex");
        }

        if (result.TryGetProperty("rateLimitsByLimitId", out var multi)
            && multi.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in multi.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object)
                {
                    buckets.Add(ParseBucket(property.Value, property.Name));
                }
            }
        }

        int? resetCredits = null;
        if (result.TryGetProperty("rateLimitResetCredits", out var credits)
            && credits.ValueKind == JsonValueKind.Object
            && credits.TryGetProperty("availableCount", out var count)
            && count.ValueKind == JsonValueKind.Number
            && count.TryGetInt32(out var value))
        {
            resetCredits = value;
        }

        var reached = defaultBucket?.RateLimitReachedType
                      ?? buckets
                          .Select(static bucket => bucket.RateLimitReachedType)
                          .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
        return new RawRateLimitsResponse(buckets, resetCredits, reached, defaultBucket);
    }

    public async Task<LoginPrompt> StartChatGptLoginAsync(
        bool deviceCode,
        CancellationToken cancellationToken = default)
    {
        var result = deviceCode
            ? await CallAsync(
                "account/login/start",
                new { type = "chatgptDeviceCode" },
                cancellationToken).ConfigureAwait(false)
            : await CallAsync(
                "account/login/start",
                new { type = "chatgpt", useHostedLoginSuccessPage = true, appBrand = "codex" },
                cancellationToken).ConfigureAwait(false);

        return new LoginPrompt(
            ReadString(result, "type") ?? (deviceCode ? "chatgptDeviceCode" : "chatgpt"),
            ReadString(result, "loginId"),
            TryUri(ReadString(result, "authUrl")),
            TryUri(ReadString(result, "verificationUrl")),
            ReadString(result, "userCode"));
    }

    public async Task<LoginCompletion> WaitForLoginCompletionAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            return await _loginCompletion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            return new LoginCompletion(null, false, "login_timeout");
        }
    }

    public async Task<string> ConsumeResetCreditAsync(
        string idempotencyKey,
        string? creditId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        object parameters = string.IsNullOrWhiteSpace(creditId)
            ? new { idempotencyKey }
            : new { idempotencyKey, creditId };
        var result = await CallAsync(
            "account/rateLimitResetCredit/consume",
            parameters,
            cancellationToken).ConfigureAwait(false);
        return ReadString(result, "outcome") ?? "unknown";
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default) =>
        _ = await CallAsync("account/logout", null, cancellationToken).ConfigureAwait(false);

    private async Task<JsonElement> CallAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_terminalGate)
        {
            ThrowIfUnavailable();
            if (!_pending.TryAdd(id, completion))
            {
                throw new InvalidOperationException("Duplicate App Server request identifier.");
            }
        }

        try
        {
            await SendAsync(BuildMessage(method, id, parameters), cancellationToken).ConfigureAwait(false);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            return await completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private Task NotifyAsync(string method, object? parameters, CancellationToken cancellationToken) =>
        SendAsync(BuildMessage(method, null, parameters), cancellationToken);

    private Dictionary<string, object?> BuildMessage(string method, long? id, object? parameters)
    {
        var message = new Dictionary<string, object?> { ["method"] = method };
        if (id is not null)
        {
            message["id"] = id.Value;
        }

        if (parameters is not null)
        {
            message["params"] = parameters;
        }

        return message;
    }

    private async Task SendAsync(object message, CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(message, _json);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _sendLifetime.Token);
        try
        {
            await _writeGate.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_sendLifetime.IsCancellationRequested)
        {
            ThrowIfUnavailable();
            throw;
        }

        try
        {
            ThrowIfUnavailable();
            try
            {
                await _process.StandardInput.WriteLineAsync(line.AsMemory(), linked.Token).ConfigureAwait(false);
                await _process.StandardInput.FlushAsync(linked.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                MarkTerminated(new IOException("Codex App Server input stream is unavailable."));
                throw;
            }
            catch (OperationCanceledException) when (_sendLifetime.IsCancellationRequested)
            {
                ThrowIfUnavailable();
                throw;
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await _process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (line.Length > 4 * 1024 * 1024)
                {
                    throw new InvalidDataException("Codex App Server emitted an oversized protocol message.");
                }

                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("id", out var idElement)
                    && idElement.ValueKind == JsonValueKind.Number
                    && idElement.TryGetInt64(out var id)
                    && _pending.TryGetValue(id, out var completion))
                {
                    if (root.TryGetProperty("result", out var result))
                    {
                        completion.TrySetResult(result.Clone());
                    }
                    else if (root.TryGetProperty("error", out var error))
                    {
                        completion.TrySetException(ParseRpcError(error));
                    }

                    continue;
                }

                if (root.TryGetProperty("method", out var methodElement))
                {
                    var method = methodElement.GetString() ?? string.Empty;
                    var parameters = root.TryGetProperty("params", out var value)
                        ? value.Clone()
                        : JsonSerializer.SerializeToElement(new { });
                    HandleNotification(method, parameters);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.Error("app_server_read_failed", exception);
        }
        finally
        {
            MarkTerminated(new IOException("Codex App Server stopped before responding."));
        }
    }

    private async Task DrainStandardErrorAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
                // Never copy App Server stderr into logs; it may contain sensitive context.
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void HandleNotification(string method, JsonElement parameters)
    {
        Delegate[]? handlers;
        lock (_terminalGate)
        {
            // Process.Exited can race the final buffered stdout lines. Responses are still
            // completed by the read loop, but buffered notifications must not mutate state.
            if (_disposed || _terminationStarted || _terminalFailure is not null)
            {
                return;
            }

            if (string.Equals(method, "turn/started", StringComparison.Ordinal))
            {
                Interlocked.Exchange(ref _hasRunningTurn, 1);
            }
            else if (method is "turn/completed" or "turn/failed" or "turn/cancelled")
            {
                Interlocked.Exchange(ref _hasRunningTurn, 0);
            }
            else if (string.Equals(method, "account/login/completed", StringComparison.Ordinal))
            {
                var success = parameters.TryGetProperty("success", out var successElement)
                              && successElement.ValueKind == JsonValueKind.True;
                _loginCompletion.TrySetResult(new LoginCompletion(
                    ReadString(parameters, "loginId"),
                    success,
                    success ? null : "login_failed"));
            }

            handlers = NotificationReceived?.GetInvocationList();
        }

        if (handlers is not null)
        {
            var eventArgs = new AppServerNotificationEventArgs(method, parameters);
            ThreadPool.QueueUserWorkItem(
                static state => state.Client.DispatchNotification(state.Handlers, state.EventArgs),
                (Client: this, Handlers: handlers, EventArgs: eventArgs),
                preferLocal: false);
        }
    }

    private void DispatchNotification(
        Delegate[] handlers,
        AppServerNotificationEventArgs eventArgs)
    {
        foreach (var handler in handlers)
        {
            lock (_terminalGate)
            {
                if (_disposed || _terminationStarted || _terminalFailure is not null)
                {
                    return;
                }
            }

            try
            {
                ((EventHandler<AppServerNotificationEventArgs>)handler)(this, eventArgs);
            }
            catch
            {
                // Protocol observers cannot terminate the dispatcher.
            }
        }
    }

    private void ProcessOnExited(object? sender, EventArgs e)
    {
        // Exited can precede delivery of data already buffered in redirected stdout.
        // The read loop owns final terminal marking after it observes EOF.
        BeginTermination();
    }

    private void BeginTermination()
    {
        lock (_terminalGate)
        {
            if (!_terminationStarted)
            {
                _terminationStarted = true;
                Interlocked.Exchange(ref _hasRunningTurn, 0);
            }
        }

        // Cancellation is idempotent. Every terminal observer performs it so a
        // thread pre-empted just after publishing _terminationStarted cannot
        // temporarily leave queued writers asleep.
        try
        {
            _sendLifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The send lifetime intentionally outlives disposal waiters, but remain fail-soft.
        }
    }

    private void MarkTerminated(Exception failure)
    {
        BeginTermination();

        TaskCompletionSource<JsonElement>[] pending;
        bool notify;
        lock (_terminalGate)
        {
            if (_terminalFailure is not null)
            {
                return;
            }

            _terminalFailure = failure;
            pending = _pending.Values.ToArray();
            notify = !_disposed;
        }

        foreach (var completion in pending)
        {
            completion.TrySetException(failure);
        }
        _loginCompletion.TrySetResult(new LoginCompletion(null, false, "app_server_terminated"));

        if (!notify)
        {
            return;
        }

        var handlers = Terminated?.GetInvocationList();
        if (handlers is null)
        {
            return;
        }

        ThreadPool.QueueUserWorkItem(
            static state => state.Client.DispatchTerminated(state.Handlers),
            (Client: this, Handlers: handlers),
            preferLocal: false);
    }

    private void DispatchTerminated(Delegate[] handlers)
    {
        foreach (var handler in handlers)
        {
            try
            {
                ((EventHandler)handler)(this, EventArgs.Empty);
            }
            catch
            {
            }
        }
    }

    private static AppServerRpcException ParseRpcError(JsonElement error)
    {
        var code = error.TryGetProperty("code", out var codeElement) && codeElement.ValueKind == JsonValueKind.Number
                   && codeElement.TryGetInt32(out var parsedCode)
            ? parsedCode
            : -1;
        var message = ReadString(error, "message") ?? string.Empty;
        var lower = message.ToLowerInvariant();
        var authentication = code is 401 or 403
                             || lower.Contains("unauthorized", StringComparison.Ordinal)
                             || lower.Contains("authentication", StringComparison.Ordinal);
        var rateLimited = code == 429 || lower.Contains("rate limit", StringComparison.Ordinal);
        return new AppServerRpcException(code, authentication, rateLimited);
    }

    private static RawRateLimitBucket ParseBucket(JsonElement element, string fallbackId) => new(
        ReadString(element, "limitId") ?? fallbackId,
        ReadString(element, "limitName"),
        ParseWindow(element, "primary"),
        ParseWindow(element, "secondary"),
        ReadString(element, "rateLimitReachedType"));

    private static RawQuotaWindow? ParseWindow(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var window)
            || window.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        decimal? used = window.TryGetProperty("usedPercent", out var usedElement)
                        && usedElement.ValueKind == JsonValueKind.Number
                        && usedElement.TryGetDecimal(out var usedValue)
            ? usedValue
            : null;
        int? duration = window.TryGetProperty("windowDurationMins", out var durationElement)
                        && durationElement.ValueKind == JsonValueKind.Number
                        && durationElement.TryGetInt32(out var durationValue)
            ? durationValue
            : null;
        long? resetsAt = window.TryGetProperty("resetsAt", out var resetElement)
                         && resetElement.ValueKind == JsonValueKind.Number
                         && resetElement.TryGetInt64(out var resetValue)
            ? resetValue
            : null;
        return new RawQuotaWindow(used, duration, resetsAt);
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static Uri? TryUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;

    private static bool PathsEqual(string? first, string second)
    {
        if (string.IsNullOrWhiteSpace(first))
        {
            return false;
        }

        var normalizedFirst = Path.TrimEndingDirectorySeparator(Path.GetFullPath(first));
        var normalizedSecond = Path.TrimEndingDirectorySeparator(Path.GetFullPath(second));
        return string.Equals(normalizedFirst, normalizedSecond, StringComparison.OrdinalIgnoreCase)
            || WindowsDirectoryIdentity.AreSame(normalizedFirst, normalizedSecond);
    }

    private void ThrowIfUnavailable()
    {
        lock (_terminalGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_terminationStarted || _terminalFailure is not null)
            {
                throw new IOException("Codex App Server is no longer available.", _terminalFailure);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource<object?> completion;
        lock (_terminalGate)
        {
            if (_disposeCompletion is not null)
            {
                return new ValueTask(_disposeCompletion.Task);
            }

            completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeCompletion = completion;
            _disposed = true;
        }

        BeginTermination();
        MarkTerminated(new ObjectDisposedException(nameof(CodexAppServerClient)));
        _ = CompleteDisposeAsync(completion);

        return new ValueTask(completion.Task);
    }

    private async Task CompleteDisposeAsync(TaskCompletionSource<object?> completion)
    {
        try
        {
            await DisposeCoreAsync().ConfigureAwait(false);
            completion.TrySetResult(null);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            TryCloseStandardInput();

            if (!await WaitForExitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false))
            {
                // Closing the job handle is an exact-owned-process emergency stop and
                // also kills any children the App Server placed in the inherited job.
                _processJob.Dispose();
                if (!await WaitForExitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false))
                {
                    TryKillProcessTree();
                    _ = await WaitForExitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _lifetime.Cancel();
            if (_readLoop is not null)
            {
                await IgnoreFailure(_readLoop).ConfigureAwait(false);
            }

            if (_errorDrain is not null)
            {
                await IgnoreFailure(_errorDrain).ConfigureAwait(false);
            }

            _process.Exited -= ProcessOnExited;
            _processJob.Dispose();
            try
            {
                _process.Dispose();
            }
            catch
            {
            }

            _lifetime.Dispose();
            // Do not dispose _writeGate or _sendLifetime: a caller that was already
            // queued when shutdown began may still be unwinding through SendAsync.
            _logger.Info("app_server_stopped");
        }
    }

    private void TryCloseStandardInput()
    {
        try
        {
            _process.StandardInput.Close();
        }
        catch
        {
        }
    }

    private async Task<bool> WaitForExitAsync(TimeSpan timeout)
    {
        using var timeoutSource = new CancellationTokenSource(timeout);
        try
        {
            await _process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            return false;
        }
        catch
        {
            return true;
        }
    }

    private void TryKillProcessTree()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private static void StopUnownedProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2_000);
            }
        }
        catch
        {
        }
        finally
        {
            try
            {
                process.Dispose();
            }
            catch
            {
            }
        }
    }

    private sealed class KillOnCloseJob : SafeHandleZeroOrMinusOneIsInvalid
    {
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;
        private const int JobObjectExtendedLimitInformationClass = 9;

        private KillOnCloseJob()
            : base(ownsHandle: true)
        {
        }

        public static KillOnCloseJob CreateAndAssign(Process process)
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                job.Dispose();
                throw new Win32Exception(error, "Could not create the App Server process job.");
            }

            try
            {
                var limits = new JobObjectExtendedLimitInformation
                {
                    BasicLimitInformation = new JobObjectBasicLimitInformation
                    {
                        LimitFlags = JobObjectLimitKillOnJobClose
                    }
                };
                if (!SetInformationJobObject(
                        job,
                        JobObjectExtendedLimitInformationClass,
                        ref limits,
                        (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Could not configure the App Server process job.");
                }

                if (!AssignProcessToJobObject(job, process.Handle))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Could not attach the App Server to its process job.");
                }

                return job;
            }
            catch
            {
                job.Dispose();
                throw;
            }
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern KillOnCloseJob CreateJobObject(IntPtr jobAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(
            KillOnCloseJob job,
            int informationClass,
            ref JobObjectExtendedLimitInformation jobObjectInformation,
            uint jobObjectInformationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(KillOnCloseJob job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }
    }

    private static async Task IgnoreFailure(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
        }
    }
}
