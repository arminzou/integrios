namespace Integrios.Core.Domain.Integrations;

public enum IntegrationAuthScheme
{
    None = 0,
    ApiKeyHeader = 1,
    Basic = 2,
    BearerToken = 3,
    OAuthClientCredentials = 4,
    SecretReference = 5
}
