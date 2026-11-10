# Npgsql Upgrade Study: 5.0.16 → 10.0.3 for .NET 8 on PostgreSQL 17

Nov 10, 2026 · @Parthiban

## Recommendation

**Upgrade to Npgsql 10.0.3**, the latest stable release (27 May 2026). It ships a native net8.0 build, is in active servicing, and is the last major line that will still run on .NET 8.

- **Pin:** `<PackageReference Include="Npgsql" Version="10.0.3" />` (and the matching 10.x of any Npgsql plugins, e.g. Npgsql.EntityFrameworkCore.PostgreSQL 10.0.x if used).
- **Do not** choose Npgsql 11: it is still prerelease (11.0.0-rc.3 of the EF provider on 28 Oct 2026) and its line is built for .NET 11.
- **Fallback:** Npgsql 8.0.9 if the 5 → 10 breaking changes cannot be absorbed in this release window. It is still patched and carries far fewer behaviour changes beyond 6.0.

**The upgrade alone will not guarantee "Exception while reading from stream" never returns.** That message is a wrapper around a network-level failure (timeout, socket reset, end of stream). A newer driver fixes driver bugs, the CVE and PG17 testing gaps; the outage is prevented by pairing it with the connection-string and infrastructure settings in the hardening section below.

**Timing:** .NET 8 reaches end of support today, 10 Nov 2026. Plan .NET 10 as the next release; Npgsql 10.0.3 also targets net10.0, so the driver will not need to change again for that move.

## Why 5.0.16 has to go

Three reasons, each enough on its own.

