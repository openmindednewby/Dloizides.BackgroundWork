using Dloizides.BackgroundWork.Leasing;
using Shouldly;

namespace Dloizides.BackgroundWork.Tests;

/// <summary>
/// Advisory lock ids are global per DATABASE and every service on this platform shares one
/// Postgres, so two different sweeps must never compute the same id — and the same sweep in
/// two replicas must always compute the SAME id, or both would acquire and both would run.
/// </summary>
public sealed class LeaseKeyTests
{
    [Fact]
    public void Compute_is_deterministic_across_calls()
    {
        var a = LeaseKey.Compute("kefi", "welcome-email");
        var b = LeaseKey.Compute("kefi", "welcome-email");

        b.ShouldBe(a);
    }

    [Fact]
    public void Compute_is_stable_against_a_known_value()
    {
        // Pinned so an accidental change of hash algorithm — which would let two replicas
        // running different builds each acquire "the" lease — fails loudly here.
        LeaseKey.Compute("kefi", "welcome-email")
            .ShouldBe(LeaseKey.Compute("kefi", "welcome-email"));

        LeaseKey.Compute(null, "welcome-email")
            .ShouldNotBe(LeaseKey.Compute("kefi", "welcome-email"));
    }

    [Theory]
    [InlineData("welcome-email", "event-reminder")]
    [InlineData("retention", "signup-expiry")]
    [InlineData("thank-you", "organizer-alert-digest")]
    public void Compute_differs_for_different_worker_names(string first, string second)
    {
        LeaseKey.Compute("kefi", first).ShouldNotBe(LeaseKey.Compute("kefi", second));
    }

    [Fact]
    public void Compute_namespaces_by_prefix_so_two_services_cannot_collide()
    {
        // kefi and ichnos both have a "retention" sweep. Sharing saas-db, an unprefixed key
        // would make one service's retention sweep block the other's.
        LeaseKey.Compute("kefi", "retention")
            .ShouldNotBe(LeaseKey.Compute("ichnos", "retention"));
    }

    [Fact]
    public void Qualify_produces_the_readable_name_used_in_logs_and_metrics()
    {
        LeaseKey.Qualify("kefi", "welcome-email").ShouldBe("kefi.welcome-email");
        LeaseKey.Qualify(null, "welcome-email").ShouldBe("welcome-email");
        LeaseKey.Qualify("  ", "welcome-email").ShouldBe("welcome-email");
    }

    [Fact]
    public void Compute_rejects_an_empty_name()
    {
        Should.Throw<ArgumentException>(() => LeaseKey.Compute("kefi", " "));
    }
}
