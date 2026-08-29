# Roadbed.Crud Reference

Generic repository and service contracts for entities. Provides composite interfaces (`Crud`, `Crudl`, `Cruda`, `Crudal`, `ListOnly`, plus the ETL-oriented `BulkOnly` and `Bt`) and base classes that implement the boilerplate, leaving only the data-access primitives for the consuming class to fill in.

## Type catalog (84 types)

| Group                                | Examples                                                                            | Notes                                                  |
| ------------------------------------ | ----------------------------------------------------------------------------------- | ------------------------------------------------------ |
| Entity contracts (3)                 | `IEntity<TId>`, `BaseEntityRecord<TId>`, `BaseEntityClass<TId>`                     | Records for DTOs, classes for ORM-managed entities.    |
| Repository marker (1)                | `IRepository<TEntity, TId>`                                                         | Empty marker; only repository composites inherit it.   |
| Operation interfaces (20)            | `IAsyncCreateOperation<...>`, `ISyncBulkInsertOperation<...>`, etc.                 | One per operation × Async/Sync. Cherry-pick when no composite fits. Now includes `BulkInsert` and `Truncate`. |
| Repository composites (14)           | `IAsyncCrudlRepository<...>`, `IAsyncBulkOnlyRepository<...>`, `IAsyncBtRepository<...>`, etc. | `ListOnly`, `Crud`, `Crudl`, `Cruda`, `Crudal`, `BulkOnly`, `Bt` × Async/Sync. |
| Repository base classes (14)         | `BaseAsyncCrudlRepository<...>`, `BaseAsyncBtRepository<...>`, etc.                 | All methods are `abstract`.                            |
| Service composites (14)              | `IAsyncCrudlService<...>`, `IAsyncBulkOnlyService<...>`, `IAsyncBtService<...>`, etc. | Add `Exists` and `Upsert` on CRUDAL tiers. Do not inherit `IRepository`. |
| Service base classes (14)            | `BaseAsyncCrudlService<...>`, `BaseAsyncBtService<...>`, etc.                       | All methods are `virtual`. `Exists`/`Upsert` are pre-composed. ETL service tiers validate `activityId` before delegating. |

## MUST

- **MUST** supply a non-blank `activityId` whenever you call `BulkInsertAsync` / `BulkInsert`. The service base classes enforce this with `ArgumentException.ThrowIfNullOrWhiteSpace`. The string is opaque to Roadbed — the consuming app chooses the scheme (ULID, GUID, run number, etc.).
- **MUST** own activity-record creation and timing in the consuming app. The activity row that the `activityId` refers to — including its `started_on` / `ended_on` timestamps — lives in the consuming application's schema, never in Roadbed. The bulk operation carries only the id.
- **MUST** put new entities in either `BaseEntityRecord<TId>` (immutable / API DTOs / configuration) or `BaseEntityClass<TId>` (ORM-managed / Dapper / mutable domain entities). Both implement `IEntity<TId>`.
- **MUST** declare repository interfaces and service interfaces as `internal` when they sit alongside a `public` concrete service class. The application layer depends on the concrete service, never on either interface.
- **MUST** declare the concrete service as `public sealed class FooService` so the consuming application can resolve it.
- **MUST** give the concrete service the **dual-constructor pattern**:
  - `public` constructor taking only `ILogger<FooService>` and resolving the repository via `ServiceLocator.GetService<IFooRepository>()`
  - `internal` constructor taking the repository and `ILogger<FooService>` directly, used by unit tests via `[InternalsVisibleTo]`
- **MUST** inherit from the matching base class (`BaseAsyncCrudlRepository<T, TId>`, `BaseAsyncCrudaService<T, TId>`, etc.) — the abstract methods give you a compiler checklist.
- **MUST** return `null` from `ReadAsync` / `Read` when the entity is not found. The composed `ExistsAsync` depends on this.
- **MUST** throw on `DeleteAsync` failure; do not return `bool`. The signature is `Task DeleteAsync(...)`.
- **MUST** return the full entity from `CreateAsync` and `UpdateAsync` — including any server-assigned values like auto-increment IDs.
- **MUST** put the `ServiceLocator.SetLocatorProvider(services.BuildServiceProvider())` call at the end of every installer that registers a repository the dual-constructor service will resolve.

