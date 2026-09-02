using Integrios.Infrastructure.Common.Pagination;
using Microsoft.AspNetCore.DataProtection;

namespace Integrios.Infrastructure.UnitTests.Common.Pagination;

public sealed class PageCursorTests
{
    private static readonly IDataProtectionProvider Provider = new EphemeralDataProtectionProvider();

    [Fact]
    public void TryDecode_RequiresAnUntamperedExpectedListScope()
    {
        DateTimeOffset createdAt = DateTimeOffset.UtcNow;
        Guid id = Guid.NewGuid();
        string cursor = PageCursor.Encode(Provider, "tenants", createdAt, id, DateTimeOffset.UtcNow);

        PageCursor.TryDecode(Provider, cursor, "tenants", out DateTimeOffset decodedAt, out Guid decodedId).ShouldBeTrue();
        decodedAt.ShouldBe(createdAt);
        decodedId.ShouldBe(id);
        PageCursor.TryDecode(Provider, cursor, "connectors", out _, out _).ShouldBeFalse();
        PageCursor.TryDecode(Provider, cursor[..^1] + (cursor[^1] == 'A' ? "B" : "A"), "tenants", out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void TryDecode_RejectsExpiredCursor()
    {
        string cursor = PageCursor.Encode(
            Provider,
            "tenants",
            DateTimeOffset.UtcNow,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddHours(-24).AddSeconds(-1));

        PageCursor.TryDecode(Provider, cursor, "tenants", out _, out _).ShouldBeFalse();
    }
}
