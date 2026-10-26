using System.Text.Json;

namespace CodexAccountMonitor.Infrastructure;

internal sealed record PendingAccountDeletionDocument(
    int SchemaVersion,
    IReadOnlyList<Guid> AccountIds);

public sealed class PendingAccountDeletionRepository
{
    private readonly AppDataPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public PendingAccountDeletionRepository(AppDataPaths paths)
    {
        _paths = paths;
    }

    public async Task<IReadOnlyList<Guid>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task MarkAsync(Guid accountId, CancellationToken cancellationToken = default) =>
        UpdateAsync(accountId, add: true, cancellationToken);

    public Task CompleteAsync(Guid accountId, CancellationToken cancellationToken = default) =>
        UpdateAsync(accountId, add: false, cancellationToken);

    private async Task UpdateAsync(Guid accountId, bool add, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var ids = (await LoadCoreAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
            if (add ? !ids.Add(accountId) : !ids.Remove(accountId))
            {
                return;
            }

            await SaveCoreAsync(ids, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<Guid>> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.PendingDeletionsFile))
        {
            return Array.Empty<Guid>();
        }

        try
        {
            await using var stream = File.OpenRead(_paths.PendingDeletionsFile);
            var document = await JsonSerializer.DeserializeAsync<PendingAccountDeletionDocument>(
                    stream,
                    _json,
                    cancellationToken)
                .ConfigureAwait(false);
            return document?.AccountIds.Distinct().ToArray() ?? Array.Empty<Guid>();
        }
        catch (JsonException)
        {
            return Array.Empty<Guid>();
        }
    }

    private async Task SaveCoreAsync(IEnumerable<Guid> accountIds, CancellationToken cancellationToken)
    {
        _paths.EnsureBaseDirectories();
        var temporary = _paths.PendingDeletionsFile + ".tmp";
        await using (var stream = new FileStream(
                         temporary,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         4096,
                         FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(
                    stream,
                    new PendingAccountDeletionDocument(1, accountIds.Order().ToArray()),
                    _json,
                    cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, _paths.PendingDeletionsFile, true);
    }
}
