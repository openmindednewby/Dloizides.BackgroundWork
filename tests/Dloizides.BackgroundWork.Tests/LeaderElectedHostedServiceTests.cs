using Dloizides.BackgroundWork.Configuration;
using Dloizides.BackgroundWork.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Dloizides.BackgroundWork.Tests;

/// <summary>
/// Behaviour of the guard itself. These assert that the work does NOT run, which is the
/// property that matters — the guarded sweeps delete data and expire live signups.
/// </summary>
public sealed class LeaderElectedHostedServiceTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(20);

    private static LeaderElectedHostedService Create(
        SpyHostedService inner,
        FakeWorkerLease? lease,
        WorkerRole role,
        bool leaseEnabled = true)
    {
        var options = new BackgroundWorkOptions
        {
            Role = role,
            LeaseEnabled = leaseEnabled,
            ConnectionString = "Host=x;Database=y;Username=z",
            CheckInterval = Tick
        };

        return new LeaderElectedHostedService(
            inner,
            lease,
            "test.lease",
            options,
            new LeaseStateRegistry(),
            TimeProvider.System,
            NullLogger.Instance);
    }

    private static async Task<bool> EventuallyAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100; i++)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return condition();
    }

    [Fact]
    public async Task Role_Api_never_starts_the_guarded_work()
    {
        var inner = new SpyHostedService();
        var lease = new FakeWorkerLease();
        var sut = Create(inner, lease, WorkerRole.Api);

        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(100);

        inner.StartCount.ShouldBe(0);
        lease.IsHeld.ShouldBeFalse();

        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Work_does_not_start_while_another_instance_holds_the_lease()
    {
        var inner = new SpyHostedService();
        var lease = new FakeWorkerLease { CanAcquire = false };
        var sut = Create(inner, lease, WorkerRole.Worker);

        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(150);

        inner.StartCount.ShouldBe(0);

        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Work_starts_once_the_lease_is_acquired()
    {
        var inner = new SpyHostedService();
        var lease = new FakeWorkerLease { CanAcquire = true };
        var sut = Create(inner, lease, WorkerRole.Worker);

        await sut.StartAsync(CancellationToken.None);

        (await EventuallyAsync(() => inner.IsRunning)).ShouldBeTrue();
        inner.StartCount.ShouldBe(1);

        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Losing_the_lease_stops_the_guarded_work()
    {
        // The critical case: the lease session died, so Postgres has already released the
        // lock and another instance may take it. Continuing to run would mean two copies of
        // a delete sweep executing at once.
        var inner = new SpyHostedService();
        var lease = new FakeWorkerLease { CanAcquire = true, StillHeld = true };
        var sut = Create(inner, lease, WorkerRole.Worker);

        await sut.StartAsync(CancellationToken.None);
        (await EventuallyAsync(() => inner.IsRunning)).ShouldBeTrue();

        lease.CanAcquire = false;
        lease.StillHeld = false;

        (await EventuallyAsync(() => !inner.IsRunning)).ShouldBeTrue();
        inner.StopCount.ShouldBe(1);

        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Work_resumes_after_the_lease_is_reacquired()
    {
        var inner = new SpyHostedService();
        var lease = new FakeWorkerLease { CanAcquire = false };
        var sut = Create(inner, lease, WorkerRole.Worker);

        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(80);
        inner.StartCount.ShouldBe(0);

        lease.CanAcquire = true;

        (await EventuallyAsync(() => inner.IsRunning)).ShouldBeTrue();

        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Stopping_releases_the_lease()
    {
        var inner = new SpyHostedService();
        var lease = new FakeWorkerLease();
        var sut = Create(inner, lease, WorkerRole.Worker);

        await sut.StartAsync(CancellationToken.None);
        (await EventuallyAsync(() => inner.IsRunning)).ShouldBeTrue();

        await sut.StopAsync(CancellationToken.None);

        inner.StopCount.ShouldBe(1);
        lease.ReleaseCount.ShouldBeGreaterThanOrEqualTo(1);
        lease.IsHeld.ShouldBeFalse();
    }

    [Fact]
    public async Task Role_All_runs_the_work_the_same_as_Worker()
    {
        // The default role — adopting the package must not change behaviour for a service
        // that has not been split, and must keep a self-hosting customer on one container.
        var inner = new SpyHostedService();
        var lease = new FakeWorkerLease();
        var sut = Create(inner, lease, WorkerRole.All);

        await sut.StartAsync(CancellationToken.None);

        (await EventuallyAsync(() => inner.IsRunning)).ShouldBeTrue();

        await sut.StopAsync(CancellationToken.None);
    }
}
