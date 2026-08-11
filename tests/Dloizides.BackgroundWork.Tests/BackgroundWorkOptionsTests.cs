using Dloizides.BackgroundWork.Configuration;
using Shouldly;

namespace Dloizides.BackgroundWork.Tests;

/// <summary>
/// Fail-closed configuration. A worker that cannot obtain a lease must refuse to start
/// rather than run unguarded — the sweeps it guards delete data and expire live signups.
/// </summary>
public sealed class BackgroundWorkOptionsTests
{
    [Fact]
    public void Default_role_is_All_so_adoption_is_a_no_op()
    {
        // Critical for self-hosting customers (AMLService is distributed for self-hosting):
        // adopting the package must not force anyone onto two containers.
        new BackgroundWorkOptions().Role.ShouldBe(WorkerRole.All);
    }

    [Fact]
    public void Lease_is_enabled_by_default()
    {
        new BackgroundWorkOptions().LeaseEnabled.ShouldBeTrue();
    }

    [Theory]
    [InlineData(WorkerRole.Worker)]
    [InlineData(WorkerRole.All)]
    public void Validate_rejects_a_missing_connection_string_when_sweeps_would_run(WorkerRole role)
    {
        var options = new BackgroundWorkOptions { Role = role, ConnectionString = "" };

        Should.Throw<InvalidOperationException>(() => options.Validate());
    }

    [Fact]
    public void Validate_allows_an_empty_connection_string_in_the_Api_role()
    {
        // The Api role runs no sweeps, so it needs no lease and must not be forced to carry
        // a database credential it does not use.
        var options = new BackgroundWorkOptions { Role = WorkerRole.Api, ConnectionString = "" };

        Should.NotThrow(() => options.Validate());
    }

    [Fact]
    public void Validate_allows_an_empty_connection_string_when_the_lease_is_explicitly_disabled()
    {
        var options = new BackgroundWorkOptions
        {
            Role = WorkerRole.Worker,
            LeaseEnabled = false,
            ConnectionString = ""
        };

        Should.NotThrow(() => options.Validate());
    }

    [Fact]
    public void Validate_rejects_a_non_positive_check_interval()
    {
        var options = new BackgroundWorkOptions
        {
            Role = WorkerRole.Worker,
            ConnectionString = "Host=x;Database=y;Username=z",
            CheckInterval = TimeSpan.Zero
        };

        Should.Throw<InvalidOperationException>(() => options.Validate());
    }
}