## MUST NOT

- **MUST NOT** generate the `activityId` inside Roadbed. There is no `NewActivityId()` helper, no default, no fallback. Generation is the consuming app's responsibility.
- **MUST NOT** thread per-row load timestamps through the bulk operation signature. There is no `ingestedOn` parameter. Per-row timing lives on the activity record the consuming app already keeps; join through `activityId` if you need it.
- **MUST NOT** parse, normalize, or case-fold the `activityId` inside the repository — even if you "know" it's a ULID. The contract is opaque-string.
- **MUST NOT** declare the service interface as `public`. The application layer should not see it.
- **MUST NOT** declare the concrete service as `internal`. The application cannot resolve internal types.
- **MUST NOT** give the concrete service a single constructor that exposes the internal repository interface — the application layer would see types it shouldn't.
- **MUST NOT** implement `IAsyncReadOperation` or `ISyncReadOperation` to throw when the entity is missing. Return `null`.
- **MUST NOT** implement `IAsyncDeleteOperation.DeleteAsync` to return `bool`. Throw on failure.
- **MUST NOT** override `Exists` or `Upsert` on a service unless you have a database-native upsert (e.g., `INSERT ... ON CONFLICT`, `MERGE`). The default composition from `Exists → Read`, `Upsert → Exists + Create/Update` is correct for most cases.
- **MUST NOT** implement repository methods directly off `IEntity<TId>` constraints — always go through one of the composite interfaces.

## Composite selection

Pick the smallest interface that covers the operations you need:

| Operations needed                              | Interface                                                                  |
| ---------------------------------------------- | -------------------------------------------------------------------------- |
| List only (lookup tables, dimension tables)    | `IAsync/SyncListOnlyRepository<T, TId>` + `IAsync/SyncListOnlyService<T, TId>` |
| Create, Read, Update, Delete (no listing)      | `IAsync/SyncCrudRepository<T, TId>` + `IAsync/SyncCrudService<T, TId>`         |
| Standard CRUD + List                           | `IAsync/SyncCrudlRepository<T, TId>` + `IAsync/SyncCrudlService<T, TId>`       |
| CRUD + Archive (soft delete)                   | `IAsync/SyncCrudaRepository<T, TId>` + `IAsync/SyncCrudaService<T, TId>`       |
| CRUD + Archive + List (full)                   | `IAsync/SyncCrudalRepository<T, TId>` + `IAsync/SyncCrudalService<T, TId>`     |
| Bulk-insert only — Bronze append-only landing  | `IAsync/SyncBulkOnlyRepository<T, TId>` + `IAsync/SyncBulkOnlyService<T, TId>` |
| Bulk-insert + Truncate — Silver snapshot reload | `IAsync/SyncBtRepository<T, TId>` + `IAsync/SyncBtService<T, TId>`           |
| Cherry-pick (no composite fits)                | `IRepository<T, TId>` + individual `IAsync*Operation` interfaces           |

## Method signatures (lock these in)

| Operation    | Async signature                                                                       | Sync signature                                       |
| ------------ | ------------------------------------------------------------------------------------- | ---------------------------------------------------- |
| Create       | `Task<TEntity> CreateAsync(TEntity entity, CancellationToken)`                        | `TEntity Create(TEntity entity)`                     |
| Read         | `Task<TEntity?> ReadAsync(TId id, CancellationToken)`                                 | `TEntity? Read(TId id)`                              |
| Update       | `Task<TEntity> UpdateAsync(TEntity entity, CancellationToken)`                        | `TEntity Update(TEntity entity)`                     |
| Delete       | `Task DeleteAsync(TId id, CancellationToken)`                                         | `void Delete(TId id)`                                |
| Archive      | `Task<TEntity> ArchiveAsync(TId id, CancellationToken)`                               | `TEntity Archive(TId id)`                            |
| List         | `Task<IList<TEntity>> ListAsync(CancellationToken)`                                   | `IList<TEntity> List()`                              |
| Exists       | `Task<bool> ExistsAsync(TId id, CancellationToken)`                                   | `bool Exists(TId id)`                                |
| Upsert       | `Task<TEntity> UpsertAsync(TEntity entity, CancellationToken)`                        | `TEntity Upsert(TEntity entity)`                     |
| Bulk Insert  | `Task<long> BulkInsertAsync(string activityId, IList<TEntity> rows, CancellationToken)` | `long BulkInsert(string activityId, IList<TEntity> rows)` |
| Truncate     | `Task TruncateAsync(CancellationToken)`                                               | `void Truncate()`                                    |

