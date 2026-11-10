---
title: "Npgsql Driver Upgrade Context — 5.0.16 → 10.0.3 (Hexagonal .NET 8, PostgreSQL 17)"
doc_type: project-context
scope: brownfield-upgrade
framework: BMAD Method
applies_to: all .NET 8 solutions in this repo that reference Npgsql
status: approved-for-implementation
created: 2026-11-10
owner: Parthiban
target:
  dotnet: net8.0            # .NET 8 reached end of support on 2026-11-10; .NET 10 is the next release
  postgresql: "17"
  npgsql_ado: "10.0.3"      # Dapper / plain ADO.NET adapters
  npgsql_efcore_on_net8: "9.0.x provider + Npgsql 9.0.5"   # see §2 — EFCore.PG 10 requires .NET 10
current:
  npgsql: "5.0.16"          # vulnerable to CVE-2024-32655, out of support, never tested on PG17
incident: "Production outage — Npgsql.NpgsqlException: Exception while reading from stream"
---

# Npgsql Driver Upgrade — Project Context

> **For every BMAD agent.** Load this file before planning, writing a story, writing code or writing tests for the Npgsql upgrade. The rules in §3 (architecture) and §10 (guardrails) are mandatory. If anything here conflicts with a story file, this file wins for Npgsql-related decisions; raise the conflict with the Architect.

| BMAD role | Use these sections |
| --- | --- |
| Analyst / PM | §1, §2, §11 |
| Architect | §2, §3, §6, §7 |
| Scrum Master | §9 (epic and stories, ready to shard) |
| Developer | §3, §4, §5, §6, §10 |
| QA / Test Architect | §7, §8, §9 acceptance criteria |

---

## 1. Goal and success criteria

**Goal:** replace Npgsql 5.0.16 in every project of the solution with a supported version, make all dependent code and tests pass against PostgreSQL 17, and harden connection handling so "Exception while reading from stream" does not recur.

**Success criteria (measurable):**

1. No project references Npgsql < the target in §2 (`dotnet list package --include-transitive` proves it).
2. `dotnet build -warnaserror` passes for the solution; no new `CS0618` obsolete warnings from Npgsql APIs.
3. All existing unit tests pass unchanged, except tests listed in the change log of the story that touched them.
4. Integration tests run against a real `postgres:17` container and pass.
5. Architecture tests prove Domain and Application assemblies have **no** reference to `Npgsql*`.
6. Resilience tests (§8.4) pass: idle-past-network-timeout, killed backend, server restart.
7. In production, zero `NpgsqlException` with inner `TimeoutException` / `IOException` / `EndOfStreamException` over 7 days, outside planned DB maintenance.

**Out of scope:** moving to .NET 10, schema changes, query rewrites not required by a breaking change.

---

## 2. Target versions (decision record)

| Data-access style in the adapter | Npgsql | EF Core provider | Why |
| --- | --- | --- | --- |
| Dapper or plain ADO.NET (no EF Core) | **10.0.3** | — | Latest stable; ships a `net8.0` build; also targets `net10.0`, so no driver change for the .NET 10 move |
| EF Core on .NET 8 | **9.0.5** (pulled by the provider) | **Npgsql.EntityFrameworkCore.PostgreSQL 9.0.x** (latest 9.0 patch) | `Npgsql.EntityFrameworkCore.PostgreSQL 10.x` targets **.NET 10 only** (EF Core 10). On .NET 8 the newest EF line is 9 |
| Mixed (EF Core and Dapper in one process) | Follow the EF Core row (9.0.5) | 9.0.x | One Npgsql version per process; do not let Dapper pull 10.x beside EF on 9.x |
| After the .NET 10 migration | 10.0.x | 10.0.x | Re-align both to 10 |

Rules:

- Pin versions centrally in `Directory.Packages.props` (Central Package Management). If CPM is not enabled, enable it as part of story 1.1.
- Never reference Npgsql 11 previews.
- Fallback only if the date/time audit (§5.1) cannot finish in the window: Npgsql **8.0.9** (still serviced). Record the decision in the story.

> If you are a Dev agent and cannot tell which row applies, run the discovery in §4 first; if `Microsoft.EntityFrameworkCore` appears in any adapter project, use the EF Core row.

---

## 3. Hexagonal architecture rules for this upgrade

