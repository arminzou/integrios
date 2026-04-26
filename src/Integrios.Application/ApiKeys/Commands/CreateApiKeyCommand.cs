using System.Security.Cryptography;
using System.Text;
using Integrios.Application.Abstractions;
using Integrios.Domain.Common;
using Integrios.Domain.Tenants;
using MediatR;

namespace Integrios.Application.ApiKeys.Commands;

public sealed record CreateApiKeyCommand(
    Guid TenantId,
    string Name,
    IReadOnlyList<string>? Scopes,
    string? Description,
    DateTimeOffset? ExpiresAt
) : IRequest<CreateApiKeyResponse>;

public sealed class CreateApiKeyCommandHandler(IApiKeyRepository repository)
    : IRequestHandler<CreateApiKeyCommand, CreateApiKeyResponse>
{
    public async Task<CreateApiKeyResponse> Handle(CreateApiKeyCommand command, CancellationToken cancellationToken)
    {
        var publicKey = "pk_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var secretRaw = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var secret = "sk_" + secretRaw;
        var secretHash = "sha256:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();

        var apiKey = new ApiKey
        {
            Id = Guid.NewGuid(),
            TenantId = command.TenantId,
            Name = command.Name,
            PublicKey = publicKey,
            SecretHash = secretHash,
            Scopes = command.Scopes ?? ["events.write"],
            Status = OperationalStatus.Active,
            Description = command.Description,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = command.ExpiresAt,
        };

        ApiKey created = await repository.CreateAsync(apiKey, cancellationToken);
        return new CreateApiKeyResponse
        {
            Key = ApiKeyResponse.From(created),
            Secret = secret,
        };
    }
}
