using Dloizides.BackgroundWork.Leasing;
using Microsoft.Extensions.Hosting;

namespace Dloizides.BackgroundWork.Tests;

/// <summary>Controllable lease for exercising the supervision loop without a database.</summary>
internal sealed class FakeWorkerLease : IWorkerLease
{
    public string Name => "test.lease";

    public bool IsHeld { get; private set; }

    /// <summary>When false, <see cref="TryAcquireAsync"/> reports the lease as taken elsewhere.</summary>
    public bool CanAcquire { get; set; } = true;

    /// <summary>When false, the holder's re-verification fails — simulating a dead session.</summary>
    public bool StillHeld { get; set; } = true;

    public int ReleaseCount { get; private set; }

    public Task<bool> TryAcquireAsync(CancellationToken cancellationToken)
    {
        if (CanAcquire)
        {
            IsHeld = true;
        }

        return Task.FromResult(CanAcquire);
    }

    public Task<bool> IsStillHeldAsync(CancellationToken cancellationToken)
    {
        if (!StillHeld)
        {
            IsHeld = false;
        }

        return Task.FromResult(StillHeld);
    }

    public Task ReleaseAsync(CancellationToken cancellationToken)
    {
        ReleaseCount++;
        IsHeld = false;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Records whether the guarded work was actually started and stopped.</summary>
internal sealed class SpyHostedService : IHostedService
{
    public int StartCount { get; private set; }

    public int StopCount { get; private set; }

    public bool IsRunning => StartCount > StopCount;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        StartCount++;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        StopCount++;
        return Task.CompletedTask;
    }
}
