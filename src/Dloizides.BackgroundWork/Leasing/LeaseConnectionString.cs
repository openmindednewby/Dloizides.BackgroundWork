using Npgsql;

namespace Dloizides.BackgroundWork.Leasing;

/// <summary>
/// Builds the connection string used for advisory-lock sessions.
/// </summary>
/// <remarks>
/// 🔴 This is the single most important detail in the package, which is why it is a named,
/// tested unit rather than a comment inside the lease. A Postgres advisory lock is scoped to
/// a SESSION. Taken on a POOLED connection it is silently released the instant that
/// connection returns to the pool: the code reads as correct, <c>IsHeld</c> still says true,
/// and two workers run anyway. Disabling pooling is what makes the lock mean what it says.
/// </remarks>
public static class LeaseConnectionString
{
    /// <summary>Seconds between TCP keepalives on the lease session.</summary>
    public const int KeepAliveSeconds = 30;

    /// <summary>
    /// Returns <paramref name="connectionString"/> forced to a dedicated, non-pooled session.
    /// </summary>
    public static string ForDedicatedSession(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            // The whole point — see the class remarks.
            Pooling = false,

            // Keep the session alive through idle periods so the lock is not lost to an
            // intermediate device timing out an idle connection.
            KeepAlive = KeepAliveSeconds
        };

        return builder.ConnectionString;
    }
}
