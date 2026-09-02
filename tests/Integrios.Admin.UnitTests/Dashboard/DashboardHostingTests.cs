using Integrios.Admin.Dashboard;
using Microsoft.AspNetCore.Http;

namespace Integrios.Admin.UnitTests.Dashboard;

public sealed class DashboardHostingTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/tenants")]
    [InlineData("/tenants/8b1d0f2e/sources")]
    [InlineData("/events")]
    [InlineData("/administration")]
    [InlineData("/authors")]
    public void BrowserRoutes_FallBackToTheShell(string path) =>
        DashboardHosting.IsBrowserRoute(Request(path)).ShouldBeTrue();

    [Theory]
    [InlineData("/admin")]
    [InlineData("/admin/tenants")]
    [InlineData("/admin/tenants/unknown-capability")]
    [InlineData("/auth/login")]
    [InlineData("/auth/session")]
    [InlineData("/openapi/v1.json")]
    [InlineData("/health")]
    [InlineData("/ready")]
    [InlineData("/metrics")]
    public void ApiAuthenticationAndOperationalRoutes_KeepTheirOwnResponses(string path) =>
        // Answering these with the dashboard shell would turn a broken client or a missing endpoint
        // into a page that looks like it worked.
        DashboardHosting.IsBrowserRoute(Request(path)).ShouldBeFalse();

    [Fact]
    public void PrefixMatching_IsSegmentWiseAndCaseInsensitive()
    {
        // A path that merely starts with the same letters is still a browser route.
        DashboardHosting.IsBrowserRoute(Request("/administrators")).ShouldBeTrue();
        DashboardHosting.IsBrowserRoute(Request("/ADMIN/tenants")).ShouldBeFalse();
    }

    private static HttpContext Request(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        return context;
    }
}
