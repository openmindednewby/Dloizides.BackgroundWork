using Dloizides.BackgroundWork.Configuration;
using Dloizides.BackgroundWork.Hosting;
using Dloizides.BackgroundWork.Leasing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dloizides.BackgroundWork.Extensions;

/// <summary>
/// Registration surface for role-aware, lease-guarded background work.
/// </summary>
public static class BackgroundWorkServiceCollectionExtensions
{
    /// <summary>
    /// Binds <see cref="BackgroundWorkOptions"/> and registers the shared infrastructure.
    /// Call once, before registering any guarded worker.
    /// </summary>
    public static IServiceCollection AddBackgroundWork(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<BackgroundWorkOptions>? configure = null)
    {
        var options = new BackgroundWorkOptions();
        configuration.GetSection(BackgroundWorkOptions.SectionName).Bind(options);
        configure?.Invoke(options);
        options.Validate();

        services.AddSingleton(Options.Create(options));
        services.AddSingleton(options);
        services.TryAddSingleton<LeaseStateRegistry>();
        services.TryAddSingleton(TimeProvider.System);

        return services;
    }

    /// <summary>
    /// Registers a hosted service that runs only in the <see cref="WorkerRole.Worker"/> and
    /// <see cref="WorkerRole.All"/> roles — with NO lease.
    /// </summary>
    /// <remarks>
    /// Use only for background work that is genuinely safe to run concurrently. Anything
    /// that sends, deletes, expires or advances a state machine must use
    /// <see cref="AddLeaderElectedHostedService"/> instead.
    /// </remarks>
    public static IServiceCollection AddRoleAwareHostedService(
        this IServiceCollection services,
        Func<IServiceProvider, IHostedService> factory)
    {
        services.AddSingleton<IHostedService>(sp =>
        {
            var options = sp.GetRequiredService<BackgroundWorkOptions>();
            return options.Role == WorkerRole.Api
                ? new NoopHostedService()
                : factory(sp);
        });

        return services;
    }

    /// <summary>
    /// Registers a hosted service that runs only in the Worker/All roles AND only while this
    /// instance provably holds the named lease.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="leaseName">
    /// Lease name, unique within the service, e.g. <c>welcome-email</c>. It is namespaced by
    /// <see cref="BackgroundWorkOptions.KeyPrefix"/> because advisory lock ids are global per
    /// DATABASE and every service here shares one Postgres.
    /// </param>
    /// <param name="factory">Creates the inner hosted service.</param>
    public static IServiceCollection AddLeaderElectedHostedService(
        this IServiceCollection services,
        string leaseName,
        Func<IServiceProvider, IHostedService> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseName);

        services.AddSingleton<IHostedService>(sp =>
        {
            var options = sp.GetRequiredService<BackgroundWorkOptions>();
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var qualified = LeaseKey.Qualify(options.KeyPrefix, leaseName);

            if (options.Role == WorkerRole.Api)
            {
                // Do not even construct the inner service in the Api role — it may open
                // resources or schedule timers in its constructor.
                loggerFactory
                    .CreateLogger(typeof(LeaderElectedHostedService))
                    .LogInformation("Worker {Lease} not registered: Role=Api.", qualified);
                return new NoopHostedService();
            }

            IWorkerLease? lease = null;
            if (options.LeaseEnabled)
            {
                lease = new PostgresAdvisoryLease(
                    options.ConnectionString,
                    qualified,
                    LeaseKey.Compute(options.KeyPrefix, leaseName),
                    loggerFactory.CreateLogger<PostgresAdvisoryLease>());
            }

            return new LeaderElectedHostedService(
                factory(sp),
                lease,
                qualified,
                options,
                sp.GetRequiredService<LeaseStateRegistry>(),
                sp.GetRequiredService<TimeProvider>(),
                loggerFactory.CreateLogger<LeaderElectedHostedService>());
        });

        return services;
    }

    /// <summary>Placeholder used when a role means the real service must not run here.</summary>
    private sealed class NoopHostedService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
