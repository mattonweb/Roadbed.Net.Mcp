# Roadbed.DbQueue Reference

A reusable, MySQL/MariaDB-backed queue library. Multiple sites (web request handlers, background services, SDKs) push typed request records into a database during a request — a cheap `INSERT`, no inline work — and a single-consumer background job drains them later. Generic over the payload type `T`: a typed *queue definition* binds a queue name to its payload type and to the connection that reaches the schema that hosts its tables, an `EnqueueAsync(payload)` appends a message and returns a shareable tracking id, and a `QueueProcessor<T>` claims a FIFO batch, deserializes each payload, hands it to a caller-supplied delegate, and records the per-message outcome.

The library is **processing + enqueue only**. It ships no Quartz dependency, no job classes, and no scheduling wiring. The consuming host runs `ProcessBatchAsync` from a `BaseSchedulingJob<T>` marked `[DisallowConcurrentExecution]`.

The package layout mirrors `Roadbed.Logging` + `Roadbed.Logging.MySql`:

- **`Roadbed.DbQueue`** (core, provider-neutral): the public surface plus an internal execution port. No database-client dependency.
- **`Roadbed.DbQueue.MySql`** (satellite): the executor adapter, the auto-discovered installer, and the reference DDL templates as resource assets.

Reference exactly **one** Roadbed.DbQueue provider package per host.

## Architectural model — two-table immutable + processed companion

This library uses the **immutable-message + processed-companion** model:

- **`queue_message_{q}`** is append-only: each enqueue inserts one row; the library never `UPDATE`s or `DELETE`s it.
- **`queue_processed_{q}`** is also append-only: each dispatch attempt inserts one row carrying `is_processed_successfully = 1` (success) or `0` (failure).

This deliberately diverges from the portfolio's existing **single-table-with-status** queues (one row per item, mutated through a status column). New queues use **this** model; existing bespoke status-column queues stay as they are unless separately migrated. Do not "unify" the two patterns by accident.

## Type catalog

| Type                             | Kind            | Namespace                       | Purpose                                                                                                  |
| -------------------------------- | --------------- | ------------------------------- | -------------------------------------------------------------------------------------------------------- |
| `QueueDefinition<T>`             | Sealed class    | `Roadbed.DbQueue`               | Validated queue name + per-queue `IDataConnectionFactory` + derived `queue_message_{name}` / `queue_processed_{name}` identifiers. |
| `QueueProcessor<T>`              | Sealed class    | `Roadbed.DbQueue`               | Enqueue + drain engine; dual ctor (public resolves executor via `ServiceLocator`, internal takes it directly for tests). |
| `QueueMessage<T>`                | Sealed class    | `Roadbed.DbQueue`               | What a handler receives: `Id`, `ExternalId`, `CreatedOn` (UTC), `Payload`.                               |
| `QueueMessageHandler<T>`         | Delegate        | `Roadbed.DbQueue`               | `Task QueueMessageHandler<T>(QueueMessage<T>, CancellationToken)` — the per-message processor.            |
| `QueueProcessResult`             | Sealed class    | `Roadbed.DbQueue`               | `Attempted` / `Succeeded` / `Failed` counts returned from `ProcessBatchAsync`.                            |
| `QueueNameValidator`             | Static class    | `Roadbed.DbQueue` (internal)    | Strict whitelist (`^[a-z0-9_]+$`, ≤ 48 chars). Surfaced at `QueueDefinition<T>` construction.            |
| `IDbQueueDataExecutor`           | Interface       | `Roadbed.DbQueue` (internal)    | Provider-neutral execution port. Implemented by a satellite; the core has no DB-client dependency.       |
| `MySqlDbQueueDataExecutor`       | Sealed class    | `Roadbed.DbQueue.MySql`         | Thin adapter over `MySqlExecutor` — the one-to-one analogue of `MySqlLoggingDataExecutor`.               |
| `InstallDbQueueMySql`            | Sealed class    | `Roadbed.DbQueue.MySql`         | Auto-discovered `IServiceCollectionInstaller`. Registers the executor; does **not** register queues.     |

