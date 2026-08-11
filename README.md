# Dloizides.BackgroundWork

Run singleton background work safely alongside a horizontally-scaled HTTP tier.

A typical .NET service does two jobs in one process: it answers HTTP requests, and it runs
scheduled sweeps. Only the first is safe to duplicate. Set `replicas: 2` for availability and
the sweeps run twice — sending duplicate email, deleting the same rows twice, expiring the
same records twice.

This package separates those jobs and guards the second one.

```csharp
// Program.cs
builder.Services.AddBackgroundWork(builder.Configuration);

// A sweep that must never run twice
builder.Services.AddLeaderElectedHostedService(
    "welcome-email", sp => sp.GetRequiredService<WelcomeEmailWorker>());

// Background work that is genuinely safe to run concurrently
builder.Services.AddRoleAwareHostedService(sp => sp.GetRequiredService<CacheWarmer>());
```

```jsonc
// appsettings.json — the default changes nothing
{
  "BackgroundWork": {
    "Role": "All",                 // Api | Worker | All  (default All)
    "KeyPrefix": "kefi",
    "ConnectionString": "Host=saas-db;Database=kefi;Username=kefi;Password=..."
  }
}
```

Deploy the **same image** twice, with `BackgroundWork__Role=Api` on the scalable HTTP tier and
`BackgroundWork__Role=Worker` on a single-replica worker.

## Why `replicas: 1` is not enough on its own

A rolling update starts the new pod **before** terminating the old one, so a 1-replica
Deployment runs two copies for ~20–30s on **every deploy**. Without a lease, every deploy is a
window in which two copies of a delete sweep run concurrently. The lease makes correctness
independent of the replica count.

## Why Postgres advisory locks

The failure being designed against is an OOM `SIGKILL`. A session-level advisory lock is
released by Postgres the moment the TCP session ends, so a killed pod frees its lease
immediately — no TTL to tune, no clock-skew window. A Redis lease would linger for its full
TTL after the same death, and tuning that TTL down trades stuck locks for false expiries under
GC pause.

It also needs **no schema, no migration and no writes** — the lock is a session primitive, so
adopting this package cannot touch application data.

## Fails closed

| Situation | Behaviour |
|---|---|
| Lease held by another instance | guarded work does **not** start |
| Database unreachable | treated as **not held** — work does not start |
| Lease session dies while running | work is **stopped** within `CheckInterval` |
| `Role=Api` | inner service is never even constructed |
| `Role=Worker` / `All` with no connection string | **startup throws** |

## The one detail that matters

The lock **must** be held on a dedicated, non-pooled connection. An advisory lock lives on a
*session*: taken on an EF-pooled connection it is silently released the moment that connection
returns to the pool — the code reads as correct, `IsHeld` still says `true`, and two workers
run anyway. `LeaseConnectionString.ForDedicatedSession` forces `Pooling=false`, and it is
asserted by test rather than left as a comment.

Lease ids are derived by SHA-256 from `{KeyPrefix}.{name}`. Advisory lock ids are global per
**database**, so `KeyPrefix` is what stops two services sharing one Postgres from colliding.
`string.GetHashCode()` is randomised per process and would let two replicas compute different
ids for the same lease — both would acquire, both would run.

## Observability

`LeaseStateRegistry` exposes, per worker: whether the lease is held, whether the guarded work
is running, and when that last changed. A worker that silently stops sweeping is otherwise
invisible — nothing errors, nothing restarts, the emails just stop.

## API

| Member | Purpose |
|---|---|
| `AddBackgroundWork(configuration, configure?)` | Binds options, registers shared infrastructure. Call once. |
| `AddLeaderElectedHostedService(name, factory)` | Role-aware **and** lease-guarded. Use for anything that sends, deletes, expires, or advances a state machine. |
| `AddRoleAwareHostedService(factory)` | Role-aware only, no lease. Concurrency-safe work only. |
| `LeaseStateRegistry.Snapshot()` | Current held/running state of every guarded worker. |
| `WorkerRole` | `Api` \| `Worker` \| `All` (default `All`). |

Registration is **opt-in per worker**, never a blanket filter over every `IHostedService` —
some hosted services (request-driven helpers, the MassTransit bus) must keep running in the
`Api` role.

## License

MIT
