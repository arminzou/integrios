using System.Data.Common;
using Dapper;
using Integrios.Application;
using Integrios.Application.Identity;
using Integrios.Infrastructure;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Integrios.FunctionalTests.Admin;

public sealed class OperatorUserLifecycleTests : IClassFixture<AdminApiFixture>, IAsyncLifetime
{
    private readonly AdminApiFixture fixture;
    private ServiceProvider provider = null!;
    private ISender sender = null!;

    public OperatorUserLifecycleTests(AdminApiFixture fixture)
    {
        this.fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        provider = new ServiceCollection()
            .AddAdminApplicationServices()
            .AddAdminInfrastructureServices(fixture.Configuration)
            .BuildServiceProvider();
        sender = provider.GetRequiredService<ISender>();
    }

    public Task DisposeAsync()
    {
        provider.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task PasswordCredentialLifecycle_PreservesIdentityAndRevisionRules()
    {
        PasswordCredentialMutationResult created = await sender.Send(
            new CreateOperatorUserCommand("Password Operator", "  Operator@Example.com  ", "hash-1"));
        created.Status.ShouldBe(PasswordCredentialMutationStatus.Succeeded);

        PasswordCredentialRow initial = await ReadCredentialAsync(created.UserId);
        initial.Email.ShouldBe("Operator@Example.com");
        initial.NormalizedEmail.ShouldBe("OPERATOR@EXAMPLE.COM");
        initial.SessionRevision.ShouldBe(1);
        initial.PasswordHash.ShouldBe("hash-1");
        initial.DisabledAt.ShouldBeNull();
        (await ReadUserEmailAsync(created.UserId)).ShouldBeNull();

        PasswordCredentialMutationResult duplicate = await sender.Send(
            new CreateOperatorUserCommand("Another Operator", "operator@example.COM", "hash-2"));
        duplicate.Status.ShouldBe(PasswordCredentialMutationStatus.EmailConflict);
        (await CountUsersAsync()).ShouldBe(1);

        PasswordCredentialMutationResult other = await sender.Send(
            new CreateOperatorUserCommand("Other Operator", "other@example.com", "hash-other"));
        other.Status.ShouldBe(PasswordCredentialMutationStatus.Succeeded);
        PasswordCredentialMutationResult conflictingChange = await sender.Send(
            new ChangeOperatorUserPasswordEmailCommand(created.UserId, " OTHER@example.com "));
        conflictingChange.Status.ShouldBe(PasswordCredentialMutationStatus.EmailConflict);
        (await ReadCredentialAsync(created.UserId)).Email.ShouldBe(initial.Email);

        PasswordCredentialMutationResult resetWithEmail = await sender.Send(
            new SetOperatorUserPasswordCommand(created.UserId, "new@example.com", "hash-2"));
        resetWithEmail.Status.ShouldBe(PasswordCredentialMutationStatus.EmailNotAllowed);

        PasswordCredentialMutationResult reset = await sender.Send(
            new SetOperatorUserPasswordCommand(created.UserId, null, "hash-2"));
        reset.Status.ShouldBe(PasswordCredentialMutationStatus.Succeeded);
        PasswordCredentialRow afterReset = await ReadCredentialAsync(created.UserId);
        afterReset.SessionRevision.ShouldBe(2);
        afterReset.PasswordHash.ShouldBe("hash-2");
        afterReset.Email.ShouldBe(initial.Email);

        PasswordCredentialMutationResult emailChanged = await sender.Send(
            new ChangeOperatorUserPasswordEmailCommand(created.UserId, "changed@example.com"));
        emailChanged.Status.ShouldBe(PasswordCredentialMutationStatus.Succeeded);
        PasswordCredentialRow afterEmailChange = await ReadCredentialAsync(created.UserId);
        afterEmailChange.SessionRevision.ShouldBe(2);
        afterEmailChange.Email.ShouldBe("changed@example.com");

        PasswordCredentialMutationResult disabled = await sender.Send(
            new DisableOperatorUserPasswordCommand(created.UserId));
        disabled.Status.ShouldBe(PasswordCredentialMutationStatus.Succeeded);
        PasswordCredentialRow afterDisable = await ReadCredentialAsync(created.UserId);
        afterDisable.SessionRevision.ShouldBe(3);
        afterDisable.DisabledAt.ShouldNotBeNull();

        (await sender.Send(new DisableOperatorUserPasswordCommand(created.UserId))).Status
            .ShouldBe(PasswordCredentialMutationStatus.Succeeded);
        (await ReadCredentialAsync(created.UserId)).SessionRevision.ShouldBe(3);

        (await sender.Send(new SetOperatorUserPasswordCommand(created.UserId, null, "hash-3"))).Status
            .ShouldBe(PasswordCredentialMutationStatus.Succeeded);
        PasswordCredentialRow reenabled = await ReadCredentialAsync(created.UserId);
        reenabled.SessionRevision.ShouldBe(4);
        reenabled.DisabledAt.ShouldBeNull();
    }

    [Fact]
    public async Task PasswordCredential_AttachesOnlyByExactUserId_AndListsSafeState()
    {
        Guid userId = Guid.NewGuid();
        Guid otherUserId = Guid.NewGuid();
        await InsertUserAsync(userId, "OIDC Operator", "shared@example.com");
        await InsertUserAsync(otherUserId, "Other OIDC Operator", "shared@example.com");

        PasswordCredentialMutationResult missing = await sender.Send(
            new SetOperatorUserPasswordCommand(Guid.NewGuid(), "password@example.com", "hash"));
        missing.Status.ShouldBe(PasswordCredentialMutationStatus.UserNotFound);

        PasswordCredentialMutationResult attached = await sender.Send(
            new SetOperatorUserPasswordCommand(userId, "password@example.com", "hash"));
        attached.Status.ShouldBe(PasswordCredentialMutationStatus.Succeeded);

        IReadOnlyList<OperatorUserCredentialDto> users = await sender.Send(new ListOperatorUsersQuery());
        OperatorUserCredentialDto selected = users.Single(user => user.UserId == userId);
        selected.DescriptiveEmail.ShouldBe("shared@example.com");
        selected.PasswordEmail.ShouldBe("password@example.com");
        selected.PasswordEnabled.ShouldBeTrue();
        users.Single(user => user.UserId == otherUserId).PasswordCredentialId.ShouldBeNull();

        await Should.ThrowAsync<DbException>(() => InsertDuplicateCredentialAsync(userId));
    }

    private async Task InsertUserAsync(Guid id, string displayName, string email)
    {
        await using DbConnection connection = fixture.CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            "INSERT INTO users (id, display_name, email) VALUES (@Id, @DisplayName, @Email)",
            new { Id = id, DisplayName = displayName, Email = email });
    }