## MUST

- **MUST** reference exactly **one** provider package — currently `Roadbed.DbQueue.MySql` — alongside the core `Roadbed.DbQueue`. Core itself has no MySqlConnector dependency.
- **MUST** create the queue's two tables `queue_message_{q}` and `queue_processed_{q}` against the queue's business schema **before** the host starts. The library runs **no** DDL at any time. Use the reference templates shipped under `src/Roadbed.DbQueue.MySql/Assets/Tables/queue_message/install_mysql.txt` and `.../queue_processed/install_mysql.txt`; substitute the literal `{q}` placeholder with the queue's logical name.
- **MUST** keep `queue_message_{q}.external_id` declared as `VARCHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL`. The library surfaces `ExternalId` as `string` end-to-end; `CHAR(36)` collides with `MySqlConnector`'s default `GuidFormat=Char36` and the driver hands back a `System.Guid`, crashing Dapper materialization (`Convert.ChangeType(Guid, string)` → "Object must implement IConvertible"). `VARCHAR` falls into a different MySQL protocol-type branch and is never coerced. Hosts that deployed from the original `CHAR(36)` template must run the one-time `ALTER` shipped at `Assets/Tables/upgrade_2026-06_external_id_varchar_mysql.txt`.
- **MUST** keep each queue in the business schema it serves — not a dedicated queue schema, not the logging schema. The host registers a per-schema marker `IFooDatabaseFactory : IDataConnectionFactory` (standard `reference-roadbed-data.md` pattern) and passes that marker to `QueueDefinition<T>`. Multiple queues in the same host can therefore live in different databases without the library managing connection strings.
- **MUST** validate queue names through `QueueDefinition<T>`'s constructor. Names are library-/host-controlled (never user-supplied), lowercased ASCII letters + digits + underscore only (`^[a-z0-9_]+$`), and bounded to 48 characters so `queue_processed_{q}` stays inside MySQL's 64-character identifier limit. The constructor throws `ArgumentException` before any SQL string is built.
- **MUST** serialize payloads with the shared `Roadbed.RoadbedJson.Options`. The library itself does this on enqueue and claim; consuming code that constructs payload POCOs should annotate properties with `[JsonPropertyName(...)]` (System.Text.Json), not `[JsonProperty]`.
- **MUST** drive `ProcessBatchAsync` from a **single-consumer**, non-overlapping `BaseSchedulingJob<T>` marked `[DisallowConcurrentExecution]`. The processor assumes one instance per queue at a time — there is no `SKIP LOCKED`, no row-claim lock, no visibility timeout, no in-flight state.
- **MUST** treat the returned UUIDv7 `external_id` from `EnqueueAsync` as the shareable handle for confirmation URLs, support lookups, and cross-system correlation. The internal auto-increment `id` is never surfaced.
- **MUST** make handlers **idempotent**. A failed message is reprocessed **only** when an operator deletes its processed row externally, at which point the anti-join re-claims the original message — the same payload, the same delivery.
- **MUST** monitor `QueueProcessResult.Failed` and the Error-level log records for handler failures. The library does **not** auto-retry — a `is_processed_successfully = 0` row means "attempted, needs manual investigation". A silently-failed compliance message (an unsubscribe that never took) is a legal risk; the host is responsible for alerting on it.
- **MUST** monitor for **stale-unprocessed** messages: a message still unprocessed when its month partition reaches the retention boundary is **silently dropped** on `DROP PARTITION`. The library cannot see this (it does no retention). Page the operator on backlog growth before the boundary.
- **MUST** drop month partitions in the correct order on retention sweeps: the **message** table partition **FIRST**, then the **processed** partition. Reverse order orphans messages back into the claim pool and re-runs the handler.

## MUST NOT

