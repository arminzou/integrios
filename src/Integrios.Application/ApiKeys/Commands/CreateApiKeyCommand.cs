using System.Security.Cryptography;
using System.Text;
using Integrios.Domain.Common;
using Integrios.Domain.Tenants;
using MediatR;

namespace Integrios.Application.ApiKeys;

public sealed record CreateApiKeyCommand(
    Guid TenantId,
    string Name,
    string? Description,
    DateTimeOffset? ExpiresAt
) : IRequest<CreateApiKeyResult>;

internal sealed class CreateApiKeyCommandHandler(IApiKeyRepository repository)
    : IRequestHandler<CreateApiKeyCommand, CreateApiKeyResult>
{
    public async Task<CreateApiKeyResult> Handle(CreateApiKeyCommand command, CancellationToken cancellationToken)
    {
        var rawKey = "intg_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var keyPrefix = rawKey[..12]; // display hint: "intg_3f8a2c1d" (non-secret, first 12 chars)
        var keyHash = "sha256:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(rawKey))).ToLowerInvariant();

        var apiKey = new ApiKey
        {
            Id = Guid.NewGuid(),
            TenantId = command.TenantId,
            Name = command.Name,
            KeyPrefix = keyPrefix,
            KeyHash = keyHash,
            Status = OperationalStatus.Active,
            Description = command.Description,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = command.ExpiresAt,
        };

        ApiKey created = await repository.CreateAsync(apiKey, cancellationToken);
        return new CreateApiKeyResult
        {
            ApiKey = ApiKeyDto.From(created),
            Token = rawKey,
        };
    }
}