```
            ┌────────────────────────────────────────────┐
            │  Host / API  (composition root)            │  ← registers NpgsqlDataSource, options, resilience
            │  <Solution>.Api, <Solution>.Worker(Quartz) │
            └───────────────┬────────────────────────────┘
                            │ DI
 ┌──────────────────────────▼───────────────────────────┐
 │  Adapters (driven / secondary)                        │
 │  <Solution>.Infrastructure.Persistence  ← ONLY place  │  Npgsql, Dapper, EF Core, SQL, type mapping,
 │  (a.k.a. Adapters.Postgres / Infrastructure)          │  exception translation, retries
 └──────────────────────────┬───────────────────────────┘
                            │ implements
 ┌──────────────────────────▼───────────────────────────┐
 │  Application  (use cases + ports: I*Repository, …)    │  NO Npgsql. Ports use domain types only
 └──────────────────────────┬───────────────────────────┘
 ┌──────────────────────────▼───────────────────────────┐
 │  Domain  (entities, value objects, domain errors)     │  NO Npgsql. NO System.Data
 └──────────────────────────────────────────────────────┘
```

**Mandatory rules**

1. `Npgsql`, `NpgsqlTypes`, `Npgsql.*` namespaces may only be referenced from the persistence adapter project(s), the Host composition root (registration only), and test projects for those.
2. Port interfaces in Application **must not change signature** because of the upgrade. If a port currently leaks an Npgsql type (e.g. returns `NpgsqlDataReader`, takes `NpgsqlParameter`, throws `PostgresException` by contract), that is a pre-existing violation: fix it in story 1.2 and list it in the story's change log.
3. The adapter translates driver exceptions into Application-level exceptions (§6.8). No `catch (NpgsqlException)` in Application or Domain.
4. Date/time normalization (UTC) happens at the adapter boundary or in domain value objects that already enforce UTC; never by setting legacy switches silently.
5. One `NpgsqlDataSource` per physical database, registered as a singleton in the Host. Adapters receive `NpgsqlDataSource` (or an adapter-internal connection factory) through DI; they never build connection strings.

---

## 4. Discovery — build the impact inventory first

Run from the repo root. Paste the output into the story 1.1 "Inventory" section. Every hit must end up either changed or marked "no change needed — reason".

### 4.1 Packages

```bash
dotnet list package --include-transitive | grep -iE "npgsql|dapper|entityframeworkcore|quartz|healthchecks|testcontainers|opentelemetry"
grep -RniE "Include=\"(Npgsql|Dapper|Microsoft.EntityFrameworkCore|AspNetCore.HealthChecks.NpgSql|Testcontainers|Quartz)" --include=*.csproj --include=Directory.Packages.props .
```

### 4.2 Code patterns (bash; PowerShell equivalent below)

| # | Pattern (regex) | Why it matters | Fix ref |
| --- | --- | --- | --- |
| P1 | `using Npgsql` | Find every file touching the driver; flag any outside the adapter (§3 rule 1) | §3 |
| P2 | `new NpgsqlConnection\(` | Replace with `NpgsqlDataSource` | §6.1 |
| P3 | `NpgsqlConnection\.GlobalTypeMapper\|\.TypeMapper\.Map` | Connection-level / global mapping removed or obsolete since 7.0 | §6.5 |
| P4 | `CommandType\.StoredProcedure` | 7.0 issues `CALL` (procedures) not `SELECT` (functions) | §6.4 |
| P5 | `NpgsqlLogManager\|INpgsqlLoggingProvider` | Removed in 7.0 | §6.6 |
| P6 | `DateTime\.Now\|DateTimeKind\.Local\|DateTimeKind\.Unspecified\|ToLocalTime\(` | 6.0 strict `timestamptz` UTC mapping | §5.1, §6.2 |
| P7 | `NpgsqlDbType\.Timestamp(TZ\|Tz)?\|NpgsqlDbType\.TimeTZ\|DbType\.DateTime\b` | Renamed/removed enum members; `DbType.DateTime` now → `timestamptz` | §6.2 |
| P8 | `NpgsqlDateTime\|NpgsqlDate\b\|NpgsqlTimeSpan` | Removed in 7.0 | §6.2 |
| P9 | `\.GetValue\(\|ExecuteScalar(Async)?\(\|\(DateTime\)\s*reader` | 10.0 returns `DateOnly`/`TimeOnly` for `date`/`time` | §6.3 |
| P10 | `GetTimeSpan\(\|TimeSpan.*interval` | `interval` with months/years can't map to `TimeSpan` (6.0) | §6.2 |
| P11 | `\.Open\(\)\|ExecuteNonQuery\(\)\|ExecuteReader\(\)\|\.Read\(\)` (sync) | Sync I/O; planned to throw by default in Npgsql 11 | §6.7 |
| P12 | `Integrated Security\|Internal Command Timeout\|Server Compatibility Mode\|Load Table Composites\|Trust Server Certificate\|SSL Mode=Require\|Convert Infinity DateTime` | Removed / obsolete / changed meaning | §6.9 |
| P13 | `UseJsonNet\|JsonDocument\|JsonNode\|jsonb` | 8.0 requires `EnableDynamicJson()` for POCO/JsonNode mapping | §6.5 |
| P14 | `NpgsqlStatement\|PostgresException\.Statement\|\.BatchCommand` | Removed in 6.0 / null by default in 10.0 | §6.8 |
| P15 | `catch\s*\(\s*(Npgsql\|Postgres)Exception` | Must live only in the adapter; translate | §6.8 |
| P16 | `DBNull\.Value` inside `object\[\]` / array params | 6.0 rejects `DBNull` in arrays; use `null` | §6.2 |
| P17 | `NpgsqlCidr\|IPAddress, int` | `cidr` mapping changed in 8.0 and 10.0 | — |
| P18 | `quartz\.dataSource\|UsePostgres\|AdoJobStore` | Quartz job store uses the same Npgsql in-process | §6.10 |

