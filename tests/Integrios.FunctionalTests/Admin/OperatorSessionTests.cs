using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Integrios.Admin;
using Integrios.Admin.Auth;
using Integrios.Application.Identity;
using Integrios.Tests.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Integrios.FunctionalTests.Admin;

public sealed class OperatorSessionTests(OperatorSessionFixture fixture)
    : IClassFixture<OperatorSessionFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task AuthenticationOptions_ReportOnlyEnabledMethods_AndIssueAntiforgery()
    {
        AuthenticationOptions oidc = await GetAuthenticationOptionsAsync(fixture.AliceHost);
        oidc.Body.GetProperty("oidc_enabled").GetBoolean().ShouldBeTrue();
        oidc.Body.GetProperty("password_enabled").GetBoolean().ShouldBeFalse();
        oidc.Body.GetProperty("oidc_display_name").GetString().ShouldBe("Test SSO");

        AuthenticationOptions password = await GetAuthenticationOptionsAsync(fixture.PasswordHost);
        password.Body.GetProperty("oidc_enabled").GetBoolean().ShouldBeFalse();
        password.Body.GetProperty("password_enabled").GetBoolean().ShouldBeTrue();
        password.Body.GetProperty("oidc_display_name").ValueKind.ShouldBe(JsonValueKind.Null);
        password.AntiforgeryToken.ShouldNotBeNullOrWhiteSpace();
        password.Cookies.ShouldNotBeEmpty();

        AuthenticationOptions both = await GetAuthenticationOptionsAsync(fixture.BothHost);
        both.Body.GetProperty("oidc_enabled").GetBoolean().ShouldBeTrue();
        both.Body.GetProperty("password_enabled").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task PasswordSignIn_IsCaseInsensitive_Protected_AndValidOnAnotherReplica()
    {
        Guid userId = await fixture.CreatePasswordUserAsync();

        OperatorSession session = await SignInWithPasswordAsync(
            fixture.PasswordHost,
            "  PASSWORD@EXAMPLE.COM ",
            "correct-password!",
            "/tenants?view=all");

        session.User.GetProperty("user_id").GetGuid().ShouldBe(userId);
        session.User.GetProperty("display_name").GetString().ShouldBe("Password Operator");
        session.RedirectLocation.ShouldBe("/tenants?view=all");
        session.SetCookieHeader.ShouldContain("httponly", Case.Insensitive);
        session.SetCookieHeader.ShouldContain("samesite=strict", Case.Insensitive);
        session.SetCookieHeader.ShouldNotContain("correct-password!", Case.Sensitive);
        session.RawBody.ShouldNotContain("correct-password!", Case.Sensitive);

        using HttpClient replica = Client(fixture.PasswordReplica);
        using HttpResponseMessage response = await SendAsync(
            replica,
            HttpMethod.Get,
            OperatorSessionEndpoints.BootstrapPath,
            session.Cookies);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // The replica accepts the cookie because of the configured key ring, not a shared default.
        using HttpClient otherKeyRing = Client(fixture.PasswordOtherKeyRing);
        using HttpResponseMessage rejected = await SendAsync(
            otherKeyRing,
            HttpMethod.Get,
            OperatorSessionEndpoints.BootstrapPath,
            session.Cookies);
        rejected.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PasswordFailures_AreGenericAndRequireAntiforgery()
    {
        await fixture.CreatePasswordUserAsync();
        AuthenticationOptions options = await GetAuthenticationOptionsAsync(fixture.PasswordHost);
        using HttpClient client = Client(fixture.PasswordHost);
        string wrongBody = PasswordBody("password@example.com", "wrong-password!!");

        using HttpResponseMessage noToken = await SendAsync(
            client,
            HttpMethod.Post,
            OperatorSessionEndpoints.PasswordLoginPath,
            options.Cookies,
            wrongBody);
        noToken.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using HttpResponseMessage wrong = await SendPasswordAttemptAsync(
            client,
            options,
            "password@example.com",
            "wrong-password!!");
        using HttpResponseMessage unknown = await SendPasswordAttemptAsync(
            client,
            options,
            "unknown@example.com",
            "wrong-password!!");
        using HttpResponseMessage tooShort = await SendPasswordAttemptAsync(
            client,
            options,
            "password@example.com",
            "short");
        using HttpResponseMessage oversized = await SendPasswordAttemptAsync(
            client,
            options,
            new string('a', 321),
            "wrong-password!!");
        using HttpResponseMessage unmapped = await SendAsync(
            client,
            HttpMethod.Post,
            OperatorSessionEndpoints.PasswordLoginPath,
            options.Cookies,
            """{"email":"unknown@example.com","password":"wrong-password!!","unexpected":true}""",
            (options.AntiforgeryHeaderName, options.AntiforgeryToken));
        using HttpResponseMessage malformed = await SendAsync(
            client,
            HttpMethod.Post,
            OperatorSessionEndpoints.PasswordLoginPath,
            options.Cookies,
            "{\"email\":\"unknown@example.com\"",
            (options.AntiforgeryHeaderName, options.AntiforgeryToken));
        wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        unknown.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        tooShort.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        oversized.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        unmapped.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        malformed.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await wrong.Content.ReadAsStringAsync()).ShouldBe(await unknown.Content.ReadAsStringAsync());
        (await tooShort.Content.ReadAsStringAsync()).ShouldBe(await unknown.Content.ReadAsStringAsync());
        (await oversized.Content.ReadAsStringAsync()).ShouldBe(await unknown.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PasswordLogin_RejectsOversizedBodiesBeforeBindingOnKestrel()
    {
        int productPort = GetAvailablePort();
        int operationalPort = GetAvailablePort();
        using WebApplicationFactory<Program> host = fixture.PasswordHost.WithWebHostBuilder(
            builder => builder.UseSetting("OperationalPort", operationalPort.ToString()));
        host.UseKestrel(productPort);
        host.StartServer();
        string productAddress = host.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses
            .Single(address => new Uri(address).Port != operationalPort);
        var baseAddress = new UriBuilder(productAddress) { Host = IPAddress.Loopback.ToString() }.Uri;
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = baseAddress,
        };
        AuthenticationOptions options = await GetAuthenticationOptionsAsync(client);

        using HttpResponseMessage response = await SendAsync(
            client,
            HttpMethod.Post,
            OperatorSessionEndpoints.PasswordLoginPath,
            options.Cookies,
            new string(' ', (int)OperatorSessionEndpoints.MaximumPasswordLoginBodySize + 1),
            (options.AntiforgeryHeaderName, options.AntiforgeryToken));

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    private static int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task PasswordSession_EmailChangePreserves_ResetAndDisableRevoke_OidcUnaffected()
    {
        Guid userId = await fixture.CreatePasswordUserAsync();
        OperatorSession first = await SignInWithPasswordAsync(
            fixture.BothHost,
            "password@example.com",
            "correct-password!");

        (await fixture.SendAsync(new ChangeOperatorUserPasswordEmailCommand(
            userId,
            "changed@example.com"))).Status.ShouldBe(PasswordCredentialMutationStatus.Succeeded);
        using HttpClient replica = Client(fixture.PasswordReplica);
        using HttpResponseMessage afterEmailChange = await SendAsync(
            replica,
            HttpMethod.Get,
            OperatorSessionEndpoints.BootstrapPath,
            first.Cookies);
        afterEmailChange.StatusCode.ShouldBe(HttpStatusCode.OK);

        string replacementHash = new Microsoft.AspNetCore.Identity.PasswordHasher<string>()
            .HashPassword(string.Empty, "replacement-password!");
        (await fixture.SendAsync(new SetOperatorUserPasswordCommand(
            userId,
            null,
            replacementHash))).Status.ShouldBe(PasswordCredentialMutationStatus.Succeeded);
        using HttpResponseMessage afterReset = await SendAsync(
            replica,
            HttpMethod.Get,
            OperatorSessionEndpoints.BootstrapPath,
            first.Cookies);
        afterReset.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        OperatorSession second = await SignInWithPasswordAsync(
            fixture.PasswordHost,
            "changed@example.com",
            "replacement-password!");
        OperatorSession oidc = await SignInAsync(fixture.AliceHost);
        (await fixture.SendAsync(new DisableOperatorUserPasswordCommand(userId))).Status
            .ShouldBe(PasswordCredentialMutationStatus.Succeeded);
        using HttpResponseMessage afterDisable = await SendAsync(
            replica,
            HttpMethod.Get,
            OperatorSessionEndpoints.BootstrapPath,
            second.Cookies);
        afterDisable.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using HttpClient oidcClient = Client(fixture.AliceReplica);
        using HttpResponseMessage oidcStillValid = await SendAsync(
            oidcClient,
            HttpMethod.Get,
            OperatorSessionEndpoints.BootstrapPath,
            oidc.Cookies);
        oidcStillValid.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PasswordSessions_AreRejectedWhenDeploymentPasswordLoginIsDisabled()
    {
        await fixture.CreatePasswordUserAsync();
        OperatorSession password = await SignInWithPasswordAsync(
            fixture.BothHost,
            "password@example.com",
            "correct-password!");

        using HttpClient oidcOnly = Client(fixture.AliceHost);
        using HttpResponseMessage response = await SendAsync(
            oidcOnly,
            HttpMethod.Get,
            OperatorSessionEndpoints.BootstrapPath,
            password.Cookies);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PasswordAttempts_AreLimitedPerEmailAndConnectionPeer()
    {
        var emailLogs = new CapturingLoggerProvider();
        using WebApplicationFactory<Program> emailHost = fixture.PasswordHost.WithWebHostBuilder(
            builder => builder.ConfigureLogging(logging => logging.AddProvider(emailLogs)));
        AuthenticationOptions emailOptions = await GetAuthenticationOptionsAsync(emailHost);
        using HttpClient emailClient = Client(emailHost);
        for (int attempt = 0; attempt < OperatorPasswordRateLimiter.AttemptsPerEmail; attempt++)
        {
            using HttpResponseMessage response = await SendPasswordAttemptAsync(
                emailClient,
                emailOptions,
                "same@example.com",
                "wrong-password!!");
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using HttpResponseMessage emailLimited = await SendPasswordAttemptAsync(
            emailClient,
            emailOptions,
            " SAME@EXAMPLE.COM ",
            "wrong-password!!");
        emailLimited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        emailLimited.Headers.RetryAfter.ShouldNotBeNull();
        emailLogs.AnyMessageContains("outcome rate_limited").ShouldBeTrue();
        emailLogs.AnyMessageContains("rate limit email").ShouldBeTrue();
        emailLogs.AnyMessageContains("same@example.com").ShouldBeFalse();
        emailLogs.AnyMessageContains("wrong-password!!").ShouldBeFalse();

        var peerLogs = new CapturingLoggerProvider();
        using WebApplicationFactory<Program> peerHost = fixture.PasswordHost.WithWebHostBuilder(
            builder => builder.ConfigureLogging(logging => logging.AddProvider(peerLogs)));
        AuthenticationOptions peerOptions = await GetAuthenticationOptionsAsync(peerHost);
        using HttpClient peerClient = Client(peerHost);
        for (int attempt = 0; attempt < OperatorPasswordRateLimiter.AttemptsPerPeer; attempt++)
        {
            using HttpResponseMessage response = await SendPasswordAttemptAsync(
                peerClient,
                peerOptions,
                $"unknown-{attempt}@example.com",
                "wrong-password!!");
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using HttpResponseMessage peerLimited = await SendPasswordAttemptAsync(
            peerClient,
            peerOptions,
            "last@example.com",
            "wrong-password!!");
        peerLimited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        peerLogs.AnyMessageContains("rate limit peer").ShouldBeTrue();
    }

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
        // The token and session were issued by AliceHost; the second process must accept both.
        using HttpClient client = Client(fixture.AliceReplica);

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
        logout.Headers.Location?.OriginalString.ShouldBe("/?signed_out=1");
        logout.Headers.TryGetValues("Set-Cookie", out var cleared).ShouldBeTrue();
        Dictionary<string, string> afterLogout = Merge(session.Cookies, cleared!);
        afterLogout[OperatorSessionOptions.CookieName].ShouldBeEmpty();

        using HttpResponseMessage denied = await SendAsync(
            client, HttpMethod.Get, OperatorSessionEndpoints.BootstrapPath, afterLogout);
        denied.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// The dashboard's own sign-out is a native HTML form submission, not an XHR: the browser sets no
    /// custom header, so the token must travel through the antiforgery form field the session
    /// response names, using the exact `application/x-www-form-urlencoded` shape a browser sends.
    [Fact]
    public async Task NativeLogoutFormSubmission_UsesTheConfiguredFormField_AndSucceeds()
    {
        OperatorSession session = await SignInAsync(fixture.AliceHost);
        using HttpClient client = Client(fixture.AliceHost);

        using var form = new FormUrlEncodedContent(
            [new KeyValuePair<string, string>(session.AntiforgeryFormFieldName, session.AntiforgeryToken)]);
        using var request = new HttpRequestMessage(HttpMethod.Post, OperatorSessionEndpoints.LogoutPath)
        {
            Content = form,
        };
        request.Headers.Add(
            "Cookie", string.Join("; ", session.Cookies.Select(pair => $"{pair.Key}={pair.Value}")));

        using HttpResponseMessage logout = await client.SendAsync(request);
        logout.StatusCode.ShouldBe(HttpStatusCode.Found);
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

    [Fact]
    public async Task ProviderRefusal_ReturnsToTheDashboardInsteadOfChallengingAgain()
    {
        using HttpClient client = Client(fixture.AliceHost);
        using HttpResponseMessage challenge = await client.GetAsync(OperatorSessionEndpoints.LoginPath);
        string state = QueryHelpers.ParseQuery(challenge.Headers.Location!.Query)["state"].ToString();
        Dictionary<string, string> cookies = Merge(
            new Dictionary<string, string>(StringComparer.Ordinal), challenge.Headers.GetValues("Set-Cookie"));

        using HttpResponseMessage refused = await SendAsync(
            client,
            HttpMethod.Get,
            $"/auth/callback?error=access_denied&state={Uri.EscapeDataString(state)}",
            cookies);

        refused.StatusCode.ShouldBe(HttpStatusCode.Found);
        refused.Headers.Location?.OriginalString.ShouldBe("/?error=access_denied");
    }

    [Theory]
    [InlineData("/tenants?view=all", "/tenants?view=all")]
    [InlineData("//evil.example", "/")]
    [InlineData(@"/\evil.example", "/")]
    public async Task SignIn_ReturnsOnlyToSameOriginPaths(string returnTo, string expected)
    {
        OperatorSession session = await SignInAsync(fixture.AliceHost, returnTo);

        session.RedirectLocation.ShouldBe(expected);
    }

    /// Drives the real authorization-code flow: Admin redirects to the provider, the provider
    /// redirects back with a code, and Admin exchanges it over the back channel before issuing its
    /// own cookie.
    private async Task<OperatorSession> SignInAsync(WebApplicationFactory<Program> host, string? returnTo = null)
    {
        using HttpClient client = Client(host);
        using var provider = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });

        string loginPath = OperatorSessionEndpoints.LoginPath
            + (returnTo is null ? string.Empty : $"?return_to={Uri.EscapeDataString(returnTo)}");
        using HttpResponseMessage challenge = await client.GetAsync(loginPath);
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
            root.GetProperty("antiforgery_header_name").GetString()!,
            root.GetProperty("antiforgery_form_field_name").GetString()!,
            callbackResponse.Headers.Location?.OriginalString);
    }

    private static async Task<OperatorSession> SignInWithPasswordAsync(
        WebApplicationFactory<Program> host,
        string email,
        string password,
        string? returnTo = null)
    {
        AuthenticationOptions options = await GetAuthenticationOptionsAsync(host);
        using HttpClient client = Client(host);
        using HttpResponseMessage login = await SendPasswordAttemptAsync(
            client,
            options,
            email,
            password,
            returnTo);
        string loginBody = await login.Content.ReadAsStringAsync();
        login.StatusCode.ShouldBe(HttpStatusCode.OK, loginBody);
        Dictionary<string, string> cookies = Merge(
            options.Cookies,
            login.Headers.GetValues("Set-Cookie"));

        using JsonDocument loginDocument = JsonDocument.Parse(loginBody);
        using HttpResponseMessage bootstrap = await SendAsync(
            client,
            HttpMethod.Get,
            OperatorSessionEndpoints.BootstrapPath,
            cookies);
        string bootstrapBody = await bootstrap.Content.ReadAsStringAsync();
        bootstrap.StatusCode.ShouldBe(HttpStatusCode.OK, bootstrapBody);
        cookies = Merge(
            cookies,
            bootstrap.Headers.TryGetValues("Set-Cookie", out var more) ? more : []);

        using JsonDocument document = JsonDocument.Parse(bootstrapBody);
        JsonElement root = document.RootElement.Clone();
        return new OperatorSession(
            root,
            cookies,
            string.Join("; ", login.Headers.GetValues("Set-Cookie")),
            loginBody + bootstrapBody,
            root.GetProperty("antiforgery_token").GetString()!,
            root.GetProperty("antiforgery_header_name").GetString()!,
            root.GetProperty("antiforgery_form_field_name").GetString()!,
            loginDocument.RootElement.GetProperty("return_to").GetString());
    }

    private static async Task<AuthenticationOptions> GetAuthenticationOptionsAsync(
        WebApplicationFactory<Program> host)
    {
        using HttpClient client = Client(host);
        return await GetAuthenticationOptionsAsync(client);
    }

    private static async Task<AuthenticationOptions> GetAuthenticationOptionsAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.GetAsync(OperatorSessionEndpoints.OptionsPath);
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement.Clone();
        return new AuthenticationOptions(
            root,
            Merge(
                new Dictionary<string, string>(StringComparer.Ordinal),
                response.Headers.GetValues("Set-Cookie")),
            root.GetProperty("antiforgery_token").GetString()!,
            root.GetProperty("antiforgery_header_name").GetString()!);
    }

    private static Task<HttpResponseMessage> SendPasswordAttemptAsync(
        HttpClient client,
        AuthenticationOptions options,
        string email,
        string password,
        string? returnTo = null) =>
        SendAsync(
            client,
            HttpMethod.Post,
            OperatorSessionEndpoints.PasswordLoginPath,
            options.Cookies,
            PasswordBody(email, password, returnTo),
            (options.AntiforgeryHeaderName, options.AntiforgeryToken));

    private static string PasswordBody(string email, string password, string? returnTo = null) =>
        JsonSerializer.Serialize(new { email, password, return_to = returnTo });

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
        string AntiforgeryHeaderName,
        string AntiforgeryFormFieldName,
        string? RedirectLocation);

    private sealed record AuthenticationOptions(
        JsonElement Body,
        Dictionary<string, string> Cookies,
        string AntiforgeryToken,
        string AntiforgeryHeaderName);
}