- **MUST NOT** point the queue's `IDataConnectionFactory` at the logging schema or a dedicated "queue" schema. Each queue lives in the schema it serves.
- **MUST NOT** issue `UPDATE` or `DELETE` against either queue table from the library or from custom helpers built on top of it. The two tables are append-only by design — replay is a processed-row delete performed externally by an operator.
- **MUST NOT** add a foreign key from `queue_processed_{q}.fk_queue_id` to `queue_message_{q}.id`. Partitioned InnoDB tables cannot carry FKs; `fk_queue_id` is a **logical** reference, indexed but unenforced. The reference DDL omits the FK.
- **MUST NOT** add a standalone `UNIQUE (external_id)` on the message table or `UNIQUE (fk_queue_id)` on the processed table. Every unique key on a partitioned InnoDB table must contain the partition column, so the templates ship composite uniques (`external_id, created_on`) and (`fk_queue_id, processed_on`). The composite processed-row unique is "weakened to one row per message per month"; the **app-level anti-join** in `ProcessBatchAsync` is the real idempotency guard.
- **MUST NOT** allocate a `JsonSerializerOptions` per call when constructing payloads, helpers, or test fixtures. Reuse `Roadbed.RoadbedJson.Options` — STJ keys its reflection-derived metadata cache by options instance, and the library round-trips through that one cached instance.
- **MUST NOT** mint the `external_id` yourself or pass one to `EnqueueAsync`. The library mints it (`Guid.CreateVersion7().ToString("D")`) and returns it; passing your own would create two sources of truth and break the FIFO-friendly UUIDv7 ordering guarantee.
- **MUST NOT** invent your own retry layer around `ProcessBatchAsync`. A failed row is **never** auto-retried; the library is explicit about that and so should consumer code be.
- **MUST NOT** run two `QueueProcessor<T>` instances for the same queue concurrently. The job that calls `ProcessBatchAsync` carries `[DisallowConcurrentExecution]`; moving to multi-consumer is a flagged design change (the library would need `SKIP LOCKED` and a claim lock), not a silent reconfiguration.
- **MUST NOT** rely on the `is_processed_successfully` flag as a queue-status indicator. The presence of a processed row already means "tried"; the flag distinguishes outcome. Reading the message table for "what's pending" is the anti-join, not a status column.
- **MUST NOT** depend on `Roadbed.Messaging` from `Roadbed.DbQueue`-aware code. Cloud-broker envelopes are a different library; this queue is in-database.

## Code patterns

### 1. Marker connection factory for the queue's business schema

Standard `reference-roadbed-data.md` pattern, scoped per business schema. One marker per schema, regardless of how many queues live in it.

```csharp
namespace Foo.Subscriptions;

using Roadbed.Data;
using Roadbed.Data.MySql;

/// <summary>Marker for the Foo.Subscriptions MySQL schema (hosts the unsubscribe queue).</summary>
public interface IFooSubscriptionsDatabaseFactory : IDataConnectionFactory
{
}

/// <summary>Concrete factory for the Foo.Subscriptions schema.</summary>
public sealed class FooSubscriptionsDatabaseFactory(DataConnecionString connection)
    : MySqlConnectionFactory(connection), IFooSubscriptionsDatabaseFactory
{
}
```

### 2. Database setup — substitute `{q}` and run

Each new queue requires the DBA to create two pre-partitioned tables in the target schema. Substitute the literal `{q}` in both templates with the same queue name you pass to `QueueDefinition<T>`, then run the scripts against the matching schema.

```sql
-- Pick the business schema the queue lives in.
USE foo_subscriptions;

-- queue_message_{q} template — paste with {q} replaced by "foo_unsubscribe".
-- queue_processed_{q} template — paste with {q} replaced by "foo_unsubscribe".
```

