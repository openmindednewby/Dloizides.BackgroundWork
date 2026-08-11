using Dloizides.BackgroundWork.Leasing;
using Npgsql;
using Shouldly;

namespace Dloizides.BackgroundWork.Tests;

/// <summary>
/// Guards the single detail that makes advisory locking correct.
/// </summary>
/// <remarks>
/// An advisory lock lives on a SESSION. Taken on a POOLED connection it is silently released
/// when that connection returns to the pool — the lock appears held and two workers run
/// anyway. This is the failure mode that a comment cannot prevent, so it is asserted.
/// </remarks>
public sealed class LeaseConnectionStringTests
{
    private const string Sample =
        "Host=saas-db;Port=5432;Database=kefi;Username=kefi;Password=secret";

    [Fact]
    public void ForDedicatedSession_disables_pooling()
    {
        var result = LeaseConnectionString.ForDedicatedSession(Sample);

        new NpgsqlConnectionStringBuilder(result).Pooling.ShouldBeFalse();
    }

    [Fact]
    public void ForDedicatedSession_disables_pooling_even_when_caller_explicitly_enabled_it()
    {
        var result = LeaseConnectionString.ForDedicatedSession(Sample + ";Pooling=true;Maximum Pool Size=50");

        new NpgsqlConnectionStringBuilder(result).Pooling.ShouldBeFalse();
    }

    [Fact]
    public void ForDedicatedSession_sets_keepalive_so_an_idle_session_is_not_dropped()
    {
        var result = LeaseConnectionString.ForDedicatedSession(Sample);

        new NpgsqlConnectionStringBuilder(result).KeepAlive
            .ShouldBe(LeaseConnectionString.KeepAliveSeconds);
    }

    [Fact]
    public void ForDedicatedSession_preserves_the_target_database()
    {
        var result = LeaseConnectionString.ForDedicatedSession(Sample);

        var builder = new NpgsqlConnectionStringBuilder(result);
        builder.Host.ShouldBe("saas-db");
        builder.Database.ShouldBe("kefi");
        builder.Username.ShouldBe("kefi");
    }

    [Fact]
    public void ForDedicatedSession_rejects_an_empty_connection_string()
    {
        Should.Throw<ArgumentException>(() => LeaseConnectionString.ForDedicatedSession("  "));
    }
}
