using Microsoft.Extensions.Logging;
using Npgsql;

namespace Dloizides.BackgroundWork.Leasing;

/// <summary>
/// An <see cref="IWorkerLease"/> backed by a Postgres session-level advisory lock.
/// </summary>
/// <remarks>
/// <para>
/// Chosen over a Redis lease because the failure being designed against is an OOM
/// <c>SIGKILL</c>. A session-level advisory lock is released by Postgres the moment the
/// TCP session ends, so a killed pod frees its lease immediately — there is no TTL to
/// tune and no clock-skew window. A Redis lease would linger for its full TTL after the
/// same death, and tuning that TTL down trades stuck locks for false expiries under GC
/// pause. Postgres is also available in 7/7 services here; Redis in 2/7.
/// </para>
/// <para>
/// 🔴 <b>The lock MUST live on a dedicated, non-pooled connection.</b> An advisory lock is
/// scoped to a SESSION. Taken on an EF-pooled connection it is silently released the
/// instant that connection returns to the pool — the code reads as correct, the lock
/// appears held, and two workers run anyway. This class therefore forces
/// <c>Pooling=false</c> and owns the connection for the lifetime of the lease.
/// </para>
/// </remarks>
public sealed class PostgresAdvisoryLease : IWorkerLease
{
    private readonly string _connectionString;
    private readonly long _key;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private NpgsqlConnection? _connection;
    private bool _held;

    /// <summary>Creates a lease for a named piece of work.</summary>
    public PostgresAdvisoryLease(string connectionString, string qualifiedName, long key, ILogger logger)
    {
        _connectionString = LeaseConnectionString.ForDedicatedSession(connectionString);
        Name = qualifiedName;
        _key = key;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public bool IsHeld => _held;

    /// <inheritdoc />
    public async Task<bool> TryAcquireAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_held)
            {
                return true;
            }

            await CloseConnectionAsync().ConfigureAwait(false);

            var connection = new NpgsqlConnection(_connectionString);
            try
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

                await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection);
                command.Parameters.AddWithValue("key", _key);
                var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

                if (result is true)
                {
                    _connection = connection;
                    _held = true;
                    _logger.LogInformation("Lease {Lease} acquired.", Name);
                    return true;
                }

                await connection.DisposeAsync().ConfigureAwait(false);
                return false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Fail CLOSED: an unreachable database means we cannot prove exclusivity.
                await connection.DisposeAsync().ConfigureAwait(false);
                _logger.LogWarning(ex, "Lease {Lease} could not be acquired; treating as NOT held.", Name);
                return false;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> IsStillHeldAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_held || _connection is null)
            {
                return false;
            }

            try
            {
                // Proving the SESSION is alive proves the lock is still held: Postgres
                // releases session-level advisory locks when the session ends, so a live
                // session that acquired the lock and never unlocked still owns it.
                await using var command = new NpgsqlCommand("SELECT 1", _connection);
                _ = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "Lease {Lease} connection is dead — Postgres has released the lock. Marking NOT held.",
                    Name);
                _held = false;
                await CloseConnectionAsync().ConfigureAwait(false);
                return false;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_held || _connection is null)
            {
                await CloseConnectionAsync().ConfigureAwait(false);
                return;
            }

            try
            {
                await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", _connection);
                command.Parameters.AddWithValue("key", _key);
                _ = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Lease {Lease} released.", Name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Closing the connection releases the lock anyway — this is belt and braces.
                _logger.LogWarning(ex, "Lease {Lease} explicit unlock failed; closing the session instead.", Name);
            }
            finally
            {
                _held = false;
                await CloseConnectionAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task CloseConnectionAsync()
    {
        if (_connection is null)
        {
            return;
        }

        try
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Lease {Lease} connection dispose threw; ignoring.", Name);
        }

        _connection = null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
        _gate.Dispose();
    }
}
