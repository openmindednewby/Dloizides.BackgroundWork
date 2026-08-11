namespace Dloizides.BackgroundWork.Leasing;

/// <summary>
/// An exclusive, self-releasing lease over a named piece of singleton work.
/// </summary>
/// <remarks>
/// Implementations MUST fail closed: any uncertainty about whether the lease is still
/// held has to be reported as NOT held, because the callers guard sweeps that delete and
/// expire real customer data.
/// </remarks>
public interface IWorkerLease : IAsyncDisposable
{
    /// <summary>The fully-qualified lease name, for logs and metrics.</summary>
    string Name { get; }

    /// <summary>True while this instance believes it holds the lease.</summary>
    bool IsHeld { get; }

    /// <summary>
    /// Attempts to take the lease. Returns false immediately if another instance holds it —
    /// this never blocks waiting for the lock.
    /// </summary>
    Task<bool> TryAcquireAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Re-verifies that the lease is genuinely still held, by proving the owning session is
    /// alive. Returns false if the connection died — in which case Postgres has already
    /// released the lock and the caller MUST stop the guarded work.
    /// </summary>
    Task<bool> IsStillHeldAsync(CancellationToken cancellationToken);

    /// <summary>Releases the lease if held. Safe to call when not held.</summary>
    Task ReleaseAsync(CancellationToken cancellationToken);
}
