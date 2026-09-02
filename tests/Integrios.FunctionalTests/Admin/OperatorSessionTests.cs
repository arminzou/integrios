using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Integrios.Admin;
using Integrios.Admin.Auth;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Integrios.FunctionalTests.Admin;

public sealed class OperatorSessionTests(OperatorSessionFixture fixture)
    : IClassFixture<OperatorSessionFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task SignIn_ResolvesOneUserPerIssuerAndSubjectPairAndNeverLinksByEmail()
    {
        OperatorSession first = await SignInAsync(fixture.AliceHost);
        first.User.GetProperty("display_name").GetString().ShouldBe(MockOidcProvider.AliceDisplayName);
        first.User.GetProperty("email").GetString().ShouldBe(MockOidcProvider.SharedEmail);
        Guid aliceUserId = first.User.GetProperty("user_id").GetGuid();

        // The same pair signing in again resolves to the same User rather than provisioning another.
        OperatorSession repeat = await SignInAsync(fixture.AliceHost);
        repeat.User.GetProperty("user_id").GetGuid().ShouldBe(aliceUserId);
        (await fixture.CountAsync("users")).ShouldBe(1);
        (await fixture.CountAsync("operator_identities")).ShouldBe(1);

        // A different issuer and subject carrying the identical email is a different human.
        OperatorSession other = await SignInAsync(fixture.BobHost);
        other.User.GetProperty("user_id").GetGuid().ShouldNotBe(aliceUserId);
        other.User.GetProperty("email").GetString().ShouldBe(MockOidcProvider.SharedEmail);
        (await fixture.CountAsync("users")).ShouldBe(2);
        (await fixture.CountAsync("operator_identities")).ShouldBe(2);
    }

    [Fact]
    public async Task ConcurrentFirstSignIns_ForOnePair_ProvisionExactlyOneUser()
    {
        OperatorSession[] sessions = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(_ => SignInAsync(fixture.AliceHost)));

        sessions.Select(session => session.User.GetProperty("user_id").GetGuid())
            .Distinct()
            .Count()
            .ShouldBe(1);
        (await fixture.CountAsync("users")).ShouldBe(1);
        (await fixture.CountAsync("operator_identities")).ShouldBe(1);
    }

    [Fact]
    public async Task SessionCookie_IsProtectedFixedLifetimeAndValidOnAnotherReplica()
    {
        OperatorSession session = await SignInAsync(fixture.AliceHost);

        session.SetCookieHeader.ShouldContain("httponly", Case.Insensitive);
        session.SetCookieHeader.ShouldContain("secure", Case.Insensitive);
        session.SetCookieHeader.ShouldContain("samesite=strict", Case.Insensitive);
        // Fixed, non-sliding: the cookie expires on the configured bound, not on inactivity.
        session.SetCookieHeader.ShouldContain("expires=", Case.Insensitive);
        session.Cookies.ShouldContainKey(OperatorSessionOptions.CookieName);
        // The browser receives the session cookie only. No OperatorKey, no provider tokens.
        session.SetCookieHeader.ShouldNotContain("OperatorKey", Case.Insensitive);
        session.SetCookieHeader.ShouldNotContain("id_token", Case.Insensitive);
        session.SetCookieHeader.ShouldNotContain("access_token", Case.Insensitive);
        session.RawBody.ShouldNotContain("access_token");
        session.RawBody.ShouldNotContain("id_token");

        // A cookie issued by one replica is accepted by another sharing the durable key ring.
        using HttpClient replica = Client(fixture.AliceReplica);
        using HttpResponseMessage onReplica = await SendAsync(
            replica, HttpMethod.Get, OperatorSessionEndpoints.BootstrapPath, session.Cookies);
        onReplica.StatusCode.ShouldBe(HttpStatusCode.OK);
        using JsonDocument replicaBody = JsonDocument.Parse(await onReplica.Content.ReadAsStringAsync());
        replicaBody.RootElement.GetProperty("user_id").GetGuid()
            .ShouldBe(session.User.GetProperty("user_id").GetGuid());
    }

    [Fact]
    public async Task UnsafeCookieRequests_RequireAntiforgery_WhileOperatorKeyAndLogoutStillWork()
    {
        OperatorSession session = await SignInAsync(fixture.AliceHost);
        using HttpClient client = Client(fixture.AliceHost);

        // Same session, same body: the only difference is the antiforgery token.
        const string tenants = "/admin/tenants";
        string body = JsonSerializer.Serialize(new { slug = "antiforgery-tenant", name = "Antiforgery tenant" });

        using HttpResponseMessage missing = await SendAsync(
            client, HttpMethod.Post, tenants, session.Cookies, body);
        missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using HttpResponseMessage wrong = await SendAsync(
            client, HttpMethod.Post, tenants, session.Cookies, body,
            (session.AntiforgeryHeaderName, "not-the-issued-token"));
        wrong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using HttpResponseMessage accepted = await SendAsync(
            client, HttpMethod.Post, tenants, session.Cookies, body,
            (session.AntiforgeryHeaderName, session.AntiforgeryToken));
        accepted.StatusCode.ShouldBe(HttpStatusCode.Created);

        // A safe cookie request never needs the token.
        using HttpResponseMessage safe = await SendAsync(client, HttpMethod.Get, tenants, session.Cookies);
        safe.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Machine authentication is a separate scheme and does not acquire or need any of this.
        using var machine = new HttpRequestMessage(HttpMethod.Post, tenants)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { slug = "machine-tenant", name = "Machine tenant" }),
                System.Text.Encoding.UTF8,
                "application/json"),
        };
        machine.Headers.TryAddWithoutValidation("Authorization", AdminApiFixture.GlobalOperatorAuthHeader);
        using HttpResponseMessage machineResponse = await client.SendAsync(machine);
        machineResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        machineResponse.Headers.Contains("Set-Cookie").ShouldBeFalse();

        // Logout clears the session cookie, and the cleared cookie no longer authenticates.
        using HttpResponseMessage logout = await SendAsync(
            client, HttpMethod.Post, OperatorSessionEndpoints.LogoutPath, session.Cookies, "",
            (session.AntiforgeryHeaderName, session.AntiforgeryToken));
        logout.Headers.TryGetValues("Set-Cookie", out var cleared).ShouldBeTrue();
        Dictionary<string, string> afterLogout = Merge(session.Cookies, cleared!);
        afterLogout[OperatorSessionOptions.CookieName].ShouldBeEmpty();

        using HttpResponseMessage denied = await SendAsync(
            client, HttpMethod.Get, OperatorSessionEndpoints.BootstrapPath, afterLogout);
        denied.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UnauthenticatedBrowserRequests_AreRejectedRatherThanRedirected()
    {
        using HttpClient client = Client(fixture.AliceHost);

        using HttpResponseMessage session = await client.GetAsync(OperatorSessionEndpoints.BootstrapPath);
        session.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // An unauthenticated API call answers 401 instead of redirecting an XHR to the provider.
        using HttpResponseMessage api = await client.GetAsync("/admin/tenants");
        api.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // A tampered callback cannot mint a session.
        using HttpResponseMessage forged = await client.GetAsync("/auth/callback?code=forged&state=forged");
        forged.IsSuccessStatusCode.ShouldBeFalse();
        forged.Headers.TryGetValues("Set-Cookie", out var cookies);
        (cookies ?? []).ShouldNotContain(value => value.Contains(OperatorSessionOptions.CookieName, StringComparison.Ordinal)
            && !value.Contains(OperatorSessionOptions.CookieName + "=;", StringComparison.Ordinal));
    }

    /// Drives the real authorization-code flow: Admin redirects to the provider, the provider
    /// redirects back with a code, and Admin exchanges it over the back channel before issuing its
    /// own cookie.
    private async Task<OperatorSession> SignInAsync(WebApplicationFactory<Program> host)
    {
        using HttpClient client = Client(host);
        using var provider = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });

        using HttpResponseMessage challenge = await client.GetAsync(OperatorSessionEndpoints.LoginPath);
        challenge.StatusCode.ShouldBe(HttpStatusCode.Found);
        Dictionary<string, string> cookies = Merge(
            new Dictionary<string, string>(StringComparer.Ordinal), challenge.Headers.GetValues("Set-Cookie"));

        using HttpResponseMessage authorized = await provider.GetAsync(challenge.Headers.Location);
        authorized.StatusCode.ShouldBe(HttpStatusCode.Found);
        string callback = authorized.Headers.Location!.PathAndQuery;

        using HttpResponseMessage callbackResponse = await SendAsync(client, HttpMethod.Get, callback, cookies);
        callbackResponse.StatusCode.ShouldBe(HttpStatusCode.Found);
        string setCookie = string.Join("; ", callbackResponse.Headers.GetValues("Set-Cookie"));
        cookies = Merge(cookies, callbackResponse.Headers.GetValues("Set-Cookie"));

        using HttpResponseMessage bootstrap = await SendAsync(
            client, HttpMethod.Get, OperatorSessionEndpoints.BootstrapPath, cookies);
        string body = await bootstrap.Content.ReadAsStringAsync();
        bootstrap.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        cookies = Merge(cookies, bootstrap.Headers.TryGetValues("Set-Cookie", out var more) ? more : []);

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement.Clone();
        return new OperatorSession(
            root,
            cookies,
            setCookie,
            body,
            root.GetProperty("antiforgery_token").GetString()!,
            root.GetProperty("antiforgery_header_name").GetString()!);
    }

    /// The dashboard is HTTPS-only in production: the session and antiforgery cookies are both
    /// Secure, so the test must speak HTTPS rather than relax the host.
    private static HttpClient Client(WebApplicationFactory<Program> host) =>
        host.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string url,
        IReadOnlyDictionary<string, string> cookies,
        string? body = null,
        (string Name, string Value)? header = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (cookies.Count > 0)
            request.Headers.Add("Cookie", string.Join("; ", cookies.Select(pair => $"{pair.Key}={pair.Value}")));
        if (header is { } supplied)
            request.Headers.TryAddWithoutValidation(supplied.Name, supplied.Value);
        if (body is not null)
        {
            request.Content = new StringContent(body, System.Text.Encoding.UTF8);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        return await client.SendAsync(request);
    }

    private static Dictionary<string, string> Merge(
        IReadOnlyDictionary<string, string> existing,
        IEnumerable<string> setCookieHeaders)
    {
        var merged = new Dictionary<string, string>(existing, StringComparer.Ordinal);
        foreach (string header in setCookieHeaders)
        {
            string pair = header.Split(';', 2)[0];
            int equals = pair.IndexOf('=');
            if (equals > 0)
                merged[pair[..equals]] = pair[(equals + 1)..];
        }

        return merged;
    }

    private sealed record OperatorSession(
        JsonElement User,
        Dictionary<string, string> Cookies,
        string SetCookieHeader,
        string RawBody,
        string AntiforgeryToken,
        string AntiforgeryHeaderName);
}
