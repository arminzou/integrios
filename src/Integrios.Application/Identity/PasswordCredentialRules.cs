using System.Buffers;
using System.Net.Mail;
using System.Text;

namespace Integrios.Application.Identity;

public static class PasswordCredentialRules
{
    public const int MinimumPasswordLength = 15;
    public const int MaximumPasswordLength = 128;

    public static bool IsValidPasswordHash(string value) =>
        value.Length is > 0 and <= 1024 && !string.IsNullOrWhiteSpace(value);

    public static bool TryNormalizeEmail(
        string? value,
        out string email,
        out string normalizedEmail)
    {
        email = value?.Trim() ?? string.Empty;
        normalizedEmail = email.ToUpperInvariant();

        return email.Length <= 320
            && normalizedEmail.Length <= 320
            && MailAddress.TryCreate(email, out MailAddress? parsed)
            && parsed.Address.Equals(email, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsValidPassword(string value)
    {
        int scalarCount = 0;
        ReadOnlySpan<char> remaining = value.AsSpan();

        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out _, out int consumed) != OperationStatus.Done)
                return false;

            scalarCount++;
            if (scalarCount > MaximumPasswordLength)
                return false;

            remaining = remaining[consumed..];
        }

        return scalarCount >= MinimumPasswordLength;
    }
}