## Code patterns

### Step 1 — define the entity

```csharp
namespace Foo.Sdk;

using System.Text.Json.Serialization;
using Roadbed.Crud;

public sealed record Foo : BaseEntityRecord<string>
{
    [JsonPropertyName("id")]
    public override string? Id { get; set; }

    [JsonPropertyName("name")]
    required public string Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}
```

Use `BaseEntityClass<TId>` instead of `BaseEntityRecord<TId>` when the entity is mutable / managed by an ORM (Dapper-mapped database entities).

### Step 2 — declare the repository interface (`internal`)

```csharp
namespace Foo.Sdk;

using Roadbed.Crud.Repositories.Async;

internal interface IFooRepository
    : IAsyncCrudlRepository<Foo, string>
{
}
```

Add custom query methods directly on the interface when you need them:

```csharp
internal interface IFooRepository
    : IAsyncCrudlRepository<Foo, string>
{
    Task<IList<Foo>> ListByCategoryAsync(string category, CancellationToken cancellationToken = default);
}
```

### Step 3 — implement the repository (`internal sealed`)

```csharp
namespace Foo.Sdk;

using Microsoft.Extensions.Logging;
using Roadbed.Crud.Repositories.Async;

internal sealed class FooRepository
    : BaseAsyncCrudlRepository<Foo, string>,
      IFooRepository
{
    public FooRepository(ILogger<FooRepository> logger)
        : base(logger)
    {
    }

    public override async Task<Foo> CreateAsync(Foo entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        // ... data-access logic ...
        return entity;
    }

    public override async Task<Foo?> ReadAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        // ... return null if not found, never throw ...
        return null;
    }

    public override async Task<Foo> UpdateAsync(Foo entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        // ... data-access logic ...
        return entity;
    }

    public override async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        // ... throw on failure ...
    }

    public override async Task<IList<Foo>> ListAsync(CancellationToken cancellationToken = default)
    {
        // ... data-access logic ...
        return new List<Foo>();
    }
}
```

### Step 4 — declare the service interface (`internal`)

```csharp
namespace Foo.Sdk;

using Roadbed.Crud.Services.Async;

internal interface IFooService
    : IAsyncCrudlService<Foo, string>
{
}
```

### Step 5 — implement the service (`public sealed`, dual-constructor)

```csharp
namespace Foo.Sdk;

using Microsoft.Extensions.Logging;
using Roadbed;
using Roadbed.Crud.Services.Async;

public sealed class FooService
    : BaseAsyncCrudlService<Foo, string>,
      IFooService
{
    // Public constructor — what the application sees.
    public FooService(ILogger<FooService> logger)
        : base(
            ServiceLocator.GetService<IFooRepository>(),
            logger)
    {
    }

    // Internal constructor — for unit tests via [InternalsVisibleTo("Foo.Sdk.Tests")].
    internal FooService(IFooRepository repository, ILogger<FooService> logger)
        : base(repository, logger)
    {
    }

    // CRUDL + Exists + Upsert come for free. Override only when adding business logic:
    //
    // public override async Task<Foo> CreateAsync(Foo entity, CancellationToken cancellationToken = default)
    // {
    //     ArgumentNullException.ThrowIfNull(entity);
    //     ArgumentException.ThrowIfNullOrWhiteSpace(entity.Name);
    //     this.LogInformation("Creating foo: {Name}", entity.Name);
    //     return await base.CreateAsync(entity, cancellationToken);
    // }
}
```

