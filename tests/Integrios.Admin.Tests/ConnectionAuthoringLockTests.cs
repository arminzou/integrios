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
    public async Task Dispose_ReleasesLockForWaitingAcquisition()
    {
        using IServiceScope scope = fixture.WebFactory.Services.CreateScope();
        var authoringLock = scope.ServiceProvider.GetRequiredService<IConnectionAuthoringLock>();
        Guid connectionId = Guid.NewGuid();

        IAsyncDisposable firstLease = await authoringLock.AcquireAsync([connectionId]);
        Task<IAsyncDisposable> waitingAcquisition = authoringLock.AcquireAsync([connectionId]);

        await Task.Delay(100);
        Assert.False(waitingAcquisition.IsCompleted);

        await firstLease.DisposeAsync();
        IAsyncDisposable secondLease = await waitingAcquisition.WaitAsync(TimeSpan.FromSeconds(5));
        await secondLease.DisposeAsync();
    }
}
