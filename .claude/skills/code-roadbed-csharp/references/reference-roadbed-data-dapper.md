# Roadbed.Data.Dapper Reference

Dapper configuration utilities for SQLite-backed entities. Solves two problems:

1. **Date/time conversion.** SQLite stores all temporal values as `TEXT`. The four type handlers in this package handle round-trip conversion for `DateTime`, `DateTime?`, `DateTimeOffset`, and `DateTimeOffset?` — always storing as UTC ISO 8601.
2. **Column-to-property mapping.** `DapperMapping.Configure(...)` registers `[Column]`-attribute-aware type maps for every entity discovered through `IEntity<>`.

All types live under the `Roadbed.Data` namespace.

## Type catalog (5 types)

| Type                                  | Kind         | Purpose                                                          |
| ------------------------------------- | ------------ | ---------------------------------------------------------------- |
| `DapperDateTimeHandler`               | Class        | Stores/reads `DateTime` as `yyyy-MM-dd HH:mm:ss` UTC.            |
| `DapperNullableDateTimeHandler`       | Class        | Same, for `DateTime?` (writes `DBNull` when null).               |
| `DapperDateTimeOffsetHandler`         | Class        | Stores/reads `DateTimeOffset` as `yyyy-MM-dd HH:mm:sszzz` (preserves offset). |
| `DapperNullableDateTimeOffsetHandler` | Class        | Same, for `DateTimeOffset?` (writes `DBNull` when null).         |
| `DapperMapping`                       | Static class | `Configure(params Type[] entityTypes)` registers `[Column]` attribute mapping. |

## MUST

- **MUST** inherit database entities from `BaseEntityClass<TId>` (mutable). `BaseEntityRecord<TId>` is reserved for API DTOs and configuration.
- **MUST** call `DapperMapping.Configure(...)` with every database entity type before any query executes. Do this in the installer, after registering type handlers.
- **MUST** register **all four** type handlers in the installer — even if the project only uses, say, `DateTimeOffset?`. They share base-class behavior and registering them all costs nothing.
- **MUST** discover entity types via `IEntity<>` interface scanning rather than hand-listing them. Add the entity, no installer change required.
- **MUST** use `[Column("snake_case_name")]` on entity properties when the database column name differs from the C# property name.
- **MUST** keep all `DateTime` values stored and retrieved as UTC. The handlers convert non-UTC inputs automatically.

## MUST NOT

- **MUST NOT** use `BaseEntityRecord<TId>` for database entities. Records are immutable; Dapper needs a parameterless constructor and writeable properties to materialize rows.
- **MUST NOT** register repositories before calling `DapperMapping.Configure(...)`. Mapping registration must happen first.
- **MUST NOT** rely on local time in stored timestamps. Always store UTC; let the handlers convert reads back to UTC kind.
- **MUST NOT** call `SqlMapper.AddTypeHandler(...)` for the date types yourself. The four handlers in this package already wrap `Dapper.SqlMapper.TypeHandler<T>` correctly.

## Code patterns

### Database entity (uses `BaseEntityClass<TId>` + `[Column]`)

```csharp
namespace Foo.Database;

using System.ComponentModel.DataAnnotations.Schema;
using Roadbed.Crud;

public sealed class DbFoo : BaseEntityClass<long>
{
    [Column("id")]
    public override long Id { get; set; }

    [Column("name")]
    public string? Name { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [Column("archived_at")]
    public DateTimeOffset? ArchivedAt { get; set; }
}
```

### Installer registers handlers + mapping + repositories

```csharp
namespace Foo.Database.Installers;

using System.Linq;
using System.Reflection;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Roadbed;
using Roadbed.Crud;
using Roadbed.Data;

public sealed class InstallFooDatabase : IServiceCollectionInstaller
{
    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // 1. Register the four date/time type handlers.
        SqlMapper.AddTypeHandler(new DapperDateTimeHandler());
        SqlMapper.AddTypeHandler(new DapperNullableDateTimeHandler());
        SqlMapper.AddTypeHandler(new DapperDateTimeOffsetHandler());
        SqlMapper.AddTypeHandler(new DapperNullableDateTimeOffsetHandler());

        // 2. Discover every entity type in this assembly via IEntity<> interface scanning.
        var entityTypes = Assembly.GetExecutingAssembly()
            .GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(t => t.GetInterfaces().Any(i => i.IsGenericType
                                                && i.GetGenericTypeDefinition() == typeof(IEntity<>)))
            .ToArray();

        // 3. Register the [Column] mappings for every entity.
        DapperMapping.Configure(entityTypes);

        // 4. Now register the connection factory and repositories.
        var connectionString = new DataConnecionString(DataConnectionStringType.SQLite)
        {
            DatabaseSource = configuration["FooDatabase:Path"],
        };

        services.AddSingleton<IFooDatabaseFactory>(new FooDatabaseFactory(connectionString));
        services.AddSingleton<IFooRepository, FooRepository>();

        ServiceLocator.SetLocatorProvider(services.BuildServiceProvider());
    }
}
```