```bash
for p in "using Npgsql" "new NpgsqlConnection\(" "GlobalTypeMapper" "CommandType\.StoredProcedure" \
         "NpgsqlLogManager" "DateTime\.Now|DateTimeKind\.(Local|Unspecified)|ToLocalTime\(" \
         "NpgsqlDbType\.(TimestampTZ|TimeTZ)|DbType\.DateTime\b" "NpgsqlDateTime|NpgsqlDate\b|NpgsqlTimeSpan" \
         "\.GetValue\(|ExecuteScalar(Async)?\(" "\.Open\(\)|ExecuteNonQuery\(\)|ExecuteReader\(\)" \
         "Integrated Security|Internal Command Timeout|Server Compatibility Mode|Trust Server Certificate|SSL Mode=Require" \
         "EnableDynamicJson|UseJsonNet|JsonNode" "PostgresException\.Statement|NpgsqlStatement" \
         "catch\s*\(\s*(Npgsql|Postgres)Exception" "AdoJobStore|quartz\.dataSource"; do
  echo "=== $p"; grep -RnE "$p" --include=*.cs --include=*.json --include=*.config --include=*.yaml . | grep -v "/bin/\|/obj/"
done
```

```powershell
$patterns = 'using Npgsql','new NpgsqlConnection\(','GlobalTypeMapper','CommandType\.StoredProcedure','DateTime\.Now|DateTimeKind\.(Local|Unspecified)', `
            '\.GetValue\(|ExecuteScalar(Async)?\(','\.Open\(\)|ExecuteNonQuery\(\)','Trust Server Certificate|SSL Mode=Require|Internal Command Timeout', `
            'catch\s*\(\s*(Npgsql|Postgres)Exception','AdoJobStore|quartz\.dataSource'
foreach ($p in $patterns) { "=== $p"; Get-ChildItem -Recurse -Include *.cs,*.json,*.config | Where-Object FullName -notmatch '\\(bin|obj)\\' | Select-String -Pattern $p }
```

### 4.3 Database-side inventory (ask the DBA or run read-only)

```sql
-- columns whose mapping changes
SELECT table_schema, table_name, column_name, data_type
FROM information_schema.columns
WHERE table_schema NOT IN ('pg_catalog','information_schema')
  AND data_type IN ('timestamp without time zone','timestamp with time zone','date','time without time zone','interval','cidr','json','jsonb')
ORDER BY 1,2,3;

-- functions vs procedures (for CommandType.StoredProcedure)
SELECT n.nspname, p.proname, CASE p.prokind WHEN 'f' THEN 'function' WHEN 'p' THEN 'procedure' END AS kind
FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
WHERE n.nspname NOT IN ('pg_catalog','information_schema');
```

---

## 5. Highest-risk behaviour changes (read before coding)

### 5.1 `timestamptz` / `timestamp` strict mapping (Npgsql 6.0)

- `DateTime` with `Kind=Utc` → `timestamptz`; `Local`/`Unspecified` → `timestamp`. Mismatches **throw** on write.
- `timestamptz` reads return `Kind=Utc` (no conversion to server-local).
- Only `DateTimeOffset` with offset 0 can be written.
- FX/MM impact: trade time, value date, settlement date, fixing time, RFQ expiry, streaming quote timestamps, Quartz schedule times.
- Legacy opt-out exists (`AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true)`) but **is not allowed** unless the story records why and a removal ticket exists (§10).

### 5.2 `date` / `time` → `DateOnly` / `TimeOnly` on untyped reads (Npgsql 10.0, ADO/Dapper path only)

