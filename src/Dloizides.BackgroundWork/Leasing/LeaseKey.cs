using System.Security.Cryptography;
using System.Text;

namespace Dloizides.BackgroundWork.Leasing;

/// <summary>
/// Turns a human-readable lease name into the <c>bigint</c> that
/// <c>pg_try_advisory_lock</c> requires.
/// </summary>
/// <remarks>
/// <para>
/// Advisory lock ids are global per DATABASE. Every service in this platform shares one
/// Postgres instance, so <c>kefi.retention</c> and some other service's sweep MUST NOT
/// collide by accident. Callers therefore namespace by service and the id is derived from
/// the full <c>{prefix}.{name}</c> string.
/// </para>
/// <para>
/// 🔴 The hash MUST be stable across processes, machines and runtime versions — two
/// replicas that compute different ids for the same lease would BOTH acquire and both
/// run. <see cref="string.GetHashCode()"/> is randomised per process in .NET Core and is
/// therefore unusable here; SHA-256 is used precisely because it is deterministic.
/// </para>
/// </remarks>
public static class LeaseKey
{
    /// <summary>
    /// Computes the deterministic advisory-lock id for a lease.
    /// </summary>
    /// <param name="prefix">Service namespace, e.g. <c>kefi</c>. May be empty.</param>
    /// <param name="name">Lease name, e.g. <c>welcome-email</c>.</param>
    /// <returns>A stable 64-bit id suitable for <c>pg_try_advisory_lock</c>.</returns>
    public static long Compute(string? prefix, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Lease name is required.", nameof(name));
        }

        var qualified = string.IsNullOrWhiteSpace(prefix) ? name : $"{prefix}.{name}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(qualified));

        // Postgres advisory lock ids are SIGNED bigint. Reading 8 bytes may produce any
        // long including negative values, which Postgres accepts — no masking needed.
        return BitConverter.ToInt64(bytes, 0);
    }

    /// <summary>Returns the fully-qualified, human-readable lease name used in logs and metrics.</summary>
    public static string Qualify(string? prefix, string name) =>
        string.IsNullOrWhiteSpace(prefix) ? name : $"{prefix}.{name}";
}