### Step 6 — installer

```csharp
namespace Foo.Sdk.Installers;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Roadbed;

public sealed class InstallFooSdk : IServiceCollectionInstaller
{
    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IFooRepository, FooRepository>();

        ServiceLocator.SetLocatorProvider(services.BuildServiceProvider());
    }
}
```

The application's `Program.cs` calls `services.InstallModulesInAppDomain(configuration)` and resolves `FooService` via DI. It never sees `IFooService` or `IFooRepository`.

### Skipping the service layer (ListOnly reference data)

When there is no business logic, the repository goes straight to the application. The repository interface is `public` in this case.

```csharp
namespace Foo.Sdk;

using Roadbed.Crud.Repositories.Sync;

public interface IBarRepository
    : ISyncListOnlyRepository<Bar, string>
{
}

internal sealed class BarRepository
    : BaseSyncListOnlyRepository<Bar, string>,
      IBarRepository
{
    public BarRepository(ILogger<BarRepository> logger) : base(logger) { }

    public override IList<Bar> List()
    {
        // ... read from CSV / embedded resource / cache ...
        return new List<Bar>();
    }
}
```

### ETL / medallion bulk loads — `BulkOnly` (Bronze) and `Bt` (Silver)

Two specialty tiers for set-based loads in a medallion-architecture pipeline:

- **`BulkOnly`** — single operation: `BulkInsert`. Use for Bronze landing tables that **accumulate** rows across many load cycles. Every row is tagged with the `activityId` of the load that wrote it.
- **`Bt`** ("bee-tee", Bulk + Truncate) — two operations: `BulkInsert` and `Truncate`. Use for Silver staging / derived tables that are **wiped and rebuilt** in full on every load cycle (snapshot reload). The intended call pattern is `Truncate` → `BulkInsert`. `IAsyncBtRepository<T, TId>` inherits `IAsyncBulkOnlyRepository<T, TId>`, so the bulk insert method is declared once.

Both tiers require `TEntity : IEntity<TId>` like every other Crud repository. Bronze DTOs should expose their natural key or surrogate (`AUTO_INCREMENT bronze_id`, ULID, source-system key) through `Id`; pick a scheme that lets the DB assign it on insert when applicable.

#### Activity lineage contract

`activityId` is an opaque, caller-supplied string. The consuming application:

- Owns the activity record (typically a row in its own `*_activities` table with `started_on`, `ended_on`, `outcome`, etc.).
- Generates the id on its own schedule. ULIDs are a natural fit because they sort lexicographically by time.
- Passes the id to **every** bulk-insert call that belongs to that activity.

Roadbed does not generate, validate the shape of, or interpret the id. The service base classes do enforce that the id is non-blank — calling `BulkInsertAsync` with `null` / `""` / `"   "` throws `ArgumentException`. Per-row load timing lives on the activity record, not on the bulk-insert signature.

#### Bronze — `BulkOnly`

```csharp
// Marker interface (per the marker-interface DI pattern)
internal interface IFooBronzeRepository
    : IAsyncBulkOnlyRepository<FooBronzeRow, long>
{
}

internal sealed class FooBronzeRepository
    : BaseAsyncBulkOnlyRepository<FooBronzeRow, long>,
      IFooBronzeRepository
{
    private readonly IFooDatabaseFactory _connectionFactory;

    public FooBronzeRepository(
        IFooDatabaseFactory connectionFactory,
        ILogger<FooBronzeRepository> logger)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        this._connectionFactory = connectionFactory;
    }

    public override async Task<long> BulkInsertAsync(
        string activityId,
        IList<FooBronzeRow> rows,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activityId);
        ArgumentNullException.ThrowIfNull(rows);

        // ... driver-specific bulk insert; tag every row with activityId ...
        return rows.Count;
    }
}
```

#### Silver — `Bt`