- `reader.GetValue(i)`, `ExecuteScalarAsync()` and anything that materializes through `GetValue` (Dapper does) now yield `DateOnly`/`TimeOnly`.
- `reader.GetFieldValue<DateTime>(i)` still works.
- **Dapper risk:** DTO properties typed `DateTime` mapped from `date` columns may fail to materialize. Fix one of three ways, in this order of preference: change the DTO to `DateOnly`; cast in SQL (`value_date::timestamp`); register the legacy resolver (§6.3). A test must prove the chosen fix (§8.2).

### 5.3 `CommandType.StoredProcedure` (Npgsql 7.0)

Now sends `CALL proc(...)`. Database *functions* invoked this way break. Prefer explicit SQL text.

---

## 6. Code change catalogue (before → after)

All namespaces below are placeholders: replace `<Solution>` with the real root namespace.

### 6.1 Composition root: one `NpgsqlDataSource` (Host)

**Before (5.0.16):**

```csharp
services.AddScoped<IDbConnection>(_ => new NpgsqlConnection(configuration.GetConnectionString("FxDb")));
```

**After (Dapper / ADO.NET, Npgsql 10.0.3, package `Npgsql.DependencyInjection` 10.0.3):**

```csharp
// <Solution>.Api / Program.cs  (and the Quartz worker host)
builder.Services.AddNpgsqlDataSource(
    builder.Configuration.GetConnectionString("FxDb")!,
    dataSourceBuilder =>
    {
        dataSourceBuilder.UseLoggerFactory(LoggerFactory.Create(b => b.AddConsole())); // or the host's factory
        // dataSourceBuilder.MapEnum<OrderStatus>("order_status");   // replaces GlobalTypeMapper (§6.5)
        // dataSourceBuilder.EnableDynamicJson();                    // only if jsonb ↔ POCO is used (§6.5)
    });

builder.Services.AddPersistenceAdapter();   // extension in the adapter project: registers repositories
```

**After (EF Core on .NET 8, provider 9.0.x):**

```csharp
builder.Services.AddDbContext<FxDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("FxDb"),
        npgsql =>
        {
            npgsql.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(2), errorCodesToAdd: null);
            // npgsql.MapEnum<OrderStatus>("order_status");
        }));
```

### 6.2 Adapter: connection factory and UTC normalization

```csharp
// <Solution>.Infrastructure.Persistence/Postgres/PostgresConnectionFactory.cs
internal sealed class PostgresConnectionFactory(NpgsqlDataSource dataSource)
{
    public ValueTask<NpgsqlConnection> OpenAsync(CancellationToken ct) => dataSource.OpenConnectionAsync(ct);
}

// <Solution>.Infrastructure.Persistence/Postgres/UtcGuard.cs
internal static class UtcGuard
{
    public static DateTime EnsureUtc(DateTime value, string name) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => throw new ArgumentException($"{name} must be UTC for a timestamptz column (Kind=Unspecified).", name)
    };
}
```

```csharp
// Repository write — before: cmd.Parameters.AddWithValue("trade_time", order.TradeTime);   // Local → now throws
cmd.Parameters.Add(new NpgsqlParameter<DateTime>("trade_time", UtcGuard.EnsureUtc(order.TradeTime, nameof(order.TradeTime))));
// timestamp (without tz) columns: pass Kind=Unspecified on purpose
cmd.Parameters.Add(new NpgsqlParameter<DateTime>("cutoff_local", DateTime.SpecifyKind(order.CutoffLocal, DateTimeKind.Unspecified)));
```

Removed types: replace `NpgsqlDateTime`/`NpgsqlDate`/`NpgsqlTimeSpan` with `DateTime`/`DateOnly`/`TimeSpan` or `NpgsqlInterval`. Replace `NpgsqlDbType.TimestampTZ`/`TimeTZ` with `TimestampTz`/`TimeTz`. In array parameters use `null`, not `DBNull.Value`.

### 6.3 Adapter reads: `date`/`time` (Npgsql 10)

```csharp
// Before
var valueDate = (DateTime)reader.GetValue(reader.GetOrdinal("value_date"));
// After (preferred: domain already uses DateOnly)
var valueDate = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("value_date"));
// After (keep DateTime in the DTO)
var valueDate = reader.GetFieldValue<DateTime>(reader.GetOrdinal("value_date"));
```

Dapper DTO with `DateTime ValueDate` over a `date` column: change the property to `DateOnly`, or select `value_date::timestamp AS ValueDate`. Last resort, globally on the data source builder:

```csharp
dataSourceBuilder.AddTypeInfoResolverFactory(new LegacyDateAndTimeResolverFactory()); // file from the Npgsql 10 release notes
```

### 6.4 Stored procedures vs functions (Npgsql 7)

