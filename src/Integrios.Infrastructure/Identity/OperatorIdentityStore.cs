using Integrios.Application.Identity;
using Integrios.Domain.Entities;
using Integrios.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Integrios.Infrastructure.Identity;

internal sealed class OperatorIdentityStore(IntegriosDbContext context) : IOperatorIdentityStore
{
    public async Task<User> ResolveAsync(
        string issuer,
        string subject,
        OperatorIdentityClaims claims,
        CancellationToken cancellationToken)
    {
        User? existing = await FindByPairAsync(issuer, subject, cancellationToken);
        if (existing is not null)
            return await TouchAsync(existing, claims, cancellationToken);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var user = new User
        {
            Id = Guid.NewGuid(),
            DisplayName = DisplayNameFor(claims, subject),
            Email = claims.Email,
            CreatedAt = now,
            LastSignedInAt = now,
        };
        context.Users.Add(user);
        context.OperatorIdentities.Add(new OperatorIdentity
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Issuer = issuer,
            Subject = subject,
            CreatedAt = now,
        });

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return user;
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }
            || ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            // Another concurrent first sign-in won the race for this pair. The unique constraint,
            // not a lock, is what guarantees one User; read the winner back instead of retrying.
            foreach (var entry in context.ChangeTracker.Entries().ToList())
                entry.State = EntityState.Detached;

            return await FindByPairAsync(issuer, subject, cancellationToken)
                ?? throw new InvalidOperationException(
                    "The Operator identity conflicted on insert but could not be read back.", ex);
        }
    }

    public Task<User?> FindByIdAsync(Guid userId, CancellationToken cancellationToken) =>
        context.Users.AsNoTracking().SingleOrDefaultAsync(user => user.Id == userId, cancellationToken);

    private Task<User?> FindByPairAsync(string issuer, string subject, CancellationToken cancellationToken) =>
        (from identity in context.OperatorIdentities.AsNoTracking()
         join user in context.Users.AsNoTracking() on identity.UserId equals user.Id
         where identity.Issuer == issuer && identity.Subject == subject
         select user).SingleOrDefaultAsync(cancellationToken);

    private async Task<User> TouchAsync(
        User user,
        OperatorIdentityClaims claims,
        CancellationToken cancellationToken)
    {
        User updated = user with
        {
            DisplayName = DisplayNameFor(claims, user.DisplayName),
            Email = claims.Email ?? user.Email,
            LastSignedInAt = DateTimeOffset.UtcNow,
        };

        await context.Users
            .Where(candidate => candidate.Id == user.Id)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(candidate => candidate.DisplayName, updated.DisplayName)
                    .SetProperty(candidate => candidate.Email, updated.Email)
                    .SetProperty(candidate => candidate.LastSignedInAt, updated.LastSignedInAt),
                cancellationToken);

        return updated;
    }

    private static string DisplayNameFor(OperatorIdentityClaims claims, string fallback) =>
        string.IsNullOrWhiteSpace(claims.DisplayName) ? fallback : claims.DisplayName;
}
