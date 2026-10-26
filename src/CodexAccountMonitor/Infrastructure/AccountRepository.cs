using System.Text.Json;
using System.Text.Json.Serialization;
using CodexAccountMonitor.Core.Models;

namespace CodexAccountMonitor.Infrastructure;

public sealed record PersistedAccount(AccountProfile Profile, AccountUsageState Usage);

internal sealed record AccountStoreDocument(int SchemaVersion, IReadOnlyList<PersistedAccount> Accounts);

public sealed class AccountRepository
{
    private readonly AppDataPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public AccountRepository(AppDataPaths paths)
    {
        _paths = paths;
    }

    public string? RecoveryMessage { get; private set; }

    public async Task<IReadOnlyList<PersistedAccount>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_paths.AccountsFile))
            {
                if (File.Exists(_paths.AccountsFile + ".bak"))
                {
                    var recovered = await ReadDocumentAsync(_paths.AccountsFile + ".bak", cancellationToken).ConfigureAwait(false);
                    RecoveryMessage = "Account metadata was recovered from the last valid backup because the main file was missing.";
                    return recovered;
                }
                return Array.Empty<PersistedAccount>();
            }

            RecoveryMessage = null;
            try
            {
                return await ReadDocumentAsync(_paths.AccountsFile, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException)
            {
                var backup = _paths.AccountsFile + ".bak";
                if (!File.Exists(backup))
                {
                    throw new InvalidDataException("Account metadata is damaged and no backup is available. The profile folders were preserved.", exception);
                }
                var recovered = await ReadDocumentAsync(backup, cancellationToken).ConfigureAwait(false);
                RecoveryMessage = "Account metadata was recovered from the last valid backup. Check your account list; the damaged file has been preserved.";
                return recovered;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(IEnumerable<PersistedAccount> accounts, CancellationToken cancellationToken = default)
    {
        var validated = Validate(accounts.ToArray());
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _paths.EnsureBaseDirectories();
            var temporaryFile = _paths.AccountsFile + ".tmp";
            await using (var stream = new FileStream(
                temporaryFile,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    new AccountStoreDocument(1, validated),
                    _json,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var backup = _paths.AccountsFile + ".bak";
            if (File.Exists(_paths.AccountsFile))
            {
                var primaryValid = true;
                try
                {
                    _ = await ReadDocumentAsync(_paths.AccountsFile, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is JsonException or InvalidDataException)
                {
                    primaryValid = false;
                }
                if (primaryValid)
                {
                    File.Replace(temporaryFile, _paths.AccountsFile, backup);
                }
                else
                {
                    File.Copy(_paths.AccountsFile, _paths.AccountsFile + ".damaged-" + Guid.NewGuid().ToString("N"));
                    File.Move(temporaryFile, _paths.AccountsFile, true);
                }
            }
            else
            {
                File.Move(temporaryFile, _paths.AccountsFile);
                File.Copy(_paths.AccountsFile, backup, true);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<PersistedAccount>> ReadDocumentAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var document = await JsonSerializer.DeserializeAsync<AccountStoreDocument>(stream, _json, cancellationToken)
            .ConfigureAwait(false);
        if (document is null || document.SchemaVersion != 1 || document.Accounts is null)
        {
            throw new InvalidDataException("Unsupported or damaged account metadata.");
        }
        return Validate(document.Accounts);
    }

    private static IReadOnlyList<PersistedAccount> Validate(IReadOnlyList<PersistedAccount> accounts)
    {
        if (accounts.Any(static account => account is null || account.Profile is null || account.Usage is null
            || account.Profile.Id == Guid.Empty || string.IsNullOrWhiteSpace(account.Profile.Alias)))
        {
            throw new InvalidDataException("Account metadata contains an invalid profile.");
        }
        if (accounts.Any(static account => account.Profile.Slot < 1))
        {
            throw new InvalidDataException("Account slots must be positive.");
        }

        if (accounts.GroupBy(static account => account.Profile.Slot).Any(static group => group.Count() > 1)
            || accounts.GroupBy(static account => account.Profile.Id).Any(static group => group.Count() > 1))
        {
            throw new InvalidDataException("Account slots and identifiers must be unique.");
        }

        return accounts.OrderBy(static account => account.Profile.Slot).ToArray();
    }
}