The templates ship monthly RANGE partitioning (`p_min` floor, 120 monthly partitions 2026-01..2035-12, `pmax` catch-all), composite keys, UTC defaults (`UTC_TIMESTAMP(6)`), and zero foreign keys. Header comments call out the 12-month default retention window and the DROP-PARTITION ordering rule (message FIRST, processed SECOND).

### 3. Host wiring — installer + per-queue `QueueProcessor<T>` registration

`InstallDbQueueMySql` auto-discovers and registers the satellite's `IDbQueueDataExecutor` plus snaps the snapshot into `ServiceLocator`. The host registers the per-schema marker factory and one `QueueProcessor<T>` singleton per queue.

```csharp
// Program.cs
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Roadbed;
using Roadbed.Data;
using Roadbed.DbQueue;
using Foo.Subscriptions;
using Foo.Subscriptions.Queues;

var builder = Host.CreateApplicationBuilder(args);

// 1. Register the per-schema marker factory the queue lives behind.
var conn = new DataConnecionString(DataConnectionStringType.MySQL)
{
    ServerName     = "db.internal",
    DatabaseSource = "foo_subscriptions",
    Username       = "app",
    Password       = builder.Configuration["FooSubscriptions:Password"],
};
builder.Services.AddSingleton<IFooSubscriptionsDatabaseFactory>(
    new FooSubscriptionsDatabaseFactory(conn));

// 2. Register one QueueProcessor<T> singleton per queue. The QueueDefinition<T>
//    is constructed inside the DI factory so the name is whitelist-validated
//    at host startup, not at first call.
builder.Services.AddSingleton(sp =>
{
    var factory = sp.GetRequiredService<IFooSubscriptionsDatabaseFactory>();
    var logger  = sp.GetRequiredService<ILogger<QueueProcessor<FooUnsubscribePayload>>>();
    var definition = new QueueDefinition<FooUnsubscribePayload>("foo_unsubscribe", factory);
    return new QueueProcessor<FooUnsubscribePayload>(definition, logger);
});

// 3. Auto-discovery picks up InstallDbQueueMySql (the provider satellite's
//    installer) and any other Roadbed installers in loaded assemblies.
builder.Services.InstallModulesInAppDomain(builder.Configuration);

using var host = builder.Build();
await host.RunAsync();
```

If two queues share the same payload type `T`, register them as keyed singletons (`services.AddKeyedSingleton(...)`) rather than two singletons of the same closed generic.

### 4. Define the payload POCO with `[JsonPropertyName]`

```csharp
namespace Foo.Subscriptions.Queues;

using System.Text.Json.Serialization;

/// <summary>Payload for the foo_unsubscribe queue.</summary>
public sealed class FooUnsubscribePayload
{
    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("list_id")]
    public long ListId { get; set; }

    [JsonPropertyName("campaign_key")]
    public string? CampaignKey { get; set; }
}
```

### 5. Enqueue path — from a web request

`EnqueueAsync` returns the UUIDv7 external id. Surface it to the caller for confirmation URLs or support lookups.

```csharp
namespace Foo.Subscriptions.Web;

using Microsoft.AspNetCore.Mvc;
using Roadbed.DbQueue;
using Foo.Subscriptions.Queues;

[ApiController]
[Route("api/unsubscribe")]
public sealed class UnsubscribeController : ControllerBase
{
    private readonly QueueProcessor<FooUnsubscribePayload> _queue;

    public UnsubscribeController(QueueProcessor<FooUnsubscribePayload> queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        this._queue = queue;
    }

    [HttpPost]
    public async Task<IActionResult> PostAsync(
        [FromBody] FooUnsubscribePayload payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        string externalId = await this._queue.EnqueueAsync(payload, cancellationToken);
        return this.Accepted(new { trackingId = externalId });
    }
}
```

### 6. Drain path — single-consumer `BaseSchedulingJob<T>`

The handler is a delegate. A `Task` return means success; a throw means failure. The library catches the throw per-message, records a processed row with `is_processed_successfully = 0`, logs at Error with the queue name + external id, and continues the batch.

