using System.Collections.Concurrent;

namespace Dloizides.BackgroundWork.Hosting;

/// <summary>Observable state of one guarded worker.</summary>
/// <param name="Lease">Fully-qualified lease name.</param>
/// <param name="IsHeld">Whether this instance currently holds the lease.</param>
/// <param name="IsRunning">Whether the guarded inner service is currently started.</param>
/// <param name="LastTransitionUtc">When the held/running state last changed.</param>
public sealed record LeaseStatus(string Lease, bool IsHeld, bool IsRunning, DateTimeOffset LastTransitionUtc);

/// <summary>
/// Tracks which guarded workers are held and running, so a silently dead sweep is visible.
/// </summary>
/// <remarks>
/// This exists because "the worker quietly stopped weeks ago" is otherwise undetectable —
/// nothing errors, nothing restarts, the emails simply stop. A lease with no holder, or a
/// last-transition timestamp that stops advancing, is the signal.
/// </remarks>
public sealed class LeaseStateRegistry
{
    private readonly ConcurrentDictionary<string, LeaseStatus> _states = new(StringComparer.Ordinal);

    /// <summary>Records the current state of a guarded worker.</summary>
    public void Report(string lease, bool isHeld, bool isRunning, TimeProvider timeProvider)
    {
        _states.AddOrUpdate(
            lease,
            _ => new LeaseStatus(lease, isHeld, isRunning, timeProvider.GetUtcNow()),
            (_, existing) => existing.IsHeld == isHeld && existing.IsRunning == isRunning
                ? existing
                : new LeaseStatus(lease, isHeld, isRunning, timeProvider.GetUtcNow()));
    }

    /// <summary>Removes a worker from the registry — used when a role means it never runs here.</summary>
    public void Forget(string lease) => _states.TryRemove(lease, out _);

    /// <summary>Snapshot of every tracked worker.</summary>
    public IReadOnlyCollection<LeaseStatus> Snapshot() => _states.Values.ToArray();
}
