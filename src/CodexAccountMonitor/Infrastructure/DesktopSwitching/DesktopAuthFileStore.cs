using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using CodexAccountMonitor.Core.Services;

namespace CodexAccountMonitor.Infrastructure.DesktopSwitching;

public sealed record DesktopAuthIdentity(string? EmailFingerprint, string AccountId, string? UserId);

/// <summary>Credentials kept only in memory; diagnostics must never serialize this object.</summary>
public sealed class DesktopAuthSnapshot
{
    private readonly byte[] _authBytes;
    internal DesktopAuthSnapshot(byte[] authBytes, DesktopAuthIdentity identity,
        string idToken, string accessToken, string refreshToken, DateTimeOffset expiresAt)
    {
        _authBytes = authBytes;
        Identity = identity;
        IdToken = idToken;
        AccessToken = accessToken;
        RefreshToken = refreshToken;
        AccessExpiresAt = expiresAt;
    }

    public DesktopAuthIdentity Identity { get; }
    [System.Text.Json.Serialization.JsonIgnore] public string IdToken { get; }
    [System.Text.Json.Serialization.JsonIgnore] public string AccessToken { get; }
    [System.Text.Json.Serialization.JsonIgnore] public string RefreshToken { get; }
    public DateTimeOffset AccessExpiresAt { get; }
    public byte[] CloneAuthBytes() => (byte[])_authBytes.Clone();
    public override string ToString() => "DesktopAuthSnapshot [credentials redacted]";
}

public sealed record DesktopSwitchReceipt(
    Guid SelectedProfileId,
    DesktopAuthIdentity Identity,
    DateTimeOffset CreatedUtc,
    bool IsPending,
    string IntendedAuthHash,
    IReadOnlyList<string>? AcceptedSourceHashes = null);

/// <summary>
/// Replaces only a selected local Codex home's auth.json. The caller owns profile refresh
/// locks and must stop the desktop and all its app servers before every mutation.
/// Recovery is intentionally a compare-and-restore operation: an externally changed auth
/// file is never overwritten. The previous bytes exist only in a current-user DPAPI journal.
/// </summary>
public sealed class DesktopAuthFileStore
{
    public const int MaximumAuthBytes = 256 * 1024;
    private const int MaximumJournalBytes = 1024 * 1024;
    private const int MaximumTokenCharacters = 64 * 1024;
    private readonly string _desktopHome;
    private readonly string _authPath;
    private readonly string _journalDirectory;
    private readonly string _pendingPath;
    private readonly string _receiptPath;
    private readonly SemaphoreSlim _transactionGate = new(1, 1);

    public DesktopAuthFileStore(string desktopHome, string journalDirectory)
    {
        _desktopHome = CanonicalLocalDirectory(desktopHome);
        _journalDirectory = CanonicalLocalDirectory(journalDirectory);
        _authPath = Path.Combine(_desktopHome, "auth.json");
        _pendingPath = Path.Combine(_journalDirectory, "desktop-switch.pending.dpapi");
        _receiptPath = Path.Combine(_journalDirectory, "desktop-switch.receipt.dpapi");
        if (IsWithin(_journalDirectory, _desktopHome) || IsWithin(_desktopHome, _journalDirectory))
            throw new InvalidOperationException("Desktop credentials and recovery files must use separate directories.");
        EnsureSafePath(_authPath);
        EnsureSafePath(_pendingPath);
        EnsureSafePath(_receiptPath);
    }

    public DesktopAuthSnapshot? ReadSnapshot(bool requireUnexpiredAccess = false) =>
        ReadSnapshotFromHome(_desktopHome, requireUnexpiredAccess);

    public static DesktopAuthSnapshot? ReadSnapshotFromHome(string home, bool requireUnexpiredAccess = true)
    {
        var path = Path.Combine(CanonicalLocalDirectory(home), "auth.json");
        EnsureSafePath(path);
        var bytes = ReadOptional(path, MaximumAuthBytes);
        return bytes is null ? null : ParseSnapshot(bytes, requireUnexpiredAccess);
    }