```csharp
// Before — works only if fx_get_rates is a FUNCTION and Npgsql < 7
var cmd = new NpgsqlCommand("fx_get_rates", conn) { CommandType = CommandType.StoredProcedure };
// After — explicit, works for functions
await using var cmd = dataSource.CreateCommand("SELECT * FROM fx_get_rates(@ccy_pair, @as_of)");
// After — real procedures
await using var cmd = dataSource.CreateCommand("CALL fx_book_trade(@trade_id, @amount)");
```

Use the §4.3 query to know which is which. Do not enable `Npgsql.EnableStoredProcedureCompatMode` except as a recorded, temporary decision.

### 6.5 Type mapping and JSON (Npgsql 7 and 8)

- `NpgsqlConnection.GlobalTypeMapper.MapEnum<T>()` / `conn.TypeMapper.Map…` → `dataSourceBuilder.MapEnum<T>("pg_enum_name")` in the Host.
- jsonb ↔ POCO / `JsonNode` → `dataSourceBuilder.EnableDynamicJson()`; plain `string` / `JsonDocument` need nothing.
- `Npgsql.Json.NET` plugin users: upgrade the plugin to the same version as Npgsql and call `dataSourceBuilder.UseJsonNet()`.

### 6.6 Logging (Npgsql 7)

Delete any `NpgsqlLogManager.Provider = …` code. Logging flows through `ILoggerFactory` via `UseLoggerFactory` (§6.1). Parameter logging only in non-prod: `dataSourceBuilder.EnableParameterLogging()`.

### 6.7 Async all the way (hedge for Npgsql 11)

Inside adapters convert `Open()`, `ExecuteNonQuery()`, `ExecuteReader()`, `Read()` to their `Async` forms and flow `CancellationToken` from the port method. If a port is synchronous today, do **not** change it in this epic; add a backlog item instead.

### 6.8 Exception translation in the adapter

```csharp
// <Solution>.Application/Abstractions/Persistence/PersistenceExceptions.cs   (Application — no Npgsql here)
public sealed class PersistenceUnavailableException(string message, Exception inner) : Exception(message, inner);
public sealed class DuplicateEntityException(string message, Exception inner) : Exception(message, inner);

// <Solution>.Infrastructure.Persistence/Postgres/PostgresErrorTranslator.cs
internal static class PostgresErrorTranslator
{
    public static Exception Translate(Exception ex) => ex switch
    {
        PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg => new DuplicateEntityException(pg.MessageText, pg),
        NpgsqlException { IsTransient: true } npg                         => new PersistenceUnavailableException("Database temporarily unavailable.", npg),
        _ => ex
    };
}
```

Retry (Polly v8, adapter-internal; **idempotent operations only** — reads, upserts with keys; FX order submits need an idempotency key before they may be retried):

```csharp
internal static class PostgresResilience
{
    public static readonly ResiliencePipeline ReadPipeline = new ResiliencePipelineBuilder()
        .AddRetry(new RetryStrategyOptions
        {
            ShouldHandle = new PredicateBuilder().Handle<NpgsqlException>(e => e.IsTransient),
            MaxRetryAttempts = 3,
            Delay = TimeSpan.FromMilliseconds(200),
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true
        })
        .Build();
}
```

`PostgresException.Statement` / `NpgsqlStatement` are gone; `PostgresException.BatchCommand` is `null` unless `Include Failed Batched Command=true`.

### 6.9 Connection string (per environment config, not code)

```
Host=<host>;Port=5432;Database=<db>;Username=<user>;Password=<secret>;
Application Name=<ServiceName>;
Pooling=true;Minimum Pool Size=0;Maximum Pool Size=50;
Timeout=15;Command Timeout=30;
Keepalive=30;Tcp Keepalive=true;
Connection Idle Lifetime=120;Connection Pruning Interval=10;Connection Lifetime=3600;
SSL Mode=VerifyFull;Root Certificate=<path-to-full-CA-bundle>;
GSS Encryption Mode=Disable
```

- `Keepalive` and `Connection Idle Lifetime` must be **below the shortest idle timeout** of any firewall / LB / NAT between app and DB in that region. Record the measured value in the story.
- Remove: `Integrated Security`, `Internal Command Timeout`, `Server Compatibility Mode`, `Load Table Composites`, `Convert Infinity DateTime`.
- `SSL Mode=Require` no longer validates certificates (8.0); use `VerifyFull`. With `Root Certificate` set, Npgsql 10 trusts only that bundle, so include intermediates.
- `GSS Encryption Mode` exists only in Npgsql 10; omit it on the EF Core / Npgsql 9 path.

### 6.10 Quartz background jobs

