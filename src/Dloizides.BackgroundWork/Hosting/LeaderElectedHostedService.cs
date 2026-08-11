using Dloizides.BackgroundWork.Configuration;
using Dloizides.BackgroundWork.Leasing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dloizides.BackgroundWork.Hosting;

/// <summary>
/// Wraps a singleton <see cref="IHostedService"/> so it runs only when this instance both
/// has the right <see cref="WorkerRole"/> and provably holds its lease.
/// </summary>
/// <remarks>
/// <para>
/// Why the lease is needed even at <c>replicas: 1</c>: a rolling update starts the new pod
/// BEFORE terminating the old one, so a 1-replica Deployment runs two copies for ~20-30s on
/// every single deploy. Without a lease, every deploy is a window in which two copies of a
/// delete/expiry sweep run concurrently.
/// </para>
/// <para>
/// Fails closed throughout. The inner service is started only after a successful acquire,
/// and is stopped as soon as the lease cannot be re-verified.
/// </para>
/// </remarks>
public sealed class LeaderElectedHostedService : IHostedService, IAsyncDisposable
{
    private readonly IHostedService _inner;
    private readonly IWorkerLease? _lease;
    private readonly BackgroundWorkOptions _options;
    private readonly LeaseStateRegistry _registry;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly string _leaseName;

    private CancellationTokenSource? _supervisorCts;
    private Task? _supervisor;
    private bool _innerRunning;

    /// <summary>Creates a guarded wrapper around <paramref name="inner"/>.</summary>
    public LeaderElectedHostedService(
        IHostedService inner,
        IWorkerLease? lease,
        string leaseName,
        BackgroundWorkOptions options,
        LeaseStateRegistry registry,
        TimeProvider timeProvider,
        ILogger logger)
    {
        _inner = inner;
        _lease = lease;
        _leaseName = leaseName;
        _options = options;
        _registry = registry;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>True when the guarded inner service is currently started.</summary>
    public bool InnerRunning => _innerRunning;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.Role == WorkerRole.Api)
        {
            _logger.LogInformation(
                "Worker {Lease} not started: Role=Api. Background sweeps run in the Worker role.",
                _leaseName);
            _registry.Forget(_leaseName);
            return Task.CompletedTask;
        }

        if (!_options.LeaseEnabled || _lease is null)
        {
            _logger.LogWarning(
                "Worker {Lease} starting WITHOUT a lease (LeaseEnabled=false). "
                + "This is for local development only — never a deployed multi-replica service.",
                _leaseName);
            return StartInnerAsync(cancellationToken);
        }

        // Supervision must not block host startup: acquiring may legitimately fail for a
        // long time while another instance holds the lease.
        _supervisorCts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        _supervisor = SuperviseAsync(_supervisorCts.Token);
        return Task.CompletedTask;
    }

    private async Task SuperviseAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!_innerRunning)
                {
                    if (await _lease!.TryAcquireAsync(cancellationToken).ConfigureAwait(false))
                    {
                        await StartInnerAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
                else if (!await _lease!.IsStillHeldAsync(cancellationToken).ConfigureAwait(false))
                {
                    _logger.LogError(
                        "Worker {Lease} LOST its lease — stopping the guarded work immediately. "
                        + "Another instance may now acquire it.",
                        _leaseName);
                    await StopInnerAsync(CancellationToken.None).ConfigureAwait(false);
                }

                _registry.Report(_leaseName, _lease!.IsHeld, _innerRunning, _timeProvider);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let the supervisor die — a dead supervisor means a worker that
                // silently never runs again.
                _logger.LogError(ex, "Worker {Lease} supervision iteration failed; will retry.", _leaseName);
            }

            try
            {
                await Task.Delay(_options.CheckInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task StartInnerAsync(CancellationToken cancellationToken)
    {
        if (_innerRunning)
        {
            return;
        }

        await _inner.StartAsync(cancellationToken).ConfigureAwait(false);
        _innerRunning = true;
        _registry.Report(_leaseName, _lease?.IsHeld ?? true, true, _timeProvider);
        _logger.LogInformation("Worker {Lease} started.", _leaseName);
    }

    private async Task StopInnerAsync(CancellationToken cancellationToken)
    {
        if (!_innerRunning)
        {
            return;
        }

        try
        {
            await _inner.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Worker {Lease} failed to stop cleanly.", _leaseName);
        }
        finally
        {
            _innerRunning = false;
            _registry.Report(_leaseName, _lease?.IsHeld ?? false, false, _timeProvider);
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_supervisorCts is not null)
        {
            await _supervisorCts.CancelAsync().ConfigureAwait(false);
        }

        if (_supervisor is not null)
        {
            try
            {
                await _supervisor.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected on shutdown
            }
        }

        await StopInnerAsync(cancellationToken).ConfigureAwait(false);

        if (_lease is not null)
        {
            await _lease.ReleaseAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _supervisorCts?.Dispose();
        if (_lease is not null)
        {
            await _lease.DisposeAsync().ConfigureAwait(false);
        }
    }
}
