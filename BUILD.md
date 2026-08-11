# Build & Publish — Dloizides.BackgroundWork

## Build

```bash
cd NuGetPackages/Dloizides.BackgroundWork
dotnet restore src/Dloizides.BackgroundWork/Dloizides.BackgroundWork.csproj
dotnet build   src/Dloizides.BackgroundWork/Dloizides.BackgroundWork.csproj -c Release --no-restore
```

## Test

```bash
dotnet test tests/Dloizides.BackgroundWork.Tests/Dloizides.BackgroundWork.Tests.csproj -c Release
```

The suite deliberately contains **no** test that needs a live Postgres. Everything that could
touch a database is behind `IWorkerLease`, so the supervision logic is exercised with a fake.
The one thing that cannot be faked — that the lease connection string disables pooling — is
asserted directly against `NpgsqlConnectionStringBuilder`.

## Publish

```powershell
cd NuGetPackages/Dloizides.BackgroundWork
.\publish.ps1 -Bump patch      # or minor / major
.\publish.ps1 -NoBump          # ship the version already in Directory.Build.props
```

The API key auto-loads from `SaaS/.env.local` (`NUGET_API_KEY`) — never pass `-ApiKey`, and
never hand-roll `dotnet nuget push`.

Propagation to nuget.org takes ~3–4 minutes. Poll before any consumer restores:

```
https://api.nuget.org/v3-flatcontainer/dloizides.backgroundwork/index.json
```

A 404 immediately after push is normal.

## Consuming

1. `Directory.Packages.props` → `<PackageVersion Include="Dloizides.BackgroundWork" Version="X.Y.Z" />`
2. Service `.csproj` → `<PackageReference Include="Dloizides.BackgroundWork" />`
3. `Program.cs` → `builder.Services.AddBackgroundWork(builder.Configuration);` then register each
   worker with `AddLeaderElectedHostedService` / `AddRoleAwareHostedService`.

⚠️ Register **per worker**, never as a blanket filter over all `IHostedService`s — the
MassTransit bus and request-driven helpers are hosted services that must keep running in the
`Api` role.