### Storage formats (what shows up in the SQLite `TEXT` column)

| C# value                                                | Stored as                       |
| ------------------------------------------------------- | ------------------------------- |
| `new DateTime(2026, 1, 15, 14, 30, 0, DateTimeKind.Utc)` | `"2026-01-15 14:30:00"`         |
| `(DateTime?)null`                                        | `NULL`                          |
| `new DateTimeOffset(2026, 1, 15, 14, 30, 0, TimeSpan.FromHours(-6))` | `"2026-01-15 14:30:00-06:00"` |
| `(DateTimeOffset?)null`                                  | `NULL`                          |

### Choosing `DateTime` vs `DateTimeOffset` on entities

| Use                                                         | Type                       |
| ----------------------------------------------------------- | -------------------------- |
| Wall-clock timestamps in a known zone (e.g., UTC)           | `DateTime` (kind = UTC)    |
| Timestamps where the original timezone matters              | `DateTimeOffset`           |
| User-facing schedules across multiple zones                 | `DateTimeOffset`           |
| Audit columns / created_at / updated_at                     | `DateTimeOffset` (preferred for clarity) |

## Common pitfalls

### Using `BaseEntityRecord<TId>` for a database entity

```csharp
// ❌ Records are immutable; Dapper can't materialize one without a positional constructor matching every column.
public sealed record DbFoo : BaseEntityRecord<long>
{
    [Column("name")]
    public string? Name { get; set; }
}

// ✅ Use BaseEntityClass for ORM-managed entities.
public sealed class DbFoo : BaseEntityClass<long>
{
    [Column("name")]
    public string? Name { get; set; }
}
```

### Forgetting `DapperMapping.Configure`

```csharp
// ❌ Without this call, Dapper falls back to property-name matching.
// A column called "is_active" won't bind to a property called IsActive.
public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
{
    SqlMapper.AddTypeHandler(new DapperDateTimeHandler());
    services.AddSingleton<IFooRepository, FooRepository>();
}

// ✅ Always call Configure before any repository runs.
public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
{
    SqlMapper.AddTypeHandler(new DapperDateTimeHandler());
    SqlMapper.AddTypeHandler(new DapperNullableDateTimeHandler());
    SqlMapper.AddTypeHandler(new DapperDateTimeOffsetHandler());
    SqlMapper.AddTypeHandler(new DapperNullableDateTimeOffsetHandler());

    DapperMapping.Configure(typeof(DbFoo), typeof(DbBar));

    services.AddSingleton<IFooRepository, FooRepository>();
}
```

### Hand-listing entity types instead of scanning

```csharp
// ❌ Easy to forget when you add a new entity.
DapperMapping.Configure(typeof(DbFoo), typeof(DbBar), typeof(DbBaz));

// ✅ Scan for IEntity<> implementers — adding a new entity costs nothing.
var entityTypes = Assembly.GetExecutingAssembly().GetTypes()
    .Where(t => t.IsClass && !t.IsAbstract)
    .Where(t => t.GetInterfaces().Any(i => i.IsGenericType
                                        && i.GetGenericTypeDefinition() == typeof(IEntity<>)))
    .ToArray();
DapperMapping.Configure(entityTypes);
```

### Storing local time

```csharp
// ❌ Now is local — the handler converts to UTC, but the original offset is lost.
entity.CreatedAt = DateTime.Now;

// ✅ Use UtcNow.
entity.CreatedAt = DateTime.UtcNow;
```

### Registering only the non-nullable handlers

```csharp
// ❌ A nullable column will fail to materialize.
SqlMapper.AddTypeHandler(new DapperDateTimeHandler());
SqlMapper.AddTypeHandler(new DapperDateTimeOffsetHandler());

// ✅ Register all four; cost is zero.
SqlMapper.AddTypeHandler(new DapperDateTimeHandler());
SqlMapper.AddTypeHandler(new DapperNullableDateTimeHandler());
SqlMapper.AddTypeHandler(new DapperDateTimeOffsetHandler());
SqlMapper.AddTypeHandler(new DapperNullableDateTimeOffsetHandler());
```

## Quick reference

### Using statements

```csharp
using System.ComponentModel.DataAnnotations.Schema;   // [Column]
using Dapper;                                          // SqlMapper.AddTypeHandler
using Roadbed.Crud;                                    // BaseEntityClass, IEntity
using Roadbed.Data;                                    // DapperMapping, DapperDateTime*Handler
```

### Installer order

```
1. SqlMapper.AddTypeHandler(...)         × 4 handlers
2. Scan for IEntity<> implementers
3. DapperMapping.Configure(entityTypes)
4. Register connection factory
5. Register repositories
6. ServiceLocator.SetLocatorProvider(services.BuildServiceProvider())
```

### Type-handler registration order does not matter, but it must happen before `DapperMapping.Configure`.
