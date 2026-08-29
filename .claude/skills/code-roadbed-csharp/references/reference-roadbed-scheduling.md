# Roadbed.Scheduling Reference

A thin, opinionated wrapper around [Quartz.NET](https://www.quartz-scheduler.net/) for scheduling jobs in .NET hosted applications. Auto-discovers `ISchedulingJob` implementations, configures Quartz with sensible defaults, and exposes a small surface for jobs to declare schedule, group, priority, and misfire behavior.

Includes a metrics abstraction (`ISchedulingMetrics`) with a built-in logging adapter, an opt-in `SchedulingJobOptions` POCO for **gating jobs** and **overriding cron expressions** per deployment without recompiling, and an opt-in `SchedulingPersistenceOptions` POCO for switching between Quartz's in-memory and AdoJobStore (MySQL / PostgreSQL / SQLite) backends.

## Type catalog (15 types)

| Group                       | Types                                                                                                  |
| --------------------------- | ------------------------------------------------------------------------------------------------------ |
| Job contract                | `ISchedulingJob`, `BaseSchedulingJob<T>`                                                               |
| Schedule configuration      | `SchedulingSchedule`, `SchedulingScheduleType`, `SchedulingJobPriority`                                |
| Misfire handling            | `SchedulingMisfireStrategy`                                                                            |
| Options pattern             | `SchedulingJobOptions`, `SchedulingJobFeature`                                                         |
| Persistence pattern         | `SchedulingPersistenceOptions`, `SchedulingPersistenceMode`, `ISchedulingDatabaseFactory`              |
| Metrics                     | `ISchedulingMetrics`, `JobExecutionInfo`, `LoggingMetricsAdapter`                                       |
| Module auto-discovery       | `InstallScheduling` (in `Roadbed.Scheduling.Installers`)                                               |

## MUST

- **MUST** inherit from `BaseSchedulingJob<T>` — never implement `ISchedulingJob` directly. The base class implements `Quartz.IJob.Execute`, manages the `Context` lifecycle, and handles `IsEnabled` resolution.
- **MUST** pick **one** definition pattern per job. Either pass `name`/`description`/`schedule` to `base(...)` (constructor-supplied) **or** override `Name`, `Description`, `Schedule` properties (property-overridden). Do not mix.
- **MUST** implement `ExecuteAsync(CancellationToken)` only. The `Quartz.IJob.Execute` method is implemented explicitly by `BaseSchedulingJob<T>` — overriding it bypasses context lifecycle.
- **MUST** inject `IServiceProvider` and resolve runtime-only services (`IScheduler`, `ISchedulerFactory`) inside `ExecuteAsync` rather than the constructor. Job constructors run at DI configuration time, before the host is built.
- **MUST** set `this.Context.Result = "..."` at the end of `ExecuteAsync` to give the metrics adapter a one-line execution summary (e.g., `"Processed 1,234 records"`).
- **MUST** use `this.LogDebug(...)` / `this.LogInformation(...)` / etc. — not `this.Logger.LogDebug(...)`. The convenience methods check `IsEnabled` before formatting.
- **MUST** use the options-based constructor overloads when the schedule or enabled-state must be controllable per deployment. The application populates a `SchedulingJobOptions` singleton from its own appSettings; the framework never reads `IConfiguration`.
- **MUST** match the `const string JobName = "FooJob";` exactly between the job class and the `SchedulingJobOptions.Features` dictionary key. The framework does not normalize casing.
- **MUST** register a `SchedulingPersistenceOptions` singleton with `Mode = SchedulingPersistenceMode.Persistent` plus an `ISchedulingDatabaseFactory` singleton when trigger durability across restarts or clustering is required. Without these, the installer falls back to Quartz's in-memory store — state is lost on every restart.
- **MUST** apply Quartz's DDL (`tables_mysql_innodb.sql`, `tables_postgres.sql`, `tables_sqlite.sql`) to the target schema **before** the host starts. Roadbed.Scheduling does not run schema migrations.
- **MUST** populate `SchedulingPersistenceOptions.SchedulerName` with a value unique to each Windows service / host process that shares the schema with another scheduler. The default `"RoadbedScheduler"` is a placeholder, not a recommendation.

## MUST NOT

- **MUST NOT** register jobs manually in DI. `InstallScheduling.ConfigureServices` discovers every concrete `ISchedulingJob` in loaded assemblies and registers each as transient.
- **MUST NOT** override `Quartz.IJob.Execute` on a `BaseSchedulingJob<T>` subclass. Implement `ExecuteAsync` instead.
- **MUST NOT** access `this.Context` outside `ExecuteAsync`. It throws `InvalidOperationException`.
- **MUST NOT** read `IConfiguration` from inside a job to gate registration. Take `SchedulingJobOptions` instead and let the application own the config-to-POCO mapping.
- **MUST NOT** create two jobs with the same `Name` and `GroupName`. Quartz `JobKey` uniqueness applies — collisions cause registration failures.
- **MUST NOT** throw from an `ISchedulingMetrics` implementation. The metrics listener wraps every call in a try/catch that logs at Debug and swallows. Throwing only adds noise.
- **MUST NOT** mutate `SchedulingJobOptions` at runtime expecting the schedule to change. Options are evaluated once at startup. Restart the host to apply changes.
- **MUST NOT** add `Quartz.Serialization.Json` / `Quartz.Serialization.SystemTextJson` or other Quartz auxiliary packages to consuming projects unless you need them — Roadbed.Scheduling sets `UseProperties = true` on the persistent store, so `JobDataMap` is string-only and no serializer is required.
- **MUST NOT** point an `ISchedulingDatabaseFactory` at a `DataConnectionStringType.SQLiteInMemory` connection. The installer throws `InvalidOperationException` because in-memory storage cannot back a Quartz persistent store.
- **MUST NOT** share a single `SchedulerName` across two hosts unless you intend them to form a Quartz **cluster** (only one node fires each trigger, surviving nodes recover from a failed peer). For two independent schedulers sharing one schema, use distinct `SchedulerName` values.
- **MUST NOT** read connection strings from `IConfiguration` inside Roadbed.Scheduling — connection assembly is the host's responsibility. The installer resolves `ISchedulingDatabaseFactory` from DI and uses `factory.Connecion.ConnectionString` verbatim.

## Code patterns

### Minimal job (constructor-supplied schedule)

```csharp
namespace Foo.App;

using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Roadbed.Scheduling;

public sealed class FooJob : BaseSchedulingJob<FooJob>
{
    private readonly IFooService _service;

    public FooJob(ILogger<FooJob> logger, IFooService service)
        : base(
            name: "FooJob",
            description: "Processes pending foos every 5 minutes.",
            schedule: new SchedulingSchedule(TimeSpan.FromMinutes(5))
            {
                GroupName = "MyApp",
                Priority = SchedulingJobPriority.Normal,
            },
            logger: logger)
    {
        ArgumentNullException.ThrowIfNull(service);
        this._service = service;
    }

    public override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        this.LogDebug("FooJob starting");

        int processed = await this._service.ProcessPendingAsync(cancellationToken);

        this.Context.Result = $"Processed {processed} foos";
    }
}
```

### Cron-scheduled job

```csharp
public FooReportJob(ILogger<FooReportJob> logger)
    : base(
        name: "FooReport",
        description: "Generates the daily Foo report at 6 AM local time.",
        schedule: new SchedulingSchedule("0 0 6 * * ?")
        {
            TimeZone = TimeZoneInfo.Local,
            GroupName = "Reports",
            Priority = SchedulingJobPriority.High,
        },
        logger: logger)
{
}
```

### Manual-only job (no automatic schedule, must be triggered programmatically)

```csharp
public FooManualJob(ILogger<FooManualJob> logger)
    : base(
        name: "FooManual",
        description: "Triggered manually by the operations team.",
        logger: logger)
{
}
// Trigger via:
//   var scheduler = await schedulerFactory.GetScheduler();
//   await scheduler.TriggerJob(new JobKey("FooManual", "Default"));
```

### Lenient options-gated job (default schedule, optional override per deployment)

```csharp
public sealed class FooJob : BaseSchedulingJob<FooJob>
{
    public const string JobName = "FooJob";

    public FooJob(
        ILogger<FooJob> logger,
        SchedulingJobOptions options,
        IFooService service)
        : base(
            name: JobName,
            description: "Processes pending foos.",
            defaultSchedule: new SchedulingSchedule(TimeSpan.FromMinutes(30)),
            options: options,
            logger: logger)
    {
        ArgumentNullException.ThrowIfNull(service);
        this._service = service;
    }

    private readonly IFooService _service;

    public override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        await this._service.ProcessPendingAsync(cancellationToken);
    }
}
```

### Strict options-gated job (no default — schedule MUST come from options)

```csharp
public sealed class BarJob : BaseSchedulingJob<BarJob>
{
    public const string JobName = "BarJob";

    public BarJob(ILogger<BarJob> logger, SchedulingJobOptions options)
        : base(
            name: JobName,
            description: "Zone-specific job; schedule must come from SchedulingJobOptions.",
            options: options,
            logger: logger)
    {
    }

    public override Task ExecuteAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

Startup throws `InvalidOperationException` if no `BarJob` entry exists in `SchedulingJobOptions.Features`, or if the entry is enabled with no `CronExpression`.

### Application populates `SchedulingJobOptions` from its own appSettings

```csharp
namespace Foo.App;

using Microsoft.Extensions.Configuration;
using Roadbed.Scheduling;

public static class FooJobOptionsBuilder
{
    public static SchedulingJobOptions Build(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new SchedulingJobOptions
        {
            Features = new Dictionary<string, SchedulingJobFeature>
            {
                [FooJob.JobName] = new SchedulingJobFeature
                {
                    Enabled = configuration.GetValue<bool>("Jobs:Foo:Enabled", true),
                    CronExpression = configuration["Jobs:Foo:Cron"],
                    Arguments = new Dictionary<string, string>
                    {
                        ["zone"] = configuration["SecurityZone"] ?? "default",
                    },
                },
            },
        };
    }
}
```

### Wire it up in `Program.cs`

```csharp
var builder = WebApplication.CreateBuilder(args);

// Application owns the appSettings → POCO mapping.
builder.Services.AddSingleton(FooJobOptionsBuilder.Build(builder.Configuration));

// Optional: turn on metrics-to-logs.
builder.Services.AddSingleton<ISchedulingMetrics, LoggingMetricsAdapter>();

// Auto-discover and register every Roadbed module installer (including InstallScheduling).
builder.Services.InstallModulesInAppDomain(builder.Configuration);

var app = builder.Build();
app.Run();
```

`appsettings.json`:

```json
{
  "SecurityZone": "public",
  "Jobs": {
    "Foo": {
      "Enabled": true,
      "Cron": "0 */5 * * * ?"
    }
  }
}
```

To **disable** a job in a deployment: set `"Enabled": false`. To **change cadence**: change `"Cron"`. Restart the host.

### Custom metrics adapter

```csharp
public sealed class FooDatadogMetrics : ISchedulingMetrics
{
    private readonly ILogger<FooDatadogMetrics> _logger;
    private readonly IDatadogClient _datadog;

    public FooDatadogMetrics(IDatadogClient datadog, ILogger<FooDatadogMetrics> logger)
    {
        ArgumentNullException.ThrowIfNull(datadog);
        ArgumentNullException.ThrowIfNull(logger);

        this._datadog = datadog;
        this._logger = logger;
    }

    public void JobStarted(JobExecutionInfo info) { /* ... */ }
    public void JobCompleted(JobExecutionInfo info, TimeSpan duration)
    {
        try
        {
            this._datadog.Submit(info, duration);
        }
        catch (Exception ex)
        {
            // Never throw from metrics — log and swallow.
            this._logger.LogWarning(ex, "Datadog submission failed for {JobName}", info.JobName);
        }
    }
    public void JobFailed(JobExecutionInfo info, Exception exception, TimeSpan duration) { /* ... */ }
    public void JobMisfired(JobExecutionInfo info) { /* ... */ }
}

builder.Services.AddSingleton<ISchedulingMetrics, FooDatadogMetrics>();
```

### Persistent storage (MySQL / PostgreSQL / SQLite) and the SchedulerName mechanism

By default, Roadbed.Scheduling configures Quartz's in-memory store: triggers live in process memory and every restart loses in-flight state. To persist triggers across restarts — or to run multiple host nodes that cooperate — register a `SchedulingPersistenceOptions` POCO and an `ISchedulingDatabaseFactory` singleton **before** the call to `services.InstallModulesInAppDomain(configuration)`.

Roadbed.Scheduling is **driver-package-agnostic**. The installer inspects `factory.Connecion.ConnectionStringType` and dispatches to the right Quartz fluent method (`UseMySqlConnector` / `UsePostgres` / `UseMicrosoftSQLite`), all of which ship inside the main `Quartz` package. The actual ADO.NET driver assembly is loaded reflectively at runtime — supplied transitively by the host's reference to `Roadbed.Data.MySql`, `Roadbed.Data.Postgresql`, or `Roadbed.Data.Sqlite`.

#### Host registration (single-node persistent)

```csharp
namespace Foo.WindowsService;

using Roadbed;
using Roadbed.Data;
using Roadbed.Data.MySql;
using Roadbed.Scheduling;

internal sealed class FooSchedulingDatabaseFactory(DataConnecionString connectionString)
    : MySqlConnectionFactory(connectionString), ISchedulingDatabaseFactory;

public sealed class InstallFooHost : IServiceCollectionInstaller
{
    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(new SchedulingPersistenceOptions
        {
            SchedulerName = "Foo-Prod",
            Mode = SchedulingPersistenceMode.Persistent,
            TablePrefix = "QRTZ_",
            IsClustered = false,
        });

        services.AddSingleton<ISchedulingDatabaseFactory>(_ =>
            new FooSchedulingDatabaseFactory(new DataConnecionString(DataConnectionStringType.MySQL)
            {
                ServerName    = configuration["Foo:Scheduler:Mysql:Server"]!,
                DatabaseSource = configuration["Foo:Scheduler:Mysql:Database"]!,
                Username      = configuration["Foo:Scheduler:Mysql:Username"]!,
                Password      = configuration["Foo:Scheduler:Mysql:Password"]!,
            }));
    }
}
```

The application is wholly responsible for sourcing the connection-string fields (appsettings.json, environment variables, secret store, …). Roadbed.Scheduling never reads `IConfiguration` directly — only the POCO and the factory flow in via DI.

#### Two Windows services sharing one schema (distinct SchedulerName values)

If two Production Windows services need to share a `quartz_prod` schema for operational simplicity, give each a distinct `SchedulerName`. Quartz's `SCHED_NAME` column then isolates the rows:

```csharp
// FooService — host-side installer
services.AddSingleton(new SchedulingPersistenceOptions
{
    SchedulerName = "Foo-Prod",   // ← distinct
    Mode = SchedulingPersistenceMode.Persistent,
});

