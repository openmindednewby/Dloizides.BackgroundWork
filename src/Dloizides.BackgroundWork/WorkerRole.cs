namespace Dloizides.BackgroundWork;

/// <summary>
/// Which job a process instance performs. One container image, two deployments.
/// </summary>
/// <remarks>
/// <para>
/// The default is <see cref="All"/> so that adopting this package is a NO-OP for a
/// service that has not been split yet, and so that a customer SELF-HOSTING the
/// service keeps running a single container. Splitting must always be opt-in.
/// </para>
/// </remarks>
public enum WorkerRole
{
    /// <summary>
    /// HTTP only. Registered background sweeps do NOT run. Safe to scale horizontally.
    /// </summary>
    Api = 0,

    /// <summary>
    /// Background sweeps only. Intended to run at a single replica, though the lease
    /// makes correctness independent of that.
    /// </summary>
    Worker = 1,

    /// <summary>
    /// Both — the pre-split behaviour and the default. A single process serves HTTP
    /// and runs the sweeps.
    /// </summary>
    All = 2
}
