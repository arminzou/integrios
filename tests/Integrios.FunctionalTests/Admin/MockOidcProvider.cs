using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Integrios.FunctionalTests.Admin;

/// A real OpenID Connect provider in a container, configured to complete the authorization-code
/// flow without an interactive login page so the redirect, callback, provisioning, logout, and
/// failure paths are exercised for real rather than against a stub of our own.
///
/// It serves two issuers whose subjects are pinned and whose email is deliberately shared, so a
/// test can prove that email equality never links two Operator identities.
internal sealed class MockOidcProvider : IAsyncDisposable
{
    public const string AliceIssuerId = "alice";
    public const string BobIssuerId = "bob";
    public const string AliceSubject = "alice-subject";
    public const string BobSubject = "bob-subject";
    public const string AliceDisplayName = "Alice Operator";
    public const string BobDisplayName = "Bob Operator";
    public const string SharedEmail = "shared@example.test";
    public const string ClientId = "integrios-admin";
    public const string ClientSecret = "integrios-admin-secret";

    private const int ProviderPort = 8080;

    private static readonly string JsonConfig = $$"""
        {
          "interactiveLogin": false,
          "tokenCallbacks": [
            {
              "issuerId": "{{AliceIssuerId}}",
              "requestMappings": [
                {
                  "requestParam": "grant_type",
                  "match": "*",
                  "claims": {
                    "sub": "{{AliceSubject}}",
                    "email": "{{SharedEmail}}",
                    "name": "{{AliceDisplayName}}"
                  }
                }
              ]
            },
            {
              "issuerId": "{{BobIssuerId}}",
              "requestMappings": [
                {
                  "requestParam": "grant_type",
                  "match": "*",
                  "claims": {
                    "sub": "{{BobSubject}}",
                    "email": "{{SharedEmail}}",
                    "name": "{{BobDisplayName}}"
                  }
                }
              ]
            }
          ]
        }
        """;

    private readonly IContainer container = new ContainerBuilder()
        .WithImage("ghcr.io/navikt/mock-oauth2-server:2.1.10")
        .WithPortBinding(ProviderPort, assignRandomHostPort: true)
        .WithEnvironment("JSON_CONFIG", JsonConfig)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request
            .ForPort(ProviderPort)
            .ForPath($"/{AliceIssuerId}/.well-known/openid-configuration")))
        .Build();

    public string BaseAddress => $"http://{container.Hostname}:{container.GetMappedPublicPort(ProviderPort)}";

    public string Authority(string issuerId) => $"{BaseAddress}/{issuerId}";

    public Task StartAsync() => container.StartAsync();

    public async ValueTask DisposeAsync() => await container.DisposeAsync();
}
