using System.Text;

namespace Integrios.Application.Pagination;

public static class PageCursor
{
    public static string Encode(DateTimeOffset createdAt, Guid id)
    {
        var raw = $"{createdAt.UtcTicks}:{id}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
    }

    public static bool TryDecode(string cursor, out DateTimeOffset createdAt, out Guid id)
    {
        createdAt = default;
        id = default;
        try
        {
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var colon = raw.IndexOf(':');
            if (colon <= 0) return false;
            if (!long.TryParse(raw[..colon], out var ticks)) return false;
            if (!Guid.TryParse(raw[(colon + 1)..], out id)) return false;
            createdAt = new DateTimeOffset(ticks, TimeSpan.Zero);
            return true;
        }
        catch { return false; }
    }
}