```csharp
internal interface IBarSilverRepository
    : IAsyncBtRepository<BarSilverRow, long>
{
}

internal sealed class BarSilverRepository
    : BaseAsyncBtRepository<BarSilverRow, long>,
      IBarSilverRepository
{
    private readonly IBarDatabaseFactory _connectionFactory;

    public BarSilverRepository(
        IBarDatabaseFactory connectionFactory,
        ILogger<BarSilverRepository> logger)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        this._connectionFactory = connectionFactory;
    }

    public override async Task TruncateAsync(CancellationToken cancellationToken = default)
    {
        // ... TRUNCATE TABLE bar_silver ...
    }

    public override async Task<long> BulkInsertAsync(
        string activityId,
        IList<BarSilverRow> rows,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activityId);
        ArgumentNullException.ThrowIfNull(rows);

        // ... driver-specific bulk insert; tag every row with activityId ...
        return rows.Count;
    }
}
```

#### Calling the service — snapshot reload

```csharp
// activityId is generated by the consuming app at the start of the load.
string activityId = Ulid.NewUlid().ToString();
await this._activities.StartAsync(activityId, "bar.silver.reload", cancellationToken);

await this._barService.TruncateAsync(cancellationToken);
long inserted = await this._barService.BulkInsertAsync(activityId, rows, cancellationToken);

await this._activities.EndAsync(activityId, outcome: "success", rowsAffected: inserted, cancellationToken);
```

## Common pitfalls

### Wrong return types

```csharp
// ❌ Old style.
Task<int> CreateAsync(...);   // returned the new ID
Task<bool> UpdateAsync(...);  // success flag
Task<bool> DeleteAsync(...);  // success flag

// ✅ Roadbed.Crud signatures.
Task<TEntity> CreateAsync(...);   // full entity (with assigned ID)
Task<TEntity> UpdateAsync(...);   // full entity
Task DeleteAsync(...);            // throws on failure
```

### `Read` throws for missing entities

```csharp
// ❌ Breaks the composed ExistsAsync.
public override async Task<Foo?> ReadAsync(string id, CancellationToken ct = default)
{
    var foo = await this.QueryAsync(id);
    if (foo is null)
    {
        throw new KeyNotFoundException(id);
    }
    return foo;
}

// ✅ Return null.
public override async Task<Foo?> ReadAsync(string id, CancellationToken ct = default)
{
    return await this.QueryAsync(id);
}
```

### Service interface declared `public`

```csharp
// ❌ Exposes internal contract; application layer ends up depending on the interface.
public interface IFooService : IAsyncCrudlService<Foo, string> { }

// ✅
internal interface IFooService : IAsyncCrudlService<Foo, string> { }
```

### Concrete service declared `internal`

```csharp
// ❌ Application cannot resolve it.
internal sealed class FooService : BaseAsyncCrudlService<Foo, string>, IFooService { }

// ✅
public sealed class FooService : BaseAsyncCrudlService<Foo, string>, IFooService { }
```

### Single-constructor service exposing the internal repository

```csharp
// ❌ Application sees IAsyncCrudlRepository<Foo, string> and IFooRepository.
public sealed class FooService : BaseAsyncCrudlService<Foo, string>, IFooService
{
    public FooService(IFooRepository repository, ILogger<FooService> logger)
        : base(repository, logger)
    {
    }
}

// ✅ Dual constructors.
public sealed class FooService : BaseAsyncCrudlService<Foo, string>, IFooService
{
    public FooService(ILogger<FooService> logger)
        : base(ServiceLocator.GetService<IFooRepository>(), logger)
    {
    }

    internal FooService(IFooRepository repository, ILogger<FooService> logger)
        : base(repository, logger)
    {
    }
}
```

### Forgetting `ServiceLocator.SetLocatorProvider`

```csharp
// ❌ FooService's public constructor cannot resolve IFooRepository.
public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
{
    services.AddSingleton<IFooRepository, FooRepository>();
}

// ✅
public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
{
    services.AddSingleton<IFooRepository, FooRepository>();
    ServiceLocator.SetLocatorProvider(services.BuildServiceProvider());
}
```

### Missing `using Roadbed;` in the service

