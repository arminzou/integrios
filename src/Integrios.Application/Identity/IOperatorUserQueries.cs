namespace Integrios.Application.Identity;

public interface IOperatorUserQueries
{
    Task<IReadOnlyList<OperatorUserCredentialDto>> ListAsync(CancellationToken cancellationToken);
}

public sealed record OperatorUserCredentialDto(
    Guid UserId,
    string DisplayName,
    string? DescriptiveEmail,
    Guid? PasswordCredentialId,
    string? PasswordEmail,
    int? SessionRevision,
    bool PasswordEnabled);
