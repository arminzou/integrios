using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace Integrios.Infrastructure.Common.Pagination;

internal static class PageCursor
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    private const string Purpose = "Integrios.Infrastructure.PageCursor.v1";

    public static string Encode(IDataProtectionProvider provider, string scope, DateTimeOffset createdAt, Guid id, DateTimeOffset issuedAt) =>
        provider.CreateProtector(Purpose).Protect($"{scope}\n{createdAt.UtcTicks}\n{id}\n{issuedAt.UtcTicks}");

    public static bool TryDecode(IDataProtectionProvider provider, string cursor, string scope, out DateTimeOffset createdAt, out Guid id)
    {
        createdAt = default;
        id = default;
        try
        {
            string[] parts = provider.CreateProtector(Purpose).Unprotect(cursor).Split('\n');
            if (parts.Length != 4 || parts[0] != scope) return false;
            if (!long.TryParse(parts[1], out long ticks)) return false;
            if (!Guid.TryParse(parts[2], out id)) return false;
            if (!long.TryParse(parts[3], out long issuedTicks)) return false;
            createdAt = new DateTimeOffset(ticks, TimeSpan.Zero);
            return DateTimeOffset.UtcNow < new DateTimeOffset(issuedTicks, TimeSpan.Zero).Add(Lifetime);
        }
        catch { return false; }
    }
}