```csharp
namespace Foo.Subscriptions.Jobs;

using Microsoft.Extensions.Logging;
using Quartz;
using Roadbed;
using Roadbed.DbQueue;
using Roadbed.Scheduling;
using Foo.Subscriptions.Queues;
using Foo.Subscriptions.Services;

/// <summary>
/// Drains the foo_unsubscribe queue. Single-consumer per the Roadbed.DbQueue
/// contract — DO NOT remove [DisallowConcurrentExecution].
/// </summary>
[DisallowConcurrentExecution]
public sealed class FooUnsubscribeDrainJob : BaseSchedulingJob<FooUnsubscribeDrainJob>
{
    private const int BatchSize = 250;

    private readonly QueueProcessor<FooUnsubscribePayload> _queue;
    private readonly IUnsubscribeService _unsubscribeService;

    public FooUnsubscribeDrainJob(
        QueueProcessor<FooUnsubscribePayload> queue,
        IUnsubscribeService unsubscribeService,
        ILogger<FooUnsubscribeDrainJob> logger)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(unsubscribeService);
        this._queue = queue;
        this._unsubscribeService = unsubscribeService;
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        QueueProcessResult result = await this._queue.ProcessBatchAsync(
            BatchSize,
            (msg, ct) => this._unsubscribeService.MarkAsync(msg.Payload, ct),
            cancellationToken);

        this.Context.Result =
            $"attempted={result.Attempted} succeeded={result.Succeeded} failed={result.Failed}";

        if (result.Failed > 0)
        {
            this.LogWarning(
                "Drain completed with {Failed} handler failures out of {Attempted}; investigate processed rows with is_processed_successfully = 0.",
                result.Failed,
                result.Attempted);
        }
    }
}
```

The handler reference (`(msg, ct) => ...`) is the `QueueMessageHandler<T>` delegate — there is no `IQueueMessageHandler<T>` interface to implement.

### 7. Idempotent handler shape

A failed handler is replayed only by an operator deleting the processed row, at which point the same payload is re-dispatched. The handler must therefore short-circuit on already-applied work.

```csharp
public async Task MarkAsync(FooUnsubscribePayload payload, CancellationToken ct)
{
    ArgumentNullException.ThrowIfNull(payload);

    // Idempotency: if this address is already unsubscribed from this list,
    // a replay must be a no-op, not a duplicate audit-log entry.
    if (await this._repository.IsUnsubscribedAsync(payload.Email, payload.ListId, ct))
    {
        return;
    }

    await this._repository.UnsubscribeAsync(payload.Email, payload.ListId, payload.CampaignKey, ct);
}
```

## Common pitfalls

### Treating a `is_processed_successfully = 0` row as auto-retried

```csharp
// ❌ Wrong mental model: "the library will pick it up next sweep."
// A processed row — regardless of flag — excludes the message from EVERY
// future batch via the anti-join. The library never auto-retries.

// ✅ Failed rows surface to the host: alert on QueueProcessResult.Failed
//    and on the Error-level log records. Replay is operator-driven (delete
//    the processed row; the message is re-claimed; the handler runs again).
```

### Dropping the processed partition before the message partition

```sql
-- ❌ Wrong order — the surviving message rows lose their processed companion,
-- the anti-join re-claims them, and the handler runs months later on data
-- the operator considered settled.
ALTER TABLE queue_processed_foo_unsubscribe DROP PARTITION p_202501;
ALTER TABLE queue_message_foo_unsubscribe   DROP PARTITION p_202501;

-- ✅ Correct order — message FIRST, processed SECOND. Orphaned processed
-- rows remaining in the processed table are harmless until their own
-- partition is dropped.
ALTER TABLE queue_message_foo_unsubscribe   DROP PARTITION p_202501;
ALTER TABLE queue_processed_foo_unsubscribe DROP PARTITION p_202501;
```

### Reading `id` as the shareable handle

