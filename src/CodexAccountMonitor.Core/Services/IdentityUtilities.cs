using System.Security.Cryptography;
using System.Text;

namespace CodexAccountMonitor.Core.Services;

public static class IdentityUtilities
{
    public static string? MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        var normalized = email.Trim();
        var separator = normalized.IndexOf('@');
        if (separator <= 0 || separator == normalized.Length - 1)
        {
            return "••••";
        }

        var local = normalized[..separator];
        var domain = normalized[(separator + 1)..];
        var visible = local.Length == 1 ? local : local[..1];
        return $"{visible}{new string('•', Math.Clamp(local.Length - visible.Length, 3, 8))}@{domain}";
    }

    public static string? FingerprintEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(email.Trim().ToUpperInvariant()));
        return Convert.ToHexString(bytes);
    }
}