// BarService — host-side installer
services.AddSingleton(new SchedulingPersistenceOptions
{
    SchedulerName = "Bar-Prod",   // ← distinct
    Mode = SchedulingPersistenceMode.Persistent,
});
```

Both can register `ISchedulingDatabaseFactory` instances that produce the same physical connection. The shared MySQL schema sees both schedulers' jobs but each one only operates on its own rows. Backups, migrations, and access control still apply uniformly to the shared schema — which is the trade-off for the simplicity. **A separate schema per service is still the operationally safer choice** when budget allows.

#### High-availability cluster (same SchedulerName on multiple nodes)

To run two nodes of the same Windows service in active-active mode against the same schema, give them the **same** `SchedulerName` and turn clustering on:

```csharp
services.AddSingleton(new SchedulingPersistenceOptions
{
    SchedulerName = "Foo-Prod",      // ← same on every node
    Mode = SchedulingPersistenceMode.Persistent,
    IsClustered = true,              // ← Quartz's cluster lock protocol
});
```

Only one node fires each trigger; surviving nodes recover in-flight jobs from a failed peer after Quartz's misfire threshold elapses.

#### Selecting the right SchedulerName mode

| Goal | `SchedulerName` strategy | `IsClustered` |
|---|---|---|
| Single node, durable triggers | One name, doesn't matter what | `false` |
| Two services share one schema, isolated jobs | Distinct names per service | `false` |
| Active-active HA cluster | Same name on every node | `true` |
| Sharing a schema across services AND clustering each | Distinct service names + same per cluster | `true` per cluster |

#### DDL: applied by the host, not by Roadbed

Roadbed.Scheduling does not run schema migrations. Apply Quartz's bundled DDL to the target schema before the host starts:

- MySQL/MariaDB: `tables_mysql_innodb.sql`
- PostgreSQL: `tables_postgres.sql`
- SQLite: `tables_sqlite.sql`

If you want in-place migration safety, embed the Quartz version in the table prefix (`TablePrefix = "QRTZ_V3_18_"` etc.). A future Quartz upgrade with schema changes can then live alongside the previous version's tables, and consumers cut over per service.

## Common pitfalls

### Implementing `ISchedulingJob` directly

```csharp
// ❌ Bypasses BaseSchedulingJob's IJob.Execute → ExecuteAsync bridge.
public sealed class FooJob : ISchedulingJob
{
    public string Name => "FooJob";
    // ... must also implement Quartz.IJob.Execute, manage Context, etc.
}