```csharp
// ❌ The internal auto-increment id is never surfaced. Don't reach for it
// via a custom SELECT either — a future schema change is free to renumber.
long id = await db.QuerySingleAsync<long>("SELECT id FROM queue_message_foo_unsubscribe ORDER BY id DESC LIMIT 1");
return Url.Action("Confirm", new { id });

// ✅ EnqueueAsync returns the UUIDv7 external_id — the shareable, stable handle.
string externalId = await queue.EnqueueAsync(payload, ct);
return Url.Action("Confirm", new { trackingId = externalId });
```

### Forgetting `[DisallowConcurrentExecution]`

```csharp
// ❌ Without the attribute, Quartz can start a second instance while the
// first is still draining. Both anti-joins claim the same message and the
// handler runs twice — the per-month DB unique lets only one processed row
// land per partition, but the side effect has already happened twice.
public sealed class FooUnsubscribeDrainJob : BaseSchedulingJob<FooUnsubscribeDrainJob> { ... }

// ✅ Single-consumer is the contract. The attribute enforces it.
[DisallowConcurrentExecution]
public sealed class FooUnsubscribeDrainJob : BaseSchedulingJob<FooUnsubscribeDrainJob> { ... }
```

### Declaring `external_id` as `CHAR(36)`

```sql
-- ❌ Looks idiomatic for a fixed-width UUIDv7 string, but MySqlConnector
-- 2.6+ defaults to GuidFormat=Char36, under which the driver returns any
-- CHAR column declaring exactly 36 characters as System.Guid. Dapper then
-- tries to assign that Guid to ClaimedMessageRow.ExternalId (a string),
-- falls back to Convert.ChangeType(Guid → string), and throws
-- "Object must implement IConvertible". The queue stays stuck on row 1.
,external_id CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL

-- ✅ The shipped template uses VARCHAR(36). VARCHAR maps to
-- MYSQL_TYPE_VAR_STRING, a different TypeMapper branch — never coerced
-- to Guid regardless of the consumer's GuidFormat. Lexical-equals-
-- chronological UUIDv7 ordering under ascii_bin is preserved (byte-wise
-- comparison is identical for CHAR and VARCHAR on fixed-width 36-char
-- values).
,external_id VARCHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL
```

### Adding a foreign key in the reference DDL

```sql
-- ❌ Partitioned InnoDB tables cannot carry foreign keys; the CREATE TABLE
-- itself fails.
CREATE TABLE queue_processed_foo_unsubscribe (
    ...
    ,CONSTRAINT fk_processed_message
        FOREIGN KEY (fk_queue_id) REFERENCES queue_message_foo_unsubscribe (id)
) PARTITION BY RANGE (TO_DAYS(processed_on)) (...);

-- ✅ fk_queue_id is a plain BIGINT UNSIGNED with an index. The reference
-- back to queue_message_{q}.id is a LOGICAL reference, not an enforced
-- constraint. The shipped templates already omit the FK.
```

### Passing a custom `external_id` to `EnqueueAsync`

There is no overload that accepts one. The library mints `Guid.CreateVersion7().ToString("D")` and returns it — UUIDv7's first 48 bits are a big-endian millisecond timestamp, so lexical order on `external_id` equals chronological order under `ascii_bin`, which keeps the column usable for range scans even though the primary FIFO order is the auto-increment `id`.

### Allocating `JsonSerializerOptions` in a handler or test fixture

```csharp
// ❌ Per-call options thrash STJ's reflection cache. The library uses the
// shared RoadbedJson.Options for serialize/deserialize; consumer-side code
// that touches the same payloads must do the same.
var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
var payload = JsonSerializer.Deserialize<FooUnsubscribePayload>(json, options);

// ✅ One shared, frozen options instance for the whole process.
var payload = JsonSerializer.Deserialize<FooUnsubscribePayload>(json, RoadbedJson.Options);
```

