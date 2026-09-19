# Monitoring plan measurement

`MonitoringPlanMeasurement` is a manual performance and query-plan harness. It is excluded from
the normal Functional test project and does not provide correctness coverage.

Run it from the repository root in PowerShell:

```powershell
$env:INTEGRIOS_MEASURE = '1'
dotnet test tests/Integrios.FunctionalTests/Integrios.FunctionalTests.csproj `
  -p:EnableMonitoringMeasurement=true `
  --filter FullyQualifiedName~MonitoringPlanMeasurement `
  --logger 'console;verbosity=detailed'
```

The harness defaults to PostgreSQL. Select SQL Server explicitly when comparing providers:

```powershell
$env:INTEGRIOS_TEST_DATABASE_PROVIDER = 'sqlserver'
```

Optional switches:

- `INTEGRIOS_MEASURE_INDEXES=1` adds the candidate backlog index before measuring.
- `INTEGRIOS_MEASURE_DDL` replaces that candidate index DDL.
- `INTEGRIOS_MEASURE_ALL=1` includes the additional dead-letter join-versus-exists comparison.

The run seeds a representative temporary Testcontainers database with hundreds of thousands of
Events and Deliveries, then reports median timings and provider query plans. Do not run it as part
of the ordinary test loop.