// ✅
public sealed class FooJob : BaseSchedulingJob<FooJob>
{
    public FooJob(ILogger<FooJob> logger)
        : base("FooJob", "...", new SchedulingSchedule(TimeSpan.FromMinutes(5)), logger) { }

    public override Task ExecuteAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

### Mixing constructor and property patterns

```csharp
// ❌ Constructor sets schedule AND override returns a different one. The override wins —
// confusing and likely a bug.
public sealed class FooJob : BaseSchedulingJob<FooJob>
{
    public FooJob(ILogger<FooJob> logger)
        : base("FooJob", "...", new SchedulingSchedule(TimeSpan.FromMinutes(5)), logger) { }

    public override SchedulingSchedule Schedule => new SchedulingSchedule(TimeSpan.FromMinutes(15));
}

// ✅ Pick one pattern.
```

### Resolving `IScheduler` in the constructor

```csharp
// ❌ IScheduler isn't available until the host starts.
public FooJob(IScheduler scheduler, ILogger<FooJob> logger)
    : base("FooJob", "...", schedule, logger)
{
    this._scheduler = scheduler;   // throws during InstallScheduling.ConfigureServices
}

// ✅ Inject IServiceProvider and resolve at execution time.
public FooJob(IServiceProvider services, ILogger<FooJob> logger)
    : base("FooJob", "...", schedule, logger)
{
    ArgumentNullException.ThrowIfNull(services);
    this._services = services;
}

public override async Task ExecuteAsync(CancellationToken cancellationToken)
{
    var schedulerFactory = this._services.GetRequiredService<ISchedulerFactory>();
    var scheduler = await schedulerFactory.GetScheduler(cancellationToken);
    // ...
}
```

### Reading `IConfiguration` from inside the job

```csharp
// ❌ Couples the job to the application's configuration shape.
public FooJob(IConfiguration configuration, ILogger<FooJob> logger)
    : base("FooJob", "...", BuildSchedule(configuration), logger)
{
    if (!configuration.GetValue<bool>("Jobs:Foo:Enabled", true))
    {
        // No way to skip from here — the job is already registered.
    }
}

// ✅ Accept SchedulingJobOptions; let the application populate it from IConfiguration.
public FooJob(SchedulingJobOptions options, ILogger<FooJob> logger)
    : base("FooJob", "...", new SchedulingSchedule(TimeSpan.FromMinutes(30)), options, logger) { }
```

### Two jobs with the same `Name` + `GroupName`

```csharp
// ❌ Both map to JobKey("FooJob", "Default") — Quartz registration collides.
public sealed class FooJob1 : BaseSchedulingJob<FooJob1>
{
    public FooJob1(ILogger<FooJob1> logger) : base("FooJob", "...", schedule, logger) { }
}
public sealed class FooJob2 : BaseSchedulingJob<FooJob2>
{
    public FooJob2(ILogger<FooJob2> logger) : base("FooJob", "...", schedule, logger) { }
}

// ✅ Distinct names (or distinct GroupNames on the schedule).
public sealed class FooJob1 : BaseSchedulingJob<FooJob1>
{
    public FooJob1(ILogger<FooJob1> logger) : base("FooJob1", "...", schedule, logger) { }
}
```

### Throwing from an `ISchedulingMetrics` implementation

```csharp
// ❌ Throws are caught and logged at Debug but otherwise silently swallowed.
public void JobCompleted(JobExecutionInfo info, TimeSpan duration)
{
    this._datadog.Submit(info);   // throws if Datadog is unreachable
}

// ✅
public void JobCompleted(JobExecutionInfo info, TimeSpan duration)
{
    try
    {
        this._datadog.Submit(info);
    }
    catch (Exception ex)
    {
        this._logger.LogWarning(ex, "Datadog submission failed for {JobName}", info.JobName);
    }
}
```

### Expecting runtime re-evaluation of `SchedulingJobOptions`

```csharp
// ❌ Options are read at host startup. Mutating the singleton at runtime has no effect.
options.Features["FooJob"] = new SchedulingJobFeature { Enabled = false };

// ✅ Restart the host. Roadbed.Scheduling deliberately evaluates options once.
```

### Selecting `Persistent` mode without registering the database factory

```csharp
// ❌ ConfigureServices throws InvalidOperationException at startup:
// "SchedulingPersistenceOptions.Mode is 'Persistent' but no
// ISchedulingDatabaseFactory is registered."
services.AddSingleton(new SchedulingPersistenceOptions
{
    Mode = SchedulingPersistenceMode.Persistent,
});
// ISchedulingDatabaseFactory missing — host won't start.

// ✅
services.AddSingleton(new SchedulingPersistenceOptions
{
    Mode = SchedulingPersistenceMode.Persistent,
});
services.AddSingleton<ISchedulingDatabaseFactory>(_ =>
    new FooSchedulingDatabaseFactory(/* connection details */));
```

### Pointing the database factory at SQLite in-memory

```csharp
// ❌ SQLite in-memory storage cannot survive the connection lifetime —
// state would be lost the moment Quartz closes a connection. Throws.
new DataConnecionString(DataConnectionStringType.SQLiteInMemory) { /* ... */ }

// ✅ Use a file-based SQLite database for persistence; if you actually want
// process-memory-only state, leave Mode at SchedulingPersistenceMode.InMemory
// and skip registering ISchedulingDatabaseFactory entirely.
new DataConnecionString(DataConnectionStringType.SQLite) { DatabaseSource = "/var/lib/foo/scheduler.db" }
```

### Sharing one `SchedulerName` across two unrelated services

```csharp
// ❌ Two services with SchedulerName="Foo" + IsClustered=true form a cluster
// against the schema — each can fire the other's triggers. Unless you mean to
// run them as cluster members of the same service, this is data corruption
// waiting to happen.
services.AddSingleton(new SchedulingPersistenceOptions
{
    SchedulerName = "Foo",
    IsClustered = true,  // wrong on a non-cluster peer
});

// ✅ Distinct SchedulerName values isolate the rows by SCHED_NAME.
services.AddSingleton(new SchedulingPersistenceOptions
{
    SchedulerName = "Foo-Prod",      // or "Bar-Prod" in the other service
    IsClustered = false,
});
```

## Quick reference

### Using statements

```csharp
using Microsoft.Extensions.Logging;
using Roadbed.Scheduling;
using Roadbed.Data;  // when registering ISchedulingDatabaseFactory
```

### `SchedulingPersistenceOptions` fields

| Field             | Type                          | Default              | Maps to                                                       |
| ----------------- | ----------------------------- | -------------------- | ------------------------------------------------------------- |
| `SchedulerName`   | `string`                      | `"RoadbedScheduler"` | Quartz `quartz.scheduler.instanceName` + `SCHED_NAME` column. |
| `Mode`            | `SchedulingPersistenceMode`   | `InMemory`           | `RAMJobStore` vs `JobStoreTX` selection.                      |
| `TablePrefix`     | `string`                      | `"QRTZ_"`            | Quartz `quartz.jobStore.tablePrefix`.                         |
| `IsClustered`     | `bool`                        | `false`              | Quartz `quartz.jobStore.clustered` (Persistent only).         |

### `DataConnectionStringType` → Quartz dispatcher

| `ConnectionStringType` | Dispatch                       | Driver assembly (host-provided)        |
| ---------------------- | ------------------------------ | -------------------------------------- |
| `MySQL`                | `store.UseMySqlConnector(...)` | `MySqlConnector` (via Roadbed.Data.MySql) |
| `PostgreSQL`           | `store.UsePostgres(...)`       | `Npgsql` (via Roadbed.Data.Postgresql) |
| `SQLite`               | `store.UseMicrosoftSQLite(...)` | `Microsoft.Data.Sqlite` (via Roadbed.Data.Sqlite) |
| `SQLiteInMemory`       | **Throws** — incompatible with persistent storage. |  |
| `Unknown` / other      | **Throws** — no Quartz dispatch defined. |  |

### `BaseSchedulingJob<T>` constructor matrix

| Constructor                                                                            | Use case                                                                                  |
| -------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------- |
| `(ILogger logger)`                                                                     | Property-override pattern (override `Name`, `Description`, `Schedule`).                   |
| `(string name, string description, SchedulingSchedule schedule, ILogger logger)`       | Constructor-supplied with explicit schedule.                                              |
| `(string name, string description, ILogger logger)`                                    | Manual-only — no automatic schedule, triggered programmatically.                          |
| `(string name, string description, SchedulingSchedule defaultSchedule, SchedulingJobOptions options, ILogger logger)` | Lenient options-gated. Missing entry uses default.                                         |
| `(string name, string description, SchedulingJobOptions options, ILogger logger)`      | Strict options-gated. Missing entry or missing cron throws `InvalidOperationException`.   |

### `SchedulingSchedule` types

| Constructor                                  | `ScheduleType`               | Quartz trigger                                                              |
| -------------------------------------------- | ---------------------------- | --------------------------------------------------------------------------- |
| `SchedulingSchedule()`                       | `ManualOnly`                 | None — durable job, must be triggered programmatically.                     |
| `SchedulingSchedule(string cronExpression)`  | `Cron`                       | `WithCronSchedule(...)`                                                     |
| `SchedulingSchedule(TimeSpan interval)`      | `SimpleInterval`             | `WithSimpleSchedule(...)` `RepeatForever`                                   |
| `SchedulingSchedule(DateTime startAt)`       | `SpecificTimeOnce`           | `StartAt(...)` no repeat                                                    |
| `SchedulingSchedule(DateTime startAt, TimeSpan interval)` | `SpecificTimeWithInterval` | `StartAt(...)` then repeat at interval                            |

### `SchedulingJobOptions` resolution matrix

| Situation                                     | Lenient overload (with `defaultSchedule`)  | Strict overload (no default)                 |
| --------------------------------------------- | ------------------------------------------ | -------------------------------------------- |
| Entry missing from `Features`                 | Use `defaultSchedule`, `IsEnabled = true`   | **Throws `InvalidOperationException`**        |
| Entry with `Enabled = false`                  | Skipped from Quartz                         | Skipped from Quartz                           |
| Entry with `Enabled = true`, no cron          | Use `defaultSchedule`                       | **Throws `InvalidOperationException`**        |
| Entry with `Enabled = true` + `CronExpression` | Use the supplied cron                      | Use the supplied cron                         |

### `SchedulingJobPriority` values

`Lowest = 0`, `VeryLow = 2`, `Low = 4`, `Normal = 5`, `High = 7`, `VeryHigh = 9`, `Highest = 10`.

### `SchedulingMisfireStrategy` (only relevant when `MisfireHandlingEnabled = true`)

| Strategy                  | Cron-only | Simple-only |
| ------------------------- | --------- | ----------- |
| `Default`                 | both      | both        |
| `DoNothing`               | yes       |             |
| `FireAndProceed`          | yes       |             |
| `IgnoreMisfires`          | yes       | yes         |
| `FireNow`                 |           | yes         |
| `NextWithExistingCount`   |           | yes         |
| `NextWithRemainingCount`  |           | yes         |
| `NowWithExistingCount`    |           | yes         |
| `NowWithRemainingCount`   |           | yes         |

### `JobExecutionInfo` properties (passed to `ISchedulingMetrics`)

| Property                  | Notes                                                                  |
| ------------------------- | ---------------------------------------------------------------------- |
| `FireInstanceId`          | Per-execution unique ID.                                               |
| `JobName`, `JobGroup`     | From the `SchedulingSchedule`.                                          |
| `TriggerName`, `TriggerGroup` | Quartz trigger identifiers.                                         |
| `FireTimeUtc`             | When the trigger actually fired. Null on misfire.                      |
| `ScheduledFireTimeUtc`    | When the trigger was scheduled to fire.                                |
| `PreviousFireTimeUtc`     | Previous trigger fire time. Null for first execution.                  |
| `NextFireTimeUtc`         | Next scheduled trigger time. Null when no more executions.             |
| `ResultMessage`           | `Context.Result?.ToString()` set by the job. Always null on `JobStarted`. |
