namespace CodexAccountMonitor.Core.Models;

public sealed record AccountProfile
{
    public required Guid Id { get; init; }
    public required int Slot { get; init; }
    public required string Alias { get; init; }
    public string? MaskedEmail { get; init; }
    public string? IdentityFingerprint { get; init; }
    public DateTimeOffset AddedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public bool ShowMaskedEmail { get; init; } = true;
}
