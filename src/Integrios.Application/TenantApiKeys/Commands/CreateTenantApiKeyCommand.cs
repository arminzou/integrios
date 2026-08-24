using System.Security.Cryptography;
using System.Text;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using MediatR;

namespace Integrios.Application.TenantApiKeys;

public sealed record CreateTenantApiKeyCommand(
    Guid TenantId,
    string Name,
    string? Description,
    DateTimeOffset? ExpiresAt
) : IRequest<CreateTenantApiKeyResult>;

internal sealed class CreateTenantApiKeyCommandHandler(ITenantApiKeyRepository repository)
    : IRequestHandler<CreateTenantApiKeyCommand, CreateTenantApiKeyResult>
{
    public async Task<CreateTenantApiKeyResult> Handle(CreateTenantApiKeyCommand command, CancellationToken cancellationToken)
    {
        var rawKey = "intg_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var keyPrefix = rawKey[..12]; // display hint: "intg_3f8a2c1d" (non-secret, first 12 chars)
        var keyHash = "sha256:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(rawKey))).ToLowerInvariant();

        var tenantApiKey = new TenantApiKey
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

        TenantApiKey created = await repository.CreateAsync(tenantApiKey, cancellationToken);
        return new CreateTenantApiKeyResult
        {
            TenantApiKey = TenantApiKeyDto.From(created),
            Token = rawKey,
        };
    }
}
