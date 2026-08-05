using System.Buffers.Binary;
using System.Security.Cryptography;
using Integrios.Application.Connections;
using Microsoft.Extensions.DependencyInjection;

namespace Integrios.Admin.Tests;

public sealed class ConnectionAuthoringLockTests : IClassFixture<AdminApiFixture>
{
    private readonly AdminApiFixture fixture;

    public ConnectionAuthoringLockTests(AdminApiFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    public async Task ContendedAcquisition_ReturnsBoundedConflictAndDisposeReleasesLock()
    {
        using IServiceScope scope = fixture.WebFactory.Services.CreateScope();
        var authoringLock = scope.ServiceProvider.GetRequiredService<IConnectionAuthoringLock>();
        Guid connectionId = Guid.NewGuid();

        IAsyncDisposable firstLease = await authoringLock.AcquireAsync([connectionId], CancellationToken.None);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<ConnectionAuthoringConflictException>(
            () => authoringLock.AcquireAsync([connectionId], CancellationToken.None));
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));

        await firstLease.DisposeAsync();
        IAsyncDisposable secondLease = await authoringLock.AcquireAsync([connectionId], CancellationToken.None);
        await secondLease.DisposeAsync();
    }

    [Fact]
    public async Task ContendedAcquisition_HonorsCallerCancellationPromptly()
    {
        using IServiceScope scope = fixture.WebFactory.Services.CreateScope();
        var authoringLock = scope.ServiceProvider.GetRequiredService<IConnectionAuthoringLock>();
        Guid connectionId = Guid.NewGuid();

        await using IAsyncDisposable firstLease = await authoringLock.AcquireAsync([connectionId], CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => authoringLock.AcquireAsync([connectionId], cancellation.Token));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task MultiKeyConflict_ReleasesPartiallyAcquiredKeys()
    {
        using IServiceScope scope = fixture.WebFactory.Services.CreateScope();
        var authoringLock = scope.ServiceProvider.GetRequiredService<IConnectionAuthoringLock>();
        Guid[] ids = [Guid.NewGuid(), Guid.NewGuid()];
        Array.Sort(ids, (left, right) => AdvisoryKey(left).CompareTo(AdvisoryKey(right)));
        Guid firstId = ids[0];
        Guid secondId = ids[1];

        await using IAsyncDisposable heldLease = await authoringLock.AcquireAsync([secondId], CancellationToken.None);
        await Assert.ThrowsAsync<ConnectionAuthoringConflictException>(
            () => authoringLock.AcquireAsync([firstId, secondId], CancellationToken.None));

        await using IAsyncDisposable firstOnlyLease = await authoringLock.AcquireAsync([firstId], CancellationToken.None);
    }

    private static long AdvisoryKey(Guid id)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(id.ToByteArray(), hash);
        return BinaryPrimitives.ReadInt64BigEndian(hash);
    }
}