### Reaching into the message table for "what's pending"

```sql
-- ❌ Pretends nothing has been attempted; double-counts every row whose
-- processed companion already exists.
SELECT COUNT(*) FROM queue_message_foo_unsubscribe;

-- ✅ The anti-join is the source of truth for "pending":
SELECT COUNT(*)
FROM queue_message_foo_unsubscribe AS m
LEFT JOIN queue_processed_foo_unsubscribe AS p ON p.fk_queue_id = m.id
WHERE p.fk_queue_id IS NULL;
```

## Quick reference

### Using statements

```csharp
using Roadbed;                         // RoadbedJson, ServiceLocator, IServiceCollectionInstaller, BaseClassWithLogging
using Roadbed.Data;                    // IDataConnectionFactory, DataConnecionString
using Roadbed.DbQueue;                 // QueueDefinition<T>, QueueProcessor<T>, QueueMessage<T>, QueueMessageHandler<T>, QueueProcessResult
using Roadbed.DbQueue.MySql;           // InstallDbQueueMySql — only in the host's Program.cs, the satellite picks itself up by auto-discovery
```

### Decision flow for adding a new queue

```
1. Pick the queue's business schema; ensure its IFooDatabaseFactory marker is registered.
2. Pick a queue name: ^[a-z0-9_]+$, ≤ 48 chars. Plural lowercase reads well (e.g. "foo_unsubscribe").
3. DBA runs the two reference DDL templates against that schema with {q} substituted.
4. Define the payload POCO (System.Text.Json [JsonPropertyName] attributes).
5. Register the QueueProcessor<T> singleton in DI (factory closure that wires
   the QueueDefinition<T> + the schema's marker factory + the typed logger).
6. Inject QueueProcessor<T> wherever you enqueue.
7. Write a [DisallowConcurrentExecution] BaseSchedulingJob<T> that calls
   ProcessBatchAsync(batchSize, handler, ct). Make the handler idempotent.
8. Wire host alerting on QueueProcessResult.Failed > 0 and on Error-level
   logs from the QueueProcessor<T> category. Wire a separate alert on
   stale-unprocessed backlog (rows whose created_on month is approaching
   the retention boundary).
```

### Acceptance shape of a new queue (the spec's success bar)

| # | Requirement                                                                                   |
| - | --------------------------------------------------------------------------------------------- |
| 1 | `EnqueueAsync(payload)` mints a UUIDv7 external id and returns it.                            |
| 2 | Exactly one `queue_processed_{q}` row per attempted message, with the correct success flag.   |
| 3 | A throwing handler → flag = 0, logged at Error, batch continues, not auto-retried.            |
| 4 | Already-processed messages (success or failure) are never re-selected (anti-join).            |
| 5 | FIFO order — ascending `id`.                                                                  |
| 6 | Library does no DDL and no `UPDATE`/`DELETE` on either table.                                 |
| 7 | Invalid queue names rejected at `QueueDefinition<T>` construction, before any SQL is built.   |
| 8 | Tables are partitioned with composite keys; SQL prunes on the partition column where it filters. |

### Files inside Roadbed.DbQueue.MySql

```
src/Roadbed.DbQueue.MySql/
├── Assets/Tables/queue_message/install_mysql.txt     ← reference DDL; substitute {q}
├── Assets/Tables/queue_processed/install_mysql.txt   ← reference DDL; substitute {q}
├── InstallDbQueueMySql.cs                            ← auto-discovered installer
└── MySqlDbQueueDataExecutor.cs                       ← adapter over MySqlExecutor
```

### Single-consumer reminder

The processor assumes one instance per queue at a time. There is no `SKIP LOCKED`, no row-claim lock, no visibility timeout, no in-flight state. If two processors ran the same queue concurrently, both anti-joins could claim the same message; the per-partition DB unique would let only one processed row land per month, but the handler-side effect would already have happened twice. Moving to multi-consumer is a flagged design change.