1. **Known high-severity CVE.** [CVE-2024-32655](https://advisories.gitlab.com/pkg/nuget/npgsql/CVE-2024-32655/) is a SQL-injection flaw via a protocol message size overflow (integer overflow in `WriteBind()`), CVSS 3.1 score 8.1. Every 5.0.x below 5.0.18 is affected, so **5.0.16 is vulnerable**. Fixed lines: 5.0.18, 6.0.11, 7.0.7, 8.0.3 and later.
2. **Out of support.** The 5.x line targets .NET Standard 2.0 and .NET 5 only; no net8.0 build exists. The NuGet servicing list shows patches only for 8.x, 9.x and 10.x (8.0.9, 9.0.5 and 10.0.2 were all released on 12 Mar 2026). Bugs found in 5.x will not be fixed.
3. **Not tested against PostgreSQL 17.** Npgsql aims to stay compatible with PostgreSQL versions still in support, and tests each release against them. PostgreSQL 17 shipped in Sept 2024, three years after 5.0 was frozen, so 5.x never ran in that test matrix.

The network-layer code has also changed a lot since 5.0: a timeout-translation path was removed from `NpgsqlReadBuffer` in 10.0, and connection handling was reworked across 6.0–10.0. Staying on 5.x keeps a 2021-era I/O stack under a 2024 server.

## Version options compared

Npgsql 10.0.3 wins on support runway, PG17 features and .NET 10 readiness. 8.0.9 is the lower-risk fallback.

| Version | Released | Target frameworks | CVE-2024-32655 | PG17 features | Breaking-change load from 5.0 | Verdict |
| --- | --- | --- | --- | --- | --- | --- |
| **10.0.3** | 27 May 2026 | net8.0, net9.0, net10.0 | Fixed | Direct SSL, interval infinity | Highest (6.0 + 7.0 + 8.0 + 9.0 + 10.0) | **Recommended** |
| 9.0.5 | 12 Mar 2026 | .NET 8+ (drops .NET Standard 2.0) | Fixed | Direct SSL, interval infinity (added in 9.0) | High | Skip: no advantage over 10 |
| 8.0.9 | 12 Mar 2026 | net8.0 and .NET Standard 2.0 | Fixed | Runs on PG17, no PG17-specific features | Medium (6.0 + 7.0 + 8.0) | Fallback |
| 11.0 (preview) | Not released | Built for .NET 11 | n/a | PG18 temporal constraints in EF provider | n/a | Not for production |
| 5.0.16 (current) | Before May 2024 | netstandard2.0, net5.0 | **Vulnerable** | None | — | Replace |

Notes:

- Every major from 6.0 to 10.0 is a separate set of breaking changes; going to 8.0.9 now and 10.0.3 later means doing the regression cycle twice.
- Npgsql 10 kept synchronous APIs (`Open()`, `ExecuteNonQuery()`). The team is considering making them throw by default in 11.0, so moving hot paths to async now is a cheap hedge.

## Root cause: what "Exception while reading from stream" means

The message is Npgsql saying the socket read failed; the **inner exception** names the real cause. Pull it from the production logs before closing the incident, because each cause has a different fix.

| Inner exception | What happened | Usual cause | Fix |
| --- | --- | --- | --- |
| `System.TimeoutException: Timeout during reading attempt` | Server sent nothing before `Command Timeout` (default 30 s) | Slow query, lock wait, overloaded server, or **missing planner statistics after a major-version upgrade** | Tune the query or raise `Command Timeout` for that call; run `ANALYZE` after every major upgrade |
| `IOException: Unable to read data from the transport connection … forcibly closed by the remote host` | TCP connection was reset | Firewall, load balancer or NAT dropped an idle pooled connection; server restart or failover | `Keepalive` below the device idle timeout; `Connection Idle Lifetime` shorter than it |
| `EndOfStreamException: Attempted to read past the end of the stream` | Server closed the socket mid-session | Backend terminated (`idle_session_timeout`, `pg_terminate_backend`, failover, crash), or a proxy/SSL mismatch | Check the PostgreSQL log at that timestamp; retry policy for transient errors |
| Any of the above inside `Open()` / `Authenticate()` | Failure during login | SSL or auth mismatch, PgBouncer config, `max_connections` reached | Check `SSL Mode`, auth method, pool sizes vs server limit |

**Check first after a PG upgrade:** `pg_upgrade` does not carry optimizer statistics into PG17, so the planner runs blind until `ANALYZE` completes. Queries that took milliseconds can then exceed 30 s and surface exactly this error. Run `vacuumdb --all --analyze-in-stages` immediately after cut-over in every region.

**Why pooled connections die silently:** a connection idling in the pool has no traffic. A network device in the path (VPN, firewall, cloud LB) may drop it after its idle timeout without notifying either side. The next request gets that dead socket from the pool and fails on the first read. Npgsql's keepalive is **off by default**, so on 5.0.16 with a default connection string nothing detects this.

What to collect from the outage:

- [ ] Full exception chain (inner exception type and message) with timestamps
- [ ] PostgreSQL server log at the same timestamps (`could not receive data from client`, `terminating connection due to idle-session timeout`, `unexpected EOF`)
- [ ] Idle timeout of every network hop between app and DB, per region
- [ ] `pg_stat_activity` and `max_connections` usage at the time
- [ ] Whether `ANALYZE` ran after the PostgreSQL upgrade

## Hardening: connection string and server settings

The settings below close the dead-pooled-connection and timeout gaps; ship them in the same release as 10.0.3. Values are starting points: set `Keepalive` and `Connection Idle Lifetime` **below the shortest idle timeout of any network hop** in each region.

```
Host=<host>;Port=5432;Database=<db>;Username=<user>;Password=<from vault>;
Application Name=FXOrder-API;
Pooling=true;Minimum Pool Size=0;Maximum Pool Size=50;
Timeout=15;Command Timeout=30;
Keepalive=30;Tcp Keepalive=true;
Connection Idle Lifetime=120;Connection Pruning Interval=10;Connection Lifetime=3600;
SSL Mode=VerifyFull;Root Certificate=/path/ca.pem;
GSS Encryption Mode=Disable
```

| Parameter | Npgsql default | Recommended | Why |
| --- | --- | --- | --- |
| `Keepalive` | 0 (off) | 30 s | Pings idle connections so dead ones are found and evicted before a request uses them |
| `Tcp Keepalive` | false | true | OS-level probes; the only check that works **while a query runs** (long Quartz jobs). On Linux it uses the system `tcp_keepalive_*` values |
| `Connection Idle Lifetime` | 300 s | 120 s | Closes idle connections above `Minimum Pool Size` before a firewall or LB silently drops them |
| `Connection Lifetime` | 3600 s since 9.0 (0 in 5.x) | 3600 s | Recycles connections so failover and node changes are picked up; limits per-backend cache growth |
| `Command Timeout` | 30 s | 30 s, raised per command for known long reports | Fail fast on hot paths, without killing legitimate batch work |
| `Maximum Pool Size` | 100 | Sized so instances × pool ≤ `max_connections` minus a reserve | Pool exhaustion surfaces as open timeouts, not stream errors |
| `GSS Encryption Mode` | Prefer (new in 10.0) | Disable, unless you use Kerberos | Avoids an extra negotiation attempt and the `libgssapi_krb5.so.2` warning on Linux |
| `Application Name` | none | Service name | Lets the DBA tie `pg_stat_activity` and server-log lines to your API |

**Code-level practices**

- Build one `NpgsqlDataSource` per database as a singleton (`builder.Services.AddNpgsqlDataSource(connString)` from Npgsql.DependencyInjection), not a new connection string per request.
- Wrap DB calls in a retry policy that retries only when `NpgsqlException.IsTransient` is true (Polly, or `EnableRetryOnFailure()` if you use EF Core). Never retry a non-idempotent write blindly: FX order submits need an idempotency key.
- Always `await using` connections, commands and readers so connections return to the pool promptly.

**PostgreSQL 17 server settings to agree with the DBA**

| Setting | Suggested | Purpose |
| --- | --- | --- |
| `tcp_keepalives_idle` / `_interval` / `_count` | 60 / 10 / 5 | Server finds dead clients in about 2 minutes, not the OS default of 2 hours |
| `client_connection_check_interval` | 10s | Aborts queries whose client has gone away (PG14+) |
| `idle_session_timeout` | 0, or longer than `Connection Idle Lifetime` | A shorter value kills pooled connections and causes `EndOfStreamException` |
| `idle_in_transaction_session_timeout` | 60s | Stops abandoned transactions holding locks, which cause read timeouts elsewhere |
| Post-upgrade | `vacuumdb --all --analyze-in-stages` | Restores planner statistics after `pg_upgrade` |

## Breaking changes from 5.0 to 10.0: audit checklist

The **6.0 date/time change is the highest risk** for FX and Money Market code, because trade, value and settlement timestamps are everywhere. Grep the codebase for each item; tick it when tested.

| Introduced | Change | What breaks | Action / opt-out |
| --- | --- | --- | --- |
| 6.0 | `timestamptz` maps strictly to `DateTime` Kind=Utc; `timestamp` to Local/Unspecified. No implicit conversion | Writing a Local `DateTime` to `timestamptz` (or Utc to `timestamp`) throws; reads of `timestamptz` now come back as UTC, not server-local | Make all `timestamptz` writes UTC. Temporary opt-out: `AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true)` at startup |
| 6.0 | Only offset-0 `DateTimeOffset` can be written; `DbType.DateTime` → `timestamptz` | Non-UTC offsets throw | Convert with `.ToUniversalTime()` |
| 6.0 | `DateTime.MinValue/MaxValue` ↔ `-infinity`/`infinity` | Sentinel dates behave differently | `Npgsql.DisableDateTimeInfinityConversions` switch if needed |
| 6.0 | `interval` with months/years can't be read as `TimeSpan` | Tenor columns stored as intervals throw | Read as `NpgsqlInterval`, or store days |
| 6.0 | Dapper needs 2.0.123+ | Older Dapper fails on UTC `DateTime` | Upgrade Dapper |
| 7.0 | Custom logging (`NpgsqlLogManager`) removed | Compile errors | Use `ILoggerFactory` via the data source builder |
| 7.0 | `CommandType.StoredProcedure` issues `CALL` (procedures), not `SELECT` (functions) | Calls to functions via StoredProcedure fail | Use `SELECT * FROM fn(...)` text, or `Npgsql.EnableStoredProcedureCompatMode` |
| 7.0 | Connection-level type mapping removed; `GlobalTypeMapper` obsolete | Enum/composite mappings stop working | Map on `NpgsqlDataSourceBuilder` (`MapEnum<T>()`) |
| 7.0 | `NpgsqlDataReader.Dispose` now throws previously swallowed errors | Hidden failures surface | Treat as a bug fix; review catch blocks |
| 8.0 | JSON POCO, unmapped enums, records-as-tuples need opt-in | Runtime exceptions | `EnableDynamicJson()` etc. on the builder |
| 8.0 | `SSL Mode=Require` no longer validates the certificate | Silent loss of validation if you relied on it | Use `VerifyFull` / `VerifyCA` |
| 8.0 | `Internal Command Timeout` obsolete; `Integrated Security` removed | Connection-string warnings or errors | Remove them |
| 9.0 | `Connection Lifetime` defaults to 1 hour | Connections recycle hourly | Usually desirable; keep |
| 9.0 | `Server Compatibility Mode`, `Load Table Composites` obsolete | Warnings | Use builder `ConfigureTypeLoading` |
| 10.0 | `date`/`time` read as `DateOnly`/`TimeOnly` in `GetValue()` / `ExecuteScalar()` | Casts like `(DateTime)reader.GetValue(i)` throw on value-date columns | Use `GetFieldValue<DateTime>()`, or register `LegacyDateAndTimeResolverFactory` |
| 10.0 | GSS encryption tried by default | Extra attempt / Linux warning | `GSS Encryption Mode=Disable` |
| 10.0 | With `Root Certificate` set, only that CA is trusted (not the system store) | TLS failures if the chain needs an intermediate from the OS store | Supply the full CA bundle |
| 10.0 | Metric names and tracing tags follow OpenTelemetry conventions | Grafana / APM dashboards go blank | Update dashboard queries |
| 10.0 | `cidr` maps to `IPNetwork`; `PostgresException.BatchCommand` null by default | Rare | `IncludeFailedBatchedCommand=true` if needed |

Reference: release notes for [6.0](https://npgsql.org/doc/release-notes/6.0.html), [7.0](https://www.npgsql.org/doc/release-notes/7.0.html), [8.0](https://npgsql.org/doc/release-notes/8.0.html), [9.0](https://www.npgsql.org/doc/release-notes/9.0.html), [10.0](https://www.npgsql.org/doc/release-notes/10.0.html).

## Rollout, testing and rollback

Ship as one release per region, canary first. The driver change needs no schema change, so rollback is a redeploy of the previous build.

1. **Code change.** Bump Npgsql (and plugins) to 10.0.3, fix compile errors, move to a singleton `NpgsqlDataSource`, apply the hardened connection string. Enable `Npgsql.EnableLegacyTimestampBehavior` only if the date/time audit cannot finish in this window, and log a ticket to remove it.
2. **Integration tests against real PostgreSQL 17** (Testcontainers `postgres:17`), not mocks:
   - Round-trip every `timestamp`, `timestamptz`, `date`, `interval` and `numeric` column used in FX Order, RFQ, Streaming, Money Market and Options code.
   - Stored procedure and function calls.
   - Quartz jobs end to end, including the longest-running one.
3. **Resilience tests in a lower environment that has the production network path** (same firewall/LB):
   - Leave the app idle longer than the network idle timeout, then send traffic: expect zero stream exceptions.
   - `SELECT pg_terminate_backend(pid)` on pooled connections mid-traffic: expect transient errors retried, no outage.
   - Restart or fail over the PG17 primary: expect recovery within the retry budget.
   - Load test at peak FX volume for at least 1 hour (covers one `Connection Lifetime` cycle).
4. **DB side before cut-over:** `ANALYZE` done, keepalive and timeout settings agreed, `max_connections` checked against total pool sizes.
5. **Canary:** one region first, in its quietest trading window; watch for 24 hours.
6. **Remaining regions,** one at a time.

**Watch during and after each deploy**

- Count of `NpgsqlException` by inner exception type (target: zero stream exceptions outside planned DB restarts)
- `db.client.operation.duration` p95/p99 and `db.client.connection.count` vs max (new Npgsql 10 metric names)
- PostgreSQL log for `connection reset` / `unexpected EOF`

**Rollback trigger:** any recurrence of the stream exception with an unexplained cause, or a date/time data mismatch. Redeploy the previous build. Keep the keepalive, idle-lifetime, lifetime and pool keys; revert the SSL and GSS keys to what 5.0.16 used, as older versions may not accept them.

## Sources

- [Npgsql on NuGet (versions, target frameworks)](https://www.nuget.org/packages/Npgsql)
- [Npgsql 10.0.3 package page](https://www.nuget.org/packages/npgsql/10.0.3)
- [Npgsql 10.0 release notes](https://www.npgsql.org/doc/release-notes/10.0.html)
- [Npgsql 9.0 release notes](https://www.npgsql.org/doc/release-notes/9.0.html)
- [Npgsql 8.0 release notes](https://npgsql.org/doc/release-notes/8.0.html)
- [Npgsql 7.0 release notes](https://www.npgsql.org/doc/release-notes/7.0.html)
- [Npgsql 6.0 release notes](https://npgsql.org/doc/release-notes/6.0.html)
- [Npgsql compatibility notes](https://www.npgsql.org/doc/compatibility.html)
- [Npgsql keepalive documentation](https://npgsql.org/doc/keepalive.html)
- [Npgsql connection string parameters](https://www.npgsql.org/doc/connection-string-parameters)
- [Npgsql.EntityFrameworkCore.PostgreSQL 11.0 prerelease list](https://www.nuget.org/packages/Npgsql.EntityFrameworkCore.PostgreSQL/11.0.0-preview.1)
- [CVE-2024-32655 advisory (GitLab)](https://advisories.gitlab.com/pkg/nuget/npgsql/CVE-2024-32655/)
- [.NET 8 and .NET 9 end of support (Microsoft .NET Blog)](https://devblogs.microsoft.com/dotnet/dotnet-8-9-end-of-support/)
- [Dead connection handling in PostgreSQL (AWS)](https://docs.amazonaws.cn/AmazonRDS/latest/UserGuide/Appendix.PostgreSQL.CommonDBATasks.DeadConnectionHandling.html)
- [idle_session_timeout explained](https://thebuild.com/blog/all-your-gucs-in-a-row-idle_session_timeout/)