- If Quartz uses ADO job store on PostgreSQL, it loads Npgsql from the same process: verify its provider string still resolves with the new Npgsql and run the Quartz tables' tests (§8.3). Quartz schedule times are stored as ticks/`bigint` in the standard schema; only custom job tables with `timestamptz` need the §6.2 UTC rules.
- Jobs must resolve repositories from a scope per execution and pass `context.CancellationToken` to adapter calls.
- Long-running jobs rely on `Tcp Keepalive=true` (Npgsql's own keepalive cannot run while a query executes).

### 6.11 Dependent packages to bump together

| Package | Rule |
| --- | --- |
| `Npgsql.DependencyInjection`, `Npgsql.OpenTelemetry`, `Npgsql.Json.NET`, `Npgsql.NodaTime`, `Npgsql.NetTopologySuite` | Same version as `Npgsql` |
| `Npgsql.EntityFrameworkCore.PostgreSQL` (+ `.NodaTime`, `.NetTopologySuite`) | 9.0.x on .NET 8 (§2) |
| `Dapper` | ≥ 2.0.123 (required since Npgsql 6); use the latest 2.1.x |
| `AspNetCore.HealthChecks.NpgSql` | A version whose Npgsql dependency range accepts the target; check `dotnet list package --include-transitive` shows a single Npgsql |
| `Testcontainers.PostgreSql` | Latest; image pinned to `postgres:17` |
| Dashboards (Grafana / APM) | Npgsql 10 renames metrics (`db.client.operation.duration`, `db.client.connection.count`) — update queries |

---

## 7. Test strategy per hexagonal layer

| Layer / test project | Expected impact | What to do |
| --- | --- | --- |
| `Domain.Tests` | None | Must pass untouched. A failure here means Npgsql leaked into Domain: stop and fix §3 |
| `Application.Tests` (ports mocked) | None, unless a port leaked Npgsql types | Must pass untouched. Re-point mocks only if story 1.2 changed a leaky port |
| `Infrastructure.Persistence.Tests` that **mock** `NpgsqlConnection`/`NpgsqlCommand` | Break (sealed classes, changed internals) | Replace with Testcontainers integration tests (§8.1). Do not try to mock the driver |
| `Infrastructure.Persistence.IntegrationTests` | Date/time, procedure, JSON, enum failures surface here | Run on `postgres:17`; fix code per §6, not the assertion, unless the assertion encoded the old bug |
| `Architecture.Tests` | New | Add rules in §8.5 |
| `Api.IntegrationTests` (`WebApplicationFactory`) | Connection registration changes | Swap the connection string for the container's; keep the real `AddNpgsqlDataSource` registration |
| Resilience tests | New | §8.4 |

**Fixing a failing test — decision rule for the Dev/QA agent:**

1. If the failure is a `DateTime.Kind` mismatch: fix the production code to send UTC (§6.2). Change the test only if it asserted a Local/Unspecified value for a `timestamptz` column; record that in the change log.
2. If the failure is `InvalidCastException` from `DateOnly`/`TimeOnly`: fix the reader/DTO (§6.3).
3. If the failure is "function does not exist" / "is not a procedure": fix the call per §6.4.
4. If the test mocks the driver: delete and replace with an integration test covering the same behaviour; list both test names in the change log.
5. Never weaken an assertion, increase a timeout, or add `[Skip]` to make a test pass without a recorded reason approved in the story.

---

## 8. Test code templates

### 8.1 Shared PostgreSQL 17 fixture (xUnit + Testcontainers)

```csharp
// tests/<Solution>.Infrastructure.Persistence.IntegrationTests/PostgresFixture.cs
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:17")
        .WithDatabase("fx_test")
        .Build();

    public NpgsqlDataSource DataSource { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        var csb = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            KeepAlive = 30, TcpKeepAlive = true, ConnectionIdleLifetime = 120, CommandTimeout = 30
        };
        var builder = new NpgsqlDataSourceBuilder(csb.ConnectionString);
        // mirror production mappings here (MapEnum, EnableDynamicJson, …) — same extension method as the Host
        DataSource = builder.Build();
        await SchemaMigrator.ApplyAsync(DataSource);   // run the repo's real migrations / DDL scripts
    }

    public async Task DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await _container.DisposeAsync();
    }
}

[CollectionDefinition(nameof(PostgresCollection))]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
```

> Put the data-source configuration (mappings, JSON, logging) in **one** extension method in the adapter, e.g. `NpgsqlDataSourceBuilder.ConfigureFxMappings()`, and call it from both the Host and this fixture so tests exercise production mappings.

### 8.2 Date/time round-trip tests

```csharp
[Collection(nameof(PostgresCollection))]
public sealed class DateTimeMappingTests(PostgresFixture fx)
{
    [Fact]
    public async Task Timestamptz_roundtrips_as_utc()
    {
        var tradeTime = new DateTime(2026, 11, 10, 6, 0, 0, DateTimeKind.Utc);
        await using var cmd = fx.DataSource.CreateCommand("SELECT @t::timestamptz");
        cmd.Parameters.Add(new NpgsqlParameter<DateTime>("t", tradeTime));
        var result = (DateTime)(await cmd.ExecuteScalarAsync())!;
        Assert.Equal(DateTimeKind.Utc, result.Kind);
        Assert.Equal(tradeTime, result);
    }

    [Fact]
    public async Task Writing_local_datetime_to_timestamptz_is_rejected()
    {
        await using var cmd = fx.DataSource.CreateCommand("SELECT @t::timestamptz");
        cmd.Parameters.Add(new NpgsqlParameter<DateTime>("t", DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Local)));
        await Assert.ThrowsAnyAsync<Exception>(() => cmd.ExecuteScalarAsync());   // documents the 6.0 rule
    }

    [Fact]
    public async Task Date_column_reads_as_DateOnly_on_untyped_access()   // Npgsql 10 path only
    {
        await using var cmd = fx.DataSource.CreateCommand("SELECT DATE '2026-11-12'");
        Assert.IsType<DateOnly>(await cmd.ExecuteScalarAsync());
    }
}
```

Add one round-trip test **per repository** for each date/time column found in §4.3 (trade time, value date, settlement date, expiry, fixing time).

### 8.3 Repository contract tests

For each port implementation in the adapter: insert → read back → assert equality of the domain object, using the real repository class and the fixture's data source. Cover: procedure/function calls (§6.4), enums (§6.5), jsonb columns, `numeric` precision for amounts and rates (no rounding drift), and the Quartz custom tables if any.

### 8.4 Resilience tests (prove the outage cannot recur)

```csharp
[Fact]
public async Task Pooled_connection_killed_by_server_is_recovered()
{
    var repo = CreateRepository(fx.DataSource);
    await repo.GetRatesAsync("EURUSD", CancellationToken.None);                         // warms the pool

    await using (var admin = await fx.DataSource.OpenConnectionAsync())
    await using (var kill = new NpgsqlCommand(
        "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid()", admin))
        await kill.ExecuteNonQueryAsync();

    var rates = await repo.GetRatesAsync("EURUSD", CancellationToken.None);             // must succeed via retry / fresh connection
    Assert.NotNull(rates);
}
```

Also, in an environment with the production network path (not in CI): leave the service idle longer than the measured network idle timeout, then call it; expect zero `Exception while reading from stream`. Record the result in the story.

### 8.5 Architecture tests (NetArchTest.Rules)

```csharp
public sealed class LayeringTests
{
    [Theory]
    [InlineData(typeof(<Solution>.Domain.AssemblyMarker))]
    [InlineData(typeof(<Solution>.Application.AssemblyMarker))]
    public void Core_layers_do_not_depend_on_Npgsql(Type marker)
    {
        var result = Types.InAssembly(marker.Assembly)
            .ShouldNot().HaveDependencyOnAny("Npgsql", "NpgsqlTypes", "Dapper", "Microsoft.EntityFrameworkCore")
            .GetResult();
        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }
}
```

---

## 9. Epic and stories (for the Scrum Master — shard into story files)

**Epic 1: Upgrade Npgsql to a supported version for PostgreSQL 17 and eliminate stream-read outages**

| Story | Title | Depends on |
| --- | --- | --- |
| 1.1 | Inventory and package upgrade | — |
| 1.2 | Composition root and adapter connection handling | 1.1 |
| 1.3 | Date/time mapping fixes | 1.2 |
| 1.4 | Procedures, type mapping, JSON, logging, obsolete APIs | 1.2 |
| 1.5 | Exception translation and resilience | 1.2 |
| 1.6 | Test suite migration to PostgreSQL 17 | 1.3, 1.4, 1.5 |
| 1.7 | Connection-string hardening and server settings (config + DBA) | 1.1 |
| 1.8 | Observability and dashboards | 1.2 |
| 1.9 | Staged rollout and rollback rehearsal | all |

**Acceptance criteria**

- **1.1** — *Given* the repo, *when* §4 discovery runs, *then* an inventory table (pattern, file, line, decision) is committed in the story; *and* `Directory.Packages.props` pins the §2 versions; *and* `dotnet list package --include-transitive` shows exactly one Npgsql version.
- **1.2** — *Given* the Host, *when* it starts, *then* exactly one `NpgsqlDataSource` per database is registered as a singleton; *and* no `new NpgsqlConnection(` remains in `src/`; *and* adapters flow `CancellationToken` from ports.
- **1.3** — *Given* every `timestamptz` column from §4.3, *when* written and read via its repository, *then* the value round-trips with `Kind=Utc`; *and* every `date` column reads into the intended type without `InvalidCastException`; *and* `EnableLegacyTimestampBehavior` is not set.
- **1.4** — *Given* each database routine, *when* called by the adapter, *then* functions use `SELECT … FROM fn()` and procedures use `CALL`; *and* no use of `GlobalTypeMapper`, `NpgsqlLogManager`, removed `NpgsqlDbType` members or removed connection-string keys remains; *and* build has zero Npgsql obsolete warnings.
- **1.5** — *Given* a transient `NpgsqlException`, *when* an idempotent read runs, *then* it is retried up to 3 times with jittered backoff; *and* Application code catches only Application exceptions; *and* non-idempotent writes are not retried without an idempotency key.
- **1.6** — *Given* CI, *when* tests run, *then* integration tests use `postgres:17` via Testcontainers; *and* architecture tests (§8.5) pass; *and* resilience test §8.4 passes; *and* any changed or removed test is listed with reason.
- **1.7** — *Given* each environment, *when* the service starts, *then* the connection string matches §6.9 with `Keepalive`/`Connection Idle Lifetime` below the measured network idle timeout (value recorded per region); *and* the DBA has confirmed `tcp_keepalives_*`, `idle_session_timeout` (0 or > idle lifetime), `idle_in_transaction_session_timeout`, and that `ANALYZE` ran after the PG17 upgrade.
- **1.8** — *Given* Npgsql 10 metric names, *when* dashboards load, *then* DB latency and connection-count panels show data; *and* an alert fires on any `NpgsqlException` whose inner exception is `TimeoutException`, `IOException` or `EndOfStreamException`.
- **1.9** — *Given* a canary region, *when* deployed in its quietest trading window, *then* 24 h pass with zero stream exceptions before the next region; *and* rollback (redeploy previous build) has been rehearsed in a lower environment.

---

## 10. Guardrails for the Dev agent (do / don't)

**Do**

- Keep all driver code in the persistence adapter; keep port signatures stable.
- Fix production code to satisfy the new driver rules; fix tests only per §7's decision rule.
- Use `await using` for connections, commands, readers.
- Record every deviation (legacy switch, skipped test, version fallback) in the story's Dev Agent Record / change log with a reason and a follow-up ticket.
- Run the full test suite and `dotnet build -warnaserror` before marking a story done.

**Don't**

- Don't reference Npgsql from Domain or Application.
- Don't set `Npgsql.EnableLegacyTimestampBehavior`, `Npgsql.EnableStoredProcedureCompatMode` or the legacy date/time resolver unless the story says so explicitly.
- Don't retry non-idempotent writes (FX order submit, booking) without an idempotency key.
- Don't raise `Command Timeout` globally to hide slow queries; raise it per command with a comment.
- Don't mock `NpgsqlConnection` / `NpgsqlCommand`; use the container.
- Don't upgrade to Npgsql 11 previews or EF Core 10 on .NET 8.
- Don't change database schema in this epic.

---

## 11. Open questions (resolve before story 1.3 starts)

1. Which adapters use EF Core vs Dapper vs plain ADO.NET? (Determines the §2 row.)
2. Measured idle timeout of each network hop (firewall, LB, VPN, NAT) per region.
3. Is the Quartz job store on PostgreSQL (ADO job store) or in-memory?
4. Do any domain value objects already enforce UTC (e.g. `TradeTimestamp`)? If yes, the adapter only maps; if no, `UtcGuard` is the boundary.
5. The full inner exception chain from the outage logs (which of the §5 causes it was).

---

## 12. References

- Npgsql release notes: [6.0](https://npgsql.org/doc/release-notes/6.0.html) · [7.0](https://www.npgsql.org/doc/release-notes/7.0.html) · [8.0](https://npgsql.org/doc/release-notes/8.0.html) · [9.0](https://www.npgsql.org/doc/release-notes/9.0.html) · [10.0](https://www.npgsql.org/doc/release-notes/10.0.html)
- [Npgsql on NuGet](https://www.nuget.org/packages/Npgsql) · [Npgsql.EntityFrameworkCore.PostgreSQL 10.0.2](https://www.nuget.org/packages/npgsql.entityframeworkcore.postgresql/10.0.2)
- [Npgsql keepalive](https://npgsql.org/doc/keepalive.html) · [Connection string parameters](https://www.npgsql.org/doc/connection-string-parameters)
- [CVE-2024-32655](https://advisories.gitlab.com/pkg/nuget/npgsql/CVE-2024-32655/)
- [.NET 8 and .NET 9 end of support](https://devblogs.microsoft.com/dotnet/dotnet-8-9-end-of-support/)
- Companion study: `Npgsql-Upgrade-Study-5.0.16-to-10.0.3.md`