    private async Task InsertDuplicateCredentialAsync(Guid userId)
    {
        await using DbConnection connection = fixture.CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO password_credentials
                (id, user_id, email, normalized_email, password_hash, session_revision)
            VALUES (@Id, @UserId, @Email, @NormalizedEmail, @PasswordHash, 1)
            """,
            new
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Email = "another@example.com",
                NormalizedEmail = "ANOTHER@EXAMPLE.COM",
                PasswordHash = "hash",
            });
    }

    private async Task<PasswordCredentialRow> ReadCredentialAsync(Guid userId)
    {
        await using DbConnection connection = fixture.CreateConnection();
        await connection.OpenAsync();
        return await connection.QuerySingleAsync<PasswordCredentialRow>(
            """
            SELECT email, normalized_email, password_hash, session_revision, disabled_at
            FROM password_credentials
            WHERE user_id = @UserId
            """,
            new { UserId = userId });
    }

    private async Task<string?> ReadUserEmailAsync(Guid userId)
    {
        await using DbConnection connection = fixture.CreateConnection();
        await connection.OpenAsync();
        return await connection.QuerySingleAsync<string?>(
            "SELECT email FROM users WHERE id = @UserId",
            new { UserId = userId });
    }

    private async Task<long> CountUsersAsync()
    {
        await using DbConnection connection = fixture.CreateConnection();
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM users");
    }

    private sealed record PasswordCredentialRow
    {
        public string Email { get; init; } = string.Empty;
        public string NormalizedEmail { get; init; } = string.Empty;
        public string PasswordHash { get; init; } = string.Empty;
        public int SessionRevision { get; init; }
        public object? DisabledAt { get; init; }
    }
}
