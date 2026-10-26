using System.Text.Json;

namespace CodexAccountMonitor.Infrastructure;

public sealed record ResetCreditOperation(
    Guid AccountId,
    string IdempotencyKey,
    DateTimeOffset CreatedAtUtc,
    bool Completed,
    string? Outcome);

internal sealed record ResetOperationDocument(int SchemaVersion, IReadOnlyList<ResetCreditOperation> Operations);

public sealed class ResetOperationRepository
{
    private readonly AppDataPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public ResetOperationRepository(AppDataPaths paths)
    {
        _paths = paths;
    }

    public async Task<ResetCreditOperation> GetOrCreatePendingAsync(
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var operations = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            var pending = operations
                .Where(operation => operation.AccountId == accountId && !operation.Completed)
                .OrderByDescending(static operation => operation.CreatedAtUtc)
                .FirstOrDefault();
            if (pending is not null)
            {
                return pending;
            }

            var created = new ResetCreditOperation(
                accountId,
                Guid.NewGuid().ToString(),
                DateTimeOffset.UtcNow,
                false,
                null);
            operations.Add(created);
            await SaveCoreAsync(operations, cancellationToken).ConfigureAwait(false);
            return created;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ResetCreditOperation?> FindPendingAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return (await LoadCoreAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(operation => operation.AccountId == accountId && !operation.Completed);
        }
        finally { _gate.Release(); }
    }

    public async Task<ResetCreditOperation?> FindLatestAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return (await LoadCoreAsync(cancellationToken).ConfigureAwait(false))
                .Where(operation => operation.AccountId == accountId)
                .OrderByDescending(static operation => operation.CreatedAtUtc).FirstOrDefault();
        }
        finally { _gate.Release(); }
    }

    public async Task CompleteAsync(
        ResetCreditOperation operation,
        string outcome,
        CancellationToken cancellationToken = default)
    {
        // Unknown responses are unresolved operations, not permission to create
        // a different idempotency key on the next attempt.
        if (outcome is not ("reset" or "alreadyRedeemed" or "noCredit" or "nothingToReset"))
        {
            return;
        }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var operations = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            var index = operations.FindIndex(value =>
                value.AccountId == operation.AccountId
                && string.Equals(value.IdempotencyKey, operation.IdempotencyKey, StringComparison.Ordinal));
            var completed = operation with { Completed = true, Outcome = outcome };
            if (index >= 0)
            {
                operations[index] = completed;
            }
            else
            {
                operations.Add(completed);
            }

            // Keep a small audit trail without retaining unbounded local state.
            var pending = operations.Where(static value => !value.Completed);
            operations = pending.Concat(operations.Where(static value => value.Completed)
                .OrderByDescending(static value => value.CreatedAtUtc)
                .Take(100))
                .ToList();
            await SaveCoreAsync(operations, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<ResetCreditOperation>> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.ResetOperationsFile))
        {
            return new List<ResetCreditOperation>();
        }

        try
        {
            await using var stream = File.OpenRead(_paths.ResetOperationsFile);
            var document = await JsonSerializer.DeserializeAsync<ResetOperationDocument>(stream, _json, cancellationToken)
                .ConfigureAwait(false);
            if (document is null || document.SchemaVersion != 1 || document.Operations is null
                || document.Operations.Any(static operation => operation is null
                    || operation.AccountId == Guid.Empty || string.IsNullOrWhiteSpace(operation.IdempotencyKey)))
            {
                throw new InvalidDataException("The reset operation journal is damaged. Existing reset keys must be recovered before another credit can be used.");
            }
            // Earlier versions could mark an unknown response completed. Treat
            // such records as unresolved when upgrading, retaining their key.
            return document.Operations.Select(static operation => operation.Completed
                && operation.Outcome is not ("reset" or "alreadyRedeemed" or "noCredit" or "nothingToReset")
                    ? operation with { Completed = false }
                    : operation).ToList();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The reset operation journal is damaged. Existing reset keys must be recovered before another credit can be used.", exception);
        }
    }

    private async Task SaveCoreAsync(List<ResetCreditOperation> operations, CancellationToken cancellationToken)
    {
        _paths.EnsureBaseDirectories();
        var temporary = _paths.ResetOperationsFile + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write,
                         FileShare.None, 4096, FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                new ResetOperationDocument(1, operations),
                _json,
                cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, _paths.ResetOperationsFile, true);
    }
}
