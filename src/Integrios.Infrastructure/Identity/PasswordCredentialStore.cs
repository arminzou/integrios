using Integrios.Application.Identity;
using Integrios.Domain.Entities;
using Integrios.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Integrios.Infrastructure.Identity;

internal sealed class PasswordCredentialStore(IntegriosDbContext context)
    : IPasswordCredentialLifecycle, IOperatorUserQueries, IPasswordAuthenticationStore
{
    public Task<PasswordAuthenticationCredential?> FindEnabledAsync(
        string normalizedEmail,
        CancellationToken cancellationToken) =>
        (from credential in context.PasswordCredentials.AsNoTracking()
         join user in context.Users.AsNoTracking() on credential.UserId equals user.Id
         where credential.NormalizedEmail == normalizedEmail && credential.DisabledAt == null
         select new PasswordAuthenticationCredential(
             credential.Id,
             user.Id,
             user.DisplayName,
             credential.PasswordHash,
             credential.SessionRevision))
        .SingleOrDefaultAsync(cancellationToken);

    public Task<bool> IsCurrentSessionAsync(
        Guid credentialId,
        Guid userId,
        int sessionRevision,
        CancellationToken cancellationToken) =>
        context.PasswordCredentials.AsNoTracking().AnyAsync(
            credential => credential.Id == credentialId
                && credential.UserId == userId
                && credential.SessionRevision == sessionRevision
                && credential.DisabledAt == null,
            cancellationToken);

    public async Task RecordSuccessfulSignInAsync(Guid userId, CancellationToken cancellationToken) =>
        await context.Users
            .Where(user => user.Id == userId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(user => user.LastSignedInAt, DateTimeOffset.UtcNow),
                cancellationToken);

    public async Task ReplaceHashAsync(
        Guid credentialId,
        string currentHash,
        string replacementHash,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken) =>
        await context.PasswordCredentials
            .Where(credential => credential.Id == credentialId
                && credential.PasswordHash == currentHash
                && credential.DisabledAt == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(credential => credential.PasswordHash, replacementHash)
                    .SetProperty(credential => credential.UpdatedAt, changedAt),
                cancellationToken);

    public async Task<IReadOnlyList<OperatorUserCredentialDto>> ListAsync(
        CancellationToken cancellationToken) =>
        await (from user in context.Users.AsNoTracking()
               join credential in context.PasswordCredentials.AsNoTracking()
                   on user.Id equals credential.UserId into credentials
               from credential in credentials.DefaultIfEmpty()
               orderby user.DisplayName, user.Id
               select new OperatorUserCredentialDto(
                   user.Id,
                   user.DisplayName,
                   user.Email,
                   credential == null ? null : credential.Id,
                   credential == null ? null : credential.Email,
                   credential == null ? null : credential.SessionRevision,
                   credential != null && credential.DisabledAt == null))
            .ToListAsync(cancellationToken);

    public async Task<PasswordCredentialMutationStatus> CreateAsync(
        User user,
        PasswordCredential credential,
        CancellationToken cancellationToken)
    {
        context.Users.Add(user);
        context.PasswordCredentials.Add(credential);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return PasswordCredentialMutationStatus.Succeeded;
        }
        catch (Exception exception) when (IsUniqueViolation(
            exception, "uq_password_credentials_normalized_email"))
        {
            DetachChanges();
            return PasswordCredentialMutationStatus.EmailConflict;
        }
    }

    public async Task<PasswordCredentialMutationStatus> SetPasswordAsync(
        Guid userId,
        string? email,
        string? normalizedEmail,
        string passwordHash,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken)
    {
        if (email is null || normalizedEmail is null)
        {
            int updated = await context.PasswordCredentials
                .Where(credential => credential.UserId == userId)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(credential => credential.PasswordHash, passwordHash)
                        .SetProperty(credential => credential.SessionRevision, credential => credential.SessionRevision + 1)
                        .SetProperty(credential => credential.UpdatedAt, changedAt)
                        .SetProperty(credential => credential.DisabledAt, (DateTimeOffset?)null),
                    cancellationToken);
            if (updated == 1)
                return PasswordCredentialMutationStatus.Succeeded;

            return await context.Users.AnyAsync(user => user.Id == userId, cancellationToken)
                ? PasswordCredentialMutationStatus.EmailRequired
                : PasswordCredentialMutationStatus.UserNotFound;
        }

        if (await context.PasswordCredentials.AnyAsync(
            credential => credential.UserId == userId,
            cancellationToken))
        {
            return PasswordCredentialMutationStatus.EmailNotAllowed;
        }
        if (!await context.Users.AnyAsync(user => user.Id == userId, cancellationToken))
            return PasswordCredentialMutationStatus.UserNotFound;

        var credential = new PasswordCredential
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Email = email,
            NormalizedEmail = normalizedEmail,
            PasswordHash = passwordHash,
            SessionRevision = 1,
            CreatedAt = changedAt,
            UpdatedAt = changedAt,
            DisabledAt = null,
        };
        context.PasswordCredentials.Add(credential);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return PasswordCredentialMutationStatus.Succeeded;
        }
        catch (Exception exception) when (IsUniqueViolation(
            exception, "uq_password_credentials_normalized_email"))
        {
            DetachChanges();
            return PasswordCredentialMutationStatus.EmailConflict;
        }
        catch (Exception exception) when (IsUniqueViolation(
            exception, "uq_password_credentials_user_id"))
        {
            DetachChanges();
            return PasswordCredentialMutationStatus.ConcurrentConflict;
        }
    }

    public async Task<PasswordCredentialMutationStatus> ChangeEmailAsync(
        Guid userId,
        string email,
        string normalizedEmail,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken)
    {
        try
        {
            int updated = await context.PasswordCredentials
                .Where(credential => credential.UserId == userId)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(credential => credential.Email, email)
                        .SetProperty(credential => credential.NormalizedEmail, normalizedEmail)
                        .SetProperty(credential => credential.UpdatedAt, changedAt),
                    cancellationToken);

            return updated == 1
                ? PasswordCredentialMutationStatus.Succeeded
                : PasswordCredentialMutationStatus.CredentialNotFound;
        }
        catch (Exception exception) when (IsUniqueViolation(
            exception, "uq_password_credentials_normalized_email"))
        {
            return PasswordCredentialMutationStatus.EmailConflict;
        }
    }

    public async Task<PasswordCredentialMutationStatus> DisableAsync(
        Guid userId,
        DateTimeOffset disabledAt,
        CancellationToken cancellationToken)
    {
        int updated = await context.PasswordCredentials
            .Where(credential => credential.UserId == userId && credential.DisabledAt == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(credential => credential.SessionRevision, credential => credential.SessionRevision + 1)
                    .SetProperty(credential => credential.UpdatedAt, disabledAt)
                    .SetProperty(credential => credential.DisabledAt, disabledAt),
                cancellationToken);

        if (updated == 1)
            return PasswordCredentialMutationStatus.Succeeded;

        return await context.PasswordCredentials.AnyAsync(
            credential => credential.UserId == userId,
            cancellationToken)
            ? PasswordCredentialMutationStatus.Succeeded
            : PasswordCredentialMutationStatus.CredentialNotFound;
    }

    private static bool IsUniqueViolation(Exception exception, string constraintName) =>
        exception is DbUpdateException { InnerException: Exception innerException }
            ? IsUniqueViolation(innerException, constraintName)
            : exception is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: var postgresConstraint,
        } && postgresConstraint == constraintName
        || exception is SqlException { Number: 2601 or 2627 } sqlException
        && sqlException.Message.Contains(constraintName, StringComparison.Ordinal);

    private void DetachChanges()
    {
        foreach (var entry in context.ChangeTracker.Entries().ToList())
            entry.State = EntityState.Detached;
    }
}