```csharp
// ❌ ServiceLocator is in the Roadbed namespace, not Roadbed.Common.
using Roadbed.Common;

public sealed class FooService : BaseAsyncCrudlService<Foo, string>
{
    public FooService(ILogger<FooService> logger)
        : base(ServiceLocator.GetService<IFooRepository>(), logger) { }  // doesn't compile
}

// ✅
using Roadbed;
```

### Generating `activityId` inside the repository

```csharp
// ❌ Repository invents its own id. Callers can't correlate the inserted rows
//    with any activity record on their side, defeating the entire point.
public override async Task<long> BulkInsertAsync(
    string activityId,
    IList<FooBronzeRow> rows,
    CancellationToken cancellationToken = default)
{
    activityId = Ulid.NewUlid().ToString();  // 🔥
    // ...
}

// ✅ The caller supplies activityId. The repository (or service base) only
//    validates it's non-blank and threads it through to persistence.
```

### Adding a per-row `ingestedOn` parameter to the bulk operation

```csharp
// ❌ Forces every caller to compute and supply a timestamp; if it drifts from
//    the activity record's started_on, lineage is broken.
public override async Task<long> BulkInsertAsync(
    string activityId,
    DateTimeOffset ingestedOn,   // <-- not part of the Roadbed contract
    IList<FooBronzeRow> rows,
    CancellationToken cancellationToken = default) { ... }

// ✅ The activity record on the consuming-app side owns the timestamp.
//    Join through activityId when you need to know "when did this row land."
public override async Task<long> BulkInsertAsync(
    string activityId,
    IList<FooBronzeRow> rows,
    CancellationToken cancellationToken = default) { ... }
```

### `Bt` repository that re-implements `BulkInsertAsync` separately

```csharp
// ❌ Bt inherits BulkOnly. Declaring BulkInsertAsync twice (once via each
//    base interface) doesn't compile, but trying to "implement BulkOnly +
//    Truncate side by side" without going through BaseAsyncBtRepository
//    means you've lost the marker-interface story.
public sealed class BarRepo
    : IAsyncBulkOnlyRepository<Bar, long>,
      IAsyncTruncateOperation<Bar, long> { /* ... */ }

// ✅ Use BaseAsyncBtRepository — Bt extends BulkOnly, so one BulkInsertAsync
//    method satisfies both interfaces and downstream consumers can inject
//    either marker.
public sealed class BarRepo
    : BaseAsyncBtRepository<Bar, long>,
      IBarSilverRepository { /* ... */ }
```

## Quick reference

### Using statements

```csharp
using Roadbed;                              // ServiceLocator, IServiceCollectionInstaller, base classes
using Roadbed.Crud;                         // IEntity, BaseEntityRecord, BaseEntityClass
using Roadbed.Crud.Repositories.Async;      // IAsync*Repository, BaseAsync*Repository
using Roadbed.Crud.Services.Async;          // IAsync*Service, BaseAsync*Service
using Roadbed.Crud.Operations.Async;        // IAsync*Operation (only for cherry-pick scenarios)
```

### Visibility cheat sheet

| Type                                 | Visibility       | Reason                                                            |
| ------------------------------------ | ---------------- | ----------------------------------------------------------------- |
| Entity (e.g., `Foo`)                 | `public`         | Application uses it.                                              |
| Repository interface (`IFooRepository`) | `internal`    | Hidden behind concrete service.                                    |
| Repository implementation (`FooRepository`) | `internal sealed` | Resolved via `ServiceLocator`, not the application.        |
| Service interface (`IFooService`)    | `internal`       | Hidden behind concrete service.                                   |
| Service implementation (`FooService`) | `public sealed` | Application's only handle into this module.                       |
| Installer (`InstallFooSdk`)          | `public sealed`  | Discovered by `InstallModulesInAppDomain`.                        |

### When skipping the service layer

| Type                                 | Visibility       |
| ------------------------------------ | ---------------- |
| Repository interface (`IFooRepository`) | `public`      |
| Repository implementation (`FooRepository`) | `internal sealed` |