    /// <summary>Copies rotated credentials back only to the same verified, locked saved profile.</summary>
    public static void WriteProfileSnapshot(string home, DesktopAuthSnapshot snapshot, Func<bool> guard)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var path = Path.Combine(CanonicalLocalDirectory(home), "auth.json");
        var existing = ReadOptional(path, MaximumAuthBytes)
            ?? throw new InvalidOperationException("The saved profile credentials are missing; sign in again.");
        var previous = ParseSnapshot(existing, requireUnexpiredAccess: false);
        var replacement = snapshot.CloneAuthBytes();
        var verified = ParseSnapshot(replacement, requireUnexpiredAccess: false);
        if (previous.Identity != verified.Identity)
            throw new InvalidOperationException("The desktop credentials do not match this saved account.");
        var originalHash = Hash(existing);
        WriteAtomic(path, replacement, () =>
        {
            EnsureClosed(guard);
            if (Hash(ReadOptional(path, MaximumAuthBytes)) != originalHash)
                throw new InvalidOperationException("The saved account changed while its credentials were being synchronized.");
        });
    }

    /// <summary>Parses locally verified credentials; this does not validate a JWT signature or log in.</summary>
    public static DesktopAuthSnapshot ParseSnapshot(ReadOnlySpan<byte> authBytes, bool requireUnexpiredAccess = true)
    {
        if (authBytes.Length == 0 || authBytes.Length > MaximumAuthBytes)
            throw new InvalidDataException("The account credential file has an invalid size.");
        try
        {
            using var document = JsonDocument.Parse(authBytes.ToArray(), new JsonDocumentOptions { MaxDepth = 24 });
            var root = document.RootElement;
            RequireObject(root);
            RejectDuplicateProperties(root);
            var mode = OptionalString(root, "auth_mode");
            if (mode is not null && mode != "chatgpt")
                throw new InvalidDataException("Only saved ChatGPT account credentials can be switched.");
            if (root.TryGetProperty("OPENAI_API_KEY", out var apiKey)
                && apiKey.ValueKind != JsonValueKind.Null
                && (apiKey.ValueKind != JsonValueKind.String || !string.IsNullOrWhiteSpace(apiKey.GetString())))
                throw new InvalidDataException("API key credentials cannot be used for account switching.");
            if (!root.TryGetProperty("tokens", out var tokens))
                throw new InvalidDataException("The saved account has no ChatGPT credentials.");
            RequireObject(tokens);
            var idToken = RequiredString(tokens, "id_token", MaximumTokenCharacters);
            var accessToken = RequiredString(tokens, "access_token", MaximumTokenCharacters);
            var refreshToken = RequiredString(tokens, "refresh_token", MaximumTokenCharacters);
            if (refreshToken.Any(char.IsWhiteSpace))
                throw new InvalidDataException("The saved refresh credential is malformed.");
            var accountId = RequiredString(tokens, "account_id", 512);
            using var idDocument = ParseJwt(idToken);
            using var accessDocument = ParseJwt(accessToken);
            var idClaims = idDocument.RootElement;
            var accessClaims = accessDocument.RootElement;
            var idAuth = OptionalObject(idClaims, "https://api.openai.com/auth");
            var accessAuth = OptionalObject(accessClaims, "https://api.openai.com/auth");
            EnsureMatches(accountId, OptionalString(idAuth, "chatgpt_account_id"));
            EnsureMatches(accountId, OptionalString(accessAuth, "chatgpt_account_id"));

            // auth0 subjects and ChatGPT user ids are different namespaces. Compare each
            // namespace independently, then prefer the ChatGPT identifier for the receipt.
            var chatUser = MergeIdentity(OptionalString(idAuth, "chatgpt_user_id"), OptionalString(accessAuth, "chatgpt_user_id"));
            var user = MergeIdentity(OptionalString(idAuth, "user_id"), OptionalString(accessAuth, "user_id"));
            if (chatUser is not null && user is not null) EnsureMatches(chatUser, user);
            var subject = MergeIdentity(OptionalString(idClaims, "sub"), OptionalString(accessClaims, "sub"));
            var email = MergeEmail(OptionalString(idClaims, "email"), OptionalString(accessClaims, "email"));
            email = MergeEmail(email, OptionalString(OptionalObject(accessClaims, "https://api.openai.com/profile"), "email"));
            email = MergeEmail(email, OptionalString(OptionalObject(idClaims, "https://api.openai.com/profile"), "email"));
            if (email is not null && (!email.Contains('@') || email.Length > 1024))
                throw new InvalidDataException("The account's email claim is invalid.");
            if (email is null && chatUser is null && user is null && subject is null)
                throw new InvalidDataException("The credentials do not contain a verifiable account identity.");
            if (!accessClaims.TryGetProperty("exp", out var expiry) || !expiry.TryGetInt64(out var seconds))
                throw new InvalidDataException("The access token has no valid expiration time.");
            var expiresAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
            if (requireUnexpiredAccess && expiresAt <= DateTimeOffset.UtcNow.AddSeconds(30))
                throw new InvalidDataException("Refresh this account before switching; its access token has expired or is about to expire.");
            return new DesktopAuthSnapshot(authBytes.ToArray(),
                new DesktopAuthIdentity(IdentityUtilities.FingerprintEmail(email), accountId, chatUser ?? user ?? subject),
                idToken, accessToken, refreshToken, expiresAt);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentOutOfRangeException
            or InvalidOperationException)
        {
            // Do not chain parsing exceptions: their messages may contain token text.
            throw new InvalidDataException("The saved account credentials are malformed.");
        }
    }

    public DesktopSwitchReceipt? GetReceipt()
    {
        using var transactionLock = AcquireLock();
        var journal = ReadJournal(_pendingPath) ?? ReadJournal(_receiptPath);
        return journal is null ? null : ToReceipt(journal, File.Exists(_pendingPath));
    }

    public DesktopSwitchReceipt Install(DesktopAuthSnapshot snapshot, Guid selectedProfileId, Func<bool> isDesktopClosed)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (selectedProfileId == Guid.Empty) throw new ArgumentException("A saved account is required.", nameof(selectedProfileId));
        var intended = snapshot.CloneAuthBytes();
        var verified = ParseSnapshot(intended, requireUnexpiredAccess: true);
        using var transactionLock = AcquireLock();
        if (ReadJournal(_pendingPath) is not null)
            throw new InvalidOperationException("An unfinished account switch must be recovered before another switch.");
        var previousReceipt = ReadJournal(_receiptPath);
        EnsureClosed(isDesktopClosed);
        EnsureSafePath(_authPath);
        if (!Directory.Exists(_desktopHome))
            throw new InvalidOperationException("The selected Codex home does not exist.");
        var original = ReadOptional(_authPath, MaximumAuthBytes);
        var journal = new Journal
        {
            Version = 1,
            AuthPath = _authPath,
            SelectedProfileId = selectedProfileId,
            Identity = verified.Identity,
            CreatedUtc = DateTimeOffset.UtcNow,
            OriginalBytes = original,
            OriginalHash = Hash(original),
            IntendedHash = Hash(intended)!,
            AcceptedSourceHashes = new[] { Hash(intended)! },
            PreviousReceipt = previousReceipt is null ? null : ProvenanceOnly(previousReceipt)
        };
        WriteJournal(_pendingPath, journal); // Must reach durable storage before auth.json changes.
        EnsureClosed(isDesktopClosed);
        EnsureCurrentHash(journal.OriginalHash);
        WriteAtomic(_authPath, intended, () =>
        {
            EnsureClosed(isDesktopClosed);
            EnsureCurrentHash(journal.OriginalHash);
        });
        EnsureCurrentHash(journal.IntendedHash);
        return ToReceipt(journal, true);
    }

    /// <summary>
    /// Durably accepts both sides of a source credential synchronization before that copy
    /// starts. A failed or interrupted copy therefore cannot orphan the desktop's loan.
    /// The original intended hash remains unchanged for auth rollback comparisons.
    /// </summary>
    public void PrepareSourceSynchronization(DesktopSwitchReceipt expectedReceipt, string existingSourceHash,
        string nextSourceHash, Func<bool> isDesktopClosed)
    {
        ArgumentNullException.ThrowIfNull(expectedReceipt);
        if (!IsHash(existingSourceHash) || !IsHash(nextSourceHash))
            throw new InvalidOperationException("The source credential fingerprints are invalid.");
        existingSourceHash = existingSourceHash.ToUpperInvariant();
        nextSourceHash = nextSourceHash.ToUpperInvariant();
        using var transactionLock = AcquireLock();
        if (ReadJournal(_pendingPath) is not null)
            throw new InvalidOperationException("Recover the unfinished account switch before synchronizing its credentials.");
        var journal = ReadJournal(_receiptPath)
            ?? throw new InvalidOperationException("The desktop's account-switch receipt is missing.");
        EnsureExpectedReceipt(journal, expectedReceipt);
        if (!AcceptedHashes(journal).Contains(existingSourceHash, StringComparer.Ordinal))
            throw new InvalidOperationException("The saved account changed independently of the desktop.");
        EnsureClosed(isDesktopClosed);
        var desktop = ReadSnapshot()
            ?? throw new InvalidOperationException("The desktop credentials are missing.");
        if (desktop.Identity != journal.Identity || Hash(desktop.CloneAuthBytes()) != nextSourceHash)
            throw new InvalidOperationException("The desktop credentials no longer match this synchronization.");
        journal.AcceptedSourceHashes = new[] { existingSourceHash, nextSourceHash }.Distinct(StringComparer.Ordinal).ToArray();
        WriteJournal(_receiptPath, journal, () =>
        {
            EnsureClosed(isDesktopClosed);
            EnsureCurrentHash(nextSourceHash);
        });
    }

    /// <summary>Call before launching the desktop. Retains an encrypted last-switch rollback receipt.</summary>
    public void FinalizeSwitch(Func<bool> isDesktopClosed)
    {
        using var transactionLock = AcquireLock();
        var journal = ReadJournal(_pendingPath);
        if (journal is null) return;
        EnsureClosed(isDesktopClosed);
        EnsureCurrentHash(journal.IntendedHash);
        WriteJournal(_receiptPath, journal);
        EnsureClosed(isDesktopClosed);
        EnsureCurrentHash(journal.IntendedHash);
        EnsureSafePath(_pendingPath);
        File.Delete(_pendingPath);
    }

    /// <summary>Restores an unfinished switch, or the last finalized switch. Returns false when nothing exists.</summary>
    public bool Rollback(Func<bool> isDesktopClosed, bool pendingOnly = false, DesktopSwitchReceipt? expectedReceipt = null)
    {
        using var transactionLock = AcquireLock();
        var journalPath = _pendingPath;
        var journal = ReadJournal(journalPath);
        if (journal is null && !pendingOnly)
        {
            journalPath = _receiptPath;
            journal = ReadJournal(journalPath);
        }
        if (journal is null) return false;
        if (expectedReceipt is not null) EnsureExpectedReceipt(journal, expectedReceipt);
        if (journal.IsProvenanceOnly)
            throw new InvalidOperationException("This restored account receipt preserves its desktop connection but has no earlier rollback history.");
        EnsureClosed(isDesktopClosed);
        var currentHash = Hash(ReadOptional(_authPath, MaximumAuthBytes));
        if (currentHash != journal.IntendedHash && currentHash != journal.OriginalHash)
            throw new InvalidOperationException("Codex credentials changed after the switch. Recovery left them untouched.");
        var currentReceipt = ReadJournal(_receiptPath);
        var replaceReceipt = currentReceipt is not null && SameTransaction(currentReceipt, journal);
        if (journalPath == _receiptPath)
        {
            // A crash during finalized rollback must retain enough state to finish restoring
            // both auth.json and the preceding source-loan provenance on the next attempt.
            WriteJournal(_pendingPath, journal);
            journalPath = _pendingPath;
        }
        if (currentHash != journal.OriginalHash)
        {
            if (journal.OriginalBytes is null)
            {
                EnsureClosed(isDesktopClosed);
                EnsureCurrentHash(journal.IntendedHash);
                EnsureSafePath(_authPath);
                File.Delete(_authPath);
            }
            else
            {
                WriteAtomic(_authPath, journal.OriginalBytes, () =>
                {
                    EnsureClosed(isDesktopClosed);
                    EnsureCurrentHash(journal.IntendedHash);
                });
            }
        }
        EnsureClosed(isDesktopClosed);
        EnsureCurrentHash(journal.OriginalHash);
        if (replaceReceipt)
        {
            if (journal.PreviousReceipt is not null)
                WriteJournal(_receiptPath, journal.PreviousReceipt);
            else
            {
                EnsureSafePath(_receiptPath);
                File.Delete(_receiptPath);
            }
        }
        EnsureSafePath(journalPath);
        File.Delete(journalPath);
        return true;
    }

    private IDisposable AcquireLock()
    {
        // Refreshing several accounts and checking the selected card share this store.
        // Serialize those local readers before taking the cross-process exclusion lock.
        _transactionGate.Wait();
        try
        {
            EnsureSafePath(_journalDirectory);
            Directory.CreateDirectory(_journalDirectory);
            EnsureSafePath(_journalDirectory);
            RestrictDirectory(_journalDirectory);
            var lockPath = Path.Combine(_journalDirectory, "desktop-switch.lock");
            EnsureSafePath(lockPath);
            try
            {
                var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                try { RestrictFile(lockPath); return new TransactionLock(stream, _transactionGate); }
                catch { stream.Dispose(); throw; }
            }
            catch (IOException exception) when ((exception.HResult & 0xffff) is 32 or 33)
            {
                throw new InvalidOperationException("Another account switch is already in progress.");
            }
        }
        catch
        {
            _transactionGate.Release();
            throw;
        }
    }

    private sealed class TransactionLock(FileStream stream, SemaphoreSlim gate) : IDisposable
    {
        private FileStream? _stream = stream;

        public void Dispose()
        {
            var owned = Interlocked.Exchange(ref _stream, null);
            if (owned is null) return;
            try { owned.Dispose(); }
            finally { gate.Release(); }
        }
    }

    private Journal? ReadJournal(string path)
    {
        EnsureSafePath(path);
        var encrypted = ReadOptional(path, MaximumJournalBytes);
        if (encrypted is null) return null;
        byte[]? clear = null;
        try
        {
            clear = UserProtection.Unprotect(encrypted);
            if (clear.Length > MaximumJournalBytes) throw new InvalidDataException();
            var journal = JsonSerializer.Deserialize<Journal>(clear);
            if (journal is null || !ValidJournal(journal)
                || (journal.PreviousReceipt is not null && (!ValidJournal(journal.PreviousReceipt)
                    || !journal.PreviousReceipt.IsProvenanceOnly || journal.PreviousReceipt.PreviousReceipt is not null)))
                throw new InvalidDataException();
            return journal;
        }
        catch (Exception exception) when (exception is JsonException or CryptographicException or InvalidDataException)
        {
            throw new InvalidDataException("The account-switch recovery file cannot be verified. Credentials were left untouched.");
        }
        finally { if (clear is not null) CryptographicOperations.ZeroMemory(clear); }
    }

    private void WriteJournal(string path, Journal journal, Action? beforeReplace = null)
    {
        var clear = JsonSerializer.SerializeToUtf8Bytes(journal);
        try { WriteAtomic(path, UserProtection.Protect(clear), beforeReplace); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    private void EnsureCurrentHash(string? expected)
    {
        EnsureSafePath(_authPath);
        if (Hash(ReadOptional(_authPath, MaximumAuthBytes)) != expected)
            throw new InvalidOperationException("Codex credentials changed during the switch. The new credentials were left untouched.");
    }

    private static void EnsureClosed(Func<bool> isDesktopClosed)
    {
        ArgumentNullException.ThrowIfNull(isDesktopClosed);
        if (!isDesktopClosed())
            throw new InvalidOperationException("Close Codex and its running tasks before switching accounts.");
    }

    private static DesktopSwitchReceipt ToReceipt(Journal journal, bool pending) =>
        new(journal.SelectedProfileId, journal.Identity!, journal.CreatedUtc, pending, journal.IntendedHash,
            Array.AsReadOnly(AcceptedHashes(journal).ToArray()));

    private bool ValidJournal(Journal journal) => journal.Version == 1
        && string.Equals(journal.AuthPath, _authPath, StringComparison.OrdinalIgnoreCase)
        && journal.SelectedProfileId != Guid.Empty && journal.Identity is not null
        && !string.IsNullOrWhiteSpace(journal.Identity.AccountId)
        && journal.OriginalBytes?.Length is not > MaximumAuthBytes
        && journal.OriginalHash == Hash(journal.OriginalBytes)
        && IsHash(journal.IntendedHash) && journal.CreatedUtc != default
        && (journal.AcceptedSourceHashes is null || (journal.AcceptedSourceHashes.Length is >= 1 and <= 2
            && journal.AcceptedSourceHashes.All(IsHash)))
        && (!journal.IsProvenanceOnly || (journal.OriginalBytes is null && journal.OriginalHash is null));

    private static IEnumerable<string> AcceptedHashes(Journal journal) =>
        journal.AcceptedSourceHashes ?? new[] { journal.IntendedHash };

    private static bool SameTransaction(Journal first, Journal second) =>
        first.SelectedProfileId == second.SelectedProfileId && first.CreatedUtc == second.CreatedUtc
        && first.IntendedHash == second.IntendedHash && first.Identity == second.Identity;

    private static void EnsureExpectedReceipt(Journal journal, DesktopSwitchReceipt expected)
    {
        if (journal.SelectedProfileId != expected.SelectedProfileId || journal.CreatedUtc != expected.CreatedUtc
            || journal.IntendedHash != expected.IntendedAuthHash || journal.Identity != expected.Identity)
            throw new InvalidOperationException("The account-switch recovery transaction changed. Credentials were left untouched.");
    }

    private static Journal ProvenanceOnly(Journal original) => new()
    {
        Version = original.Version,
        AuthPath = original.AuthPath,
        SelectedProfileId = original.SelectedProfileId,
        Identity = original.Identity,
        CreatedUtc = original.CreatedUtc,
        IntendedHash = original.IntendedHash,
        AcceptedSourceHashes = AcceptedHashes(original).ToArray(),
        IsProvenanceOnly = true
    };

    private static string? Hash(byte[]? bytes) => bytes is null ? null : Convert.ToHexString(SHA256.HashData(bytes));
    private static bool IsHash(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);

    private static byte[]? ReadOptional(string path, int limit)
    {
        EnsureSafePath(path);
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length is < 1 || file.Length > limit)
                throw new InvalidDataException("The credential or recovery file has an invalid size.");
            var bytes = new byte[(int)file.Length];
            file.ReadExactly(bytes);
            if (file.ReadByte() != -1) throw new InvalidDataException("The credential file changed while being read.");
            return bytes;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static void WriteAtomic(string path, byte[] bytes, Action? beforeReplace = null)
    {
        EnsureSafePath(path);
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        EnsureSafePath(temporary);
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                RestrictFile(temporary); // No plaintext is written before the restrictive ACL is installed.
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            EnsureSafePath(path);
            EnsureSafePath(temporary);
            beforeReplace?.Invoke();
            // Same-directory rename stays atomic; WRITE_THROUGH also asks Windows to
            // finish persisting the move before a later credential mutation can begin.
            if (!MoveFileEx(temporary, path, 0x1 | 0x8))
                throw new IOException($"Windows could not commit the account credential transaction ({Marshal.GetLastWin32Error()}).");
        }
        finally
        {
            EnsureSafePath(temporary);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existingFileName, string newFileName, int flags);

    private static string CanonicalLocalDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)
            || path.IndexOf(':', 2) >= 0)
            throw new InvalidOperationException("Choose a normal absolute local directory for Codex credentials.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (full.Equals(Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A drive root cannot be used as a credential directory.");
        EnsureSafePath(full);
        return full;
    }

    private static bool IsWithin(string candidate, string root) => candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
        || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void EnsureSafePath(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Credential paths cannot pass through links or reparse points.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }

    private static void RestrictDirectory(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User ?? throw new InvalidOperationException("The current Windows user is unavailable.");
        var security = new DirectorySecurity();
        security.SetOwner(sid);
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    private static void RestrictFile(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User ?? throw new InvalidOperationException("The current Windows user is unavailable.");
        var security = new FileSecurity();
        security.SetOwner(sid);
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    private static JsonDocument ParseJwt(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3 || parts.Any(string.IsNullOrWhiteSpace)
            || parts.Any(part => part.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-' && character != '_')))
            throw new InvalidDataException("The saved account contains an invalid JWT.");
        using var header = JsonDocument.Parse(DecodeJwtPart(parts[0]), new JsonDocumentOptions { MaxDepth = 8 });
        RequireObject(header.RootElement);
        RejectDuplicateProperties(header.RootElement);
        var algorithm = RequiredString(header.RootElement, "alg", 128);
        if (algorithm.Equals("none", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unsigned account credentials cannot be used for switching.");
        _ = DecodeJwtPart(parts[2]);
        var document = JsonDocument.Parse(DecodeJwtPart(parts[1]), new JsonDocumentOptions { MaxDepth = 16 });
        try { RequireObject(document.RootElement); RejectDuplicateProperties(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
    }

    private static byte[] DecodeJwtPart(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '='));
    }

    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate credential fields are not allowed.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicateProperties(item);
    }

    private static void RequireObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The credential structure is invalid.");
    }

    private static JsonElement OptionalObject(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Undefined || !element.TryGetProperty(name, out var value)) return default;
        RequireObject(value);
        return value;
    }

    private static string RequiredString(JsonElement element, string name, int maximum)
    {
        var value = OptionalString(element, name);
        if (value is null || value.Length > maximum)
            throw new InvalidDataException("A required account credential is missing or too long.");
        return value;
    }

    private static string? OptionalString(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Undefined || !element.TryGetProperty(name, out var value)
            || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("An account credential field has an invalid type.");
        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static void EnsureMatches(string expected, string? actual)
    {
        if (actual is not null && !string.Equals(expected, actual, StringComparison.Ordinal))
            throw new InvalidDataException("The saved credential identity is inconsistent. Sign in to this account again.");
    }

    private static string? MergeIdentity(string? first, string? second)
    {
        if (first is not null) EnsureMatches(first, second);
        return first ?? second;
    }

    private static string? MergeEmail(string? first, string? second)
    {
        if (first is not null && second is not null
            && IdentityUtilities.FingerprintEmail(first) != IdentityUtilities.FingerprintEmail(second))
            throw new InvalidDataException("The saved credential email identities are inconsistent. Sign in again.");
        return first ?? second;
    }

    private sealed class Journal
    {
        public int Version { get; set; }
        public string AuthPath { get; set; } = "";
        public Guid SelectedProfileId { get; set; }
        public DesktopAuthIdentity? Identity { get; set; }
        public DateTimeOffset CreatedUtc { get; set; }
        public byte[]? OriginalBytes { get; set; }
        public string? OriginalHash { get; set; }
        public string IntendedHash { get; set; } = "";
        public string[]? AcceptedSourceHashes { get; set; }
        public Journal? PreviousReceipt { get; set; }
        public bool IsProvenanceOnly { get; set; }
    }

    private static class UserProtection
    {
        private const int UiForbidden = 1;
        public static byte[] Protect(byte[] input) => Transform(input, true);
        public static byte[] Unprotect(byte[] input) => Transform(input, false);

        private static byte[] Transform(byte[] input, bool encrypt)
        {
            var source = new DataBlob { Length = input.Length, Data = Marshal.AllocHGlobal(input.Length) };
            var result = default(DataBlob);
            try
            {
                Marshal.Copy(input, 0, source.Data, input.Length);
                var success = encrypt
                    ? CryptProtectData(ref source, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out result)
                    : CryptUnprotectData(ref source, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out result);
                if (!success || result.Length < 0 || result.Length > MaximumJournalBytes)
                    throw new CryptographicException("Windows could not protect or verify the account-switch recovery file.");
                var bytes = new byte[result.Length];
                Marshal.Copy(result.Data, bytes, 0, result.Length);
                return bytes;
            }
            finally
            {
                for (var index = 0; index < source.Length; index++) Marshal.WriteByte(source.Data, index, 0);
                Marshal.FreeHGlobal(source.Data);
                if (result.Data != IntPtr.Zero)
                {
                    for (var index = 0; index < result.Length; index++) Marshal.WriteByte(result.Data, index, 0);
                    LocalFree(result.Data);
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob { public int Length; public IntPtr Data; }
        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy,
            IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy,
            IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
    }
}
