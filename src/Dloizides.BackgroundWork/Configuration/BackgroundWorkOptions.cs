namespace Dloizides.BackgroundWork.Configuration;

/// <summary>
/// Configuration for role-aware, lease-guarded background work.
/// Bound from the <c>BackgroundWork</c> configuration section.
/// </summary>
public sealed class BackgroundWorkOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "BackgroundWork";

    /// <summary>
    /// Which job this process performs. Defaults to <see cref="WorkerRole.All"/> so
    /// adoption changes nothing until a service explicitly opts into the split.
    /// </summary>
    public WorkerRole Role { get; set; } = WorkerRole.All;

    /// <summary>
    /// Postgres connection string used SOLELY for advisory locks.
    /// <para>
    /// This must be usable to open a DEDICATED, long-lived connection. It is deliberately
    /// separate from the application's EF Core connection: an advisory lock lives on a
    /// SESSION, so taking one on a pooled connection silently releases it the moment that
    /// connection returns to the pool.
    /// </para>
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Namespace for lease keys, so two services sharing one database cannot collide.
    /// Advisory lock ids are global per DATABASE, not per table or schema.
    /// </summary>
    public string KeyPrefix { get; set; } = string.Empty;

    /// <summary>
    /// How often the holder re-verifies that its lease connection is still alive, and
    /// how often a non-holder retries acquisition. Shorter = smaller window in which a
    /// dropped connection has released the lock while the worker is still running.
    /// </summary>
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Whether lease acquisition is required at all. When false the wrapper starts the
    /// inner service unconditionally — intended ONLY for local development and tests,
    /// never for a deployed multi-replica service.
    /// </summary>
    public bool LeaseEnabled { get; set; } = true;

    /// <summary>Throws when the options cannot support the configured behaviour.</summary>
    public void Validate()
    {
        var leaseNeeded = LeaseEnabled && Role != WorkerRole.Api;
        if (leaseNeeded && string.IsNullOrWhiteSpace(ConnectionString))
        {
            throw new InvalidOperationException(
                $"{SectionName}.{nameof(ConnectionString)} is required when {nameof(Role)}={Role} "
                + $"and {nameof(LeaseEnabled)}=true. Background sweeps refuse to run without a lease "
                + "(fail-closed) because concurrent execution of a delete/expiry sweep corrupts data.");
        }

        if (CheckInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{SectionName}.{nameof(CheckInterval)} must be greater than zero.");
        }
    }
}
