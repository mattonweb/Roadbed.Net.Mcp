# Roadbed.Data Reference

Database-agnostic abstractions for connection management and query configuration. Concrete implementations live in `Roadbed.Data.Sqlite`, `Roadbed.Data.Postgresql`, and `Roadbed.Data.MySql`.

This reference covers the abstractions only. For per-database guidance see the matching reference file.

## Type catalog

| Type                       | Kind      | Namespace      | Purpose                                                          |
| -------------------------- | --------- | -------------- | ---------------------------------------------------------------- |
| `IDataConnectionFactory`   | Interface | `Roadbed.Data` | Contract for creating already-open `IDbConnection` instances.    |
| `DataConnecionString`      | Class     | `Roadbed.Data` | Connection-string builder with database-type templates.          |
| `DataConnectionStringType` | Enum      | `Roadbed.Data` | `Unknown`, `SQLite`, `SQLiteInMemory`, `PostgreSQL`, `MySQL`.    |
| `DataExecutorRequest`      | Class     | `Roadbed.Data` | Query + parameters + retry knobs + per-query `CommandTimeoutInSeconds`, consumed by the per-database executors. |

## MUST

- **MUST** create a per-database **marker interface** (e.g., `IFooDatabaseFactory : IDataConnectionFactory`) and inject that, not `IDataConnectionFactory` directly. The marker lets DI distinguish between multiple databases in the same application.
- **MUST** implement the marker by inheriting the per-database factory class — `SqliteConnectionFactory`, `PostgresqlConnectionFactory`, or `MySqlConnectionFactory` — and implementing the marker interface.
- **MUST** wrap every connection from the factory in a `using` declaration. Connections are returned **already open**; the caller owns disposal.
- **MUST** prefer the property-based `DataConnecionString` constructor (type only, set `ServerName`, `DatabaseSource`, `Username`, `Password`, `TimeoutInSeconds`) when the template covers your needs. Fall back to the raw-string constructor only for parameters not in the template (e.g., `Port`, `SslMode`, `Pooling`).
- **MUST** put `CancellationToken cancellationToken = default` as the last parameter on every async repository method.
- **MUST** validate parameters with `ArgumentNullException.ThrowIfNull` and `ArgumentException.ThrowIfNullOrWhiteSpace` at the top of every public method.

## MUST NOT

- **MUST NOT** inject `IDataConnectionFactory` directly into a repository. There is no way to distinguish multiple databases at DI resolution time.
- **MUST NOT** call `connection.Open()` after `factory.CreateOpenConnectionAsync(...)`. The connection is already open — calling `Open()` throws.
- **MUST NOT** forget the `using` on a connection. The factory does not pool or track its instances.
- **MUST NOT** set `MaxRetries`, `DelayBetweenRetries`, or `CommandTimeoutInSeconds` to negative values. They throw `ArgumentOutOfRangeException`. Use `RetriesEnabled = false` to disable retries; use `CommandTimeoutInSeconds = null` for the connection default or `0` for no timeout.
- **MUST NOT** raise `CommandTimeoutInSeconds` without a code comment explaining why. The 5-second default is intentionally tight — reach for SQL optimization or a design change before extending the timeout.
- **MUST NOT** roll your own retry loop around the per-database executors. They retry transient errors internally.

## Code patterns

### Marker interface (one per database)

```csharp
namespace Foo.Database;

using Roadbed.Data;

public interface IFooDatabaseFactory
    : IDataConnectionFactory
{
}
```

The marker is empty — it exists solely so DI can hand the right factory to the right repository.

### Factory implementation (primary constructor inheriting the per-database factory)

```csharp
namespace Foo.Database;

using Roadbed.Data;
using Roadbed.Data.Sqlite;   // or .Postgresql, or .MySql

/// <summary>
/// Factory for the Foo database.
/// </summary>
/// <param name="connection">Connection string for the Foo database.</param>
public class FooDatabaseFactory(DataConnecionString connection)
    : SqliteConnectionFactory(connection), IFooDatabaseFactory
{
}
```

### Installer (registers the marker interface)

```csharp
namespace Foo.Database.Installers;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Roadbed;
using Roadbed.Data;

public sealed class InstallFooDatabase : IServiceCollectionInstaller
{
    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = new DataConnecionString(DataConnectionStringType.SQLite)
        {
            DatabaseSource = configuration["FooDatabase:Path"],
        };

        services.AddSingleton<IFooDatabaseFactory>(new FooDatabaseFactory(connectionString));

        ServiceLocator.SetLocatorProvider(services.BuildServiceProvider());
    }
}
```

### Repository injects the marker, never `IDataConnectionFactory`

```csharp
namespace Foo.Database;

using Microsoft.Extensions.Logging;
using Roadbed;

internal sealed class FooRepository : BaseClassWithLogging
{
    private readonly IFooDatabaseFactory _connectionFactory;

    public FooRepository(
        IFooDatabaseFactory connectionFactory,
        ILogger<FooRepository> logger)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        this._connectionFactory = connectionFactory;
    }

    public async Task<Foo?> ReadAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        using var connection = await this._connectionFactory
            .CreateOpenConnectionAsync(cancellationToken);

        // Use SqliteExecutor / PostgresqlExecutor / MySqlExecutor for the actual query —
        // they handle retries, logging, and transient-error detection.
        // ...
        return null;
    }
}
```

### `DataExecutorRequest` for query + retry configuration

```csharp
// Default retry configuration (3 retries, 100ms base delay, linear multiplier):
var request = new DataExecutorRequest("SELECT id, name FROM foo WHERE id = @Id")
{
    Parameters = new { Id = id },
};

// Disable retries (e.g., read-only queries):
var request = new DataExecutorRequest("SELECT COUNT(*) FROM foo")
{
    RetriesEnabled = false,
};

// Custom retry configuration:
var request = new DataExecutorRequest("UPDATE foo SET name = @Name WHERE id = @Id")
{
    Parameters = new { Id = id, Name = newName },
    MaxRetries = 5,
    DelayBetweenRetries = TimeSpan.FromMilliseconds(200),
    DelayMultiplierEnabled = true,   // 200ms, 400ms, 600ms, 800ms, 1000ms
};

// Per-query command-timeout override. The default is deliberately short (5s);
// raising it ALWAYS warrants a comment explaining why optimizing the SQL was
// not the answer.
var request = new DataExecutorRequest("CALL rebuild_monthly_rollup(@Month)")
{
    Parameters = new { Month = month },
    // Monthly rollup proc legitimately scans ~40M rows; verified can't be chunked. JIRA-1234.
    CommandTimeoutInSeconds = 120,
};
```

### Command timeout: two tiers

| Tier | Where | Meaning |
| --- | --- | --- |
| Global default | `DataConnecionString.CommandTimeoutInSeconds` (default **5**) | The command (statement) timeout for every execution on that connection. Set once at startup. |
| Per-query override | `DataExecutorRequest.CommandTimeoutInSeconds` (default `null`) | Overrides the global default for one execution. `null` → use the connection default; `0` → no timeout (infinite); negative → throws. |

This is the **command** timeout (how long a statement may run), distinct from
`DataConnecionString.TimeoutInSeconds`, which is the **connection-open** timeout.
The 5-second default is intentionally tight: a query that needs more is a prompt
to optimize the SQL or rethink the design, not to silently raise the ceiling.

### Connection-string templates per database type

```csharp
// SQLite (file-based)
var conn = new DataConnecionString(DataConnectionStringType.SQLite)
{
    DatabaseSource = "/data/foo.db",
    TimeoutInSeconds = 30,
};
// Produces: Data Source=/data/foo.db;Foreign Keys=true;Pooling=true;Default Timeout=30;

// SQLite (in-memory, shared cache — for tests)
var conn = new DataConnecionString(DataConnectionStringType.SQLiteInMemory)
{
    DatabaseSource = "FooTestDb",   // optional name; defaults to "DefaultInMemory"
};

// PostgreSQL
var conn = new DataConnecionString(DataConnectionStringType.PostgreSQL)
{
    ServerName = "localhost",
    DatabaseSource = "foo_db",
    Username = "foo",
    Password = "secret",
};
// Produces: Host=localhost;Database=foo_db;Username=foo;Password=secret;Timeout=20;

// MySQL
var conn = new DataConnecionString(DataConnectionStringType.MySQL)
{
    ServerName = "localhost",
    DatabaseSource = "foo_db",
    Username = "foo",
    Password = "secret",
};
// Produces: Server=localhost;Database=foo_db;User ID=foo;Password=secret;Connection Timeout=20;AutoEnlist=true;
```

### Raw connection string when the template is insufficient

```csharp
var conn = new DataConnecionString(
    DataConnectionStringType.PostgreSQL,
    "Host=db.example.com;Port=5433;Database=foo;Username=admin;Password=secret;SslMode=Require");
// The string is used as-is; properties are ignored.
```

## Common pitfalls

### Injecting `IDataConnectionFactory` directly

```csharp
// ❌ DI has no way to pick between Foo's database and Bar's database.
public FooRepository(IDataConnectionFactory connectionFactory, ILogger<FooRepository> logger)
    : base(logger)
{
    this._connectionFactory = connectionFactory;
}

// ✅ Inject the marker.
public FooRepository(IFooDatabaseFactory connectionFactory, ILogger<FooRepository> logger)
    : base(logger)
{
    this._connectionFactory = connectionFactory;
}
```

### Calling `Open()` on an already-open connection

```csharp
// ❌ The factory returned an open connection; this throws.
using var connection = await this._connectionFactory.CreateOpenConnectionAsync(cancellationToken);
await connection.OpenAsync(cancellationToken);

// ✅
using var connection = await this._connectionFactory.CreateOpenConnectionAsync(cancellationToken);
// ready to use
```

### Forgetting to dispose

```csharp
// ❌ Connection leak.
public async Task<Foo?> ReadAsync(string id, CancellationToken ct = default)
{
    var connection = await this._connectionFactory.CreateOpenConnectionAsync(ct);
    return await connection.QuerySingleOrDefaultAsync<Foo>(query, new { Id = id });
}

// ✅
public async Task<Foo?> ReadAsync(string id, CancellationToken ct = default)
{
    using var connection = await this._connectionFactory.CreateOpenConnectionAsync(ct);
    return await connection.QuerySingleOrDefaultAsync<Foo>(query, new { Id = id });
}
```

### Hand-built raw connection string when the template would have worked

```csharp
// ❌ Easy to typo, no help from the type system.
var conn = new DataConnecionString(
    DataConnectionStringType.SQLite,
    "Data Source=/data/foo.db;Foreign Keys=true;Pooling=true;Default Timeout=20;");

// ✅
var conn = new DataConnecionString(DataConnectionStringType.SQLite)
{
    DatabaseSource = "/data/foo.db",
    TimeoutInSeconds = 20,
};
```

### Wrong type for in-memory testing

```csharp
// ❌ Creates a file named "TestDb" on disk.
var conn = new DataConnecionString(DataConnectionStringType.SQLite)
{
    DatabaseSource = "TestDb",
};

// ✅ Shared in-memory database.
var conn = new DataConnecionString(DataConnectionStringType.SQLiteInMemory)
{
    DatabaseSource = "TestDb",
};
```

## Quick reference

### Using statements

```csharp
using Roadbed.Data;            // IDataConnectionFactory, DataConnecionString, DataExecutorRequest
using Roadbed.Data.Sqlite;     // SqliteConnectionFactory (only in factory implementation files)
using Roadbed.Data.Postgresql; // PostgresqlConnectionFactory
using Roadbed.Data.MySql;      // MySqlConnectionFactory
```

### `DataConnecionString` defaults

| Property                   | Default | Meaning                                    |
| -------------------------- | ------- | ------------------------------------------ |
| `TimeoutInSeconds`         | `20`    | Connection-open (connect) timeout.         |
| `CommandTimeoutInSeconds`  | `5`     | Default command (statement) timeout.       |

### `DataExecutorRequest` defaults

| Property                  | Default  |
| ------------------------- | -------- |
| `RetriesEnabled`          | `true`   |
| `MaxRetries`              | `3`      |
| `DelayBetweenRetries`     | `100ms`  |
| `DelayMultiplierEnabled`  | `true`   |
| `CommandTimeoutInSeconds` | `null`   |

`CommandTimeoutInSeconds = null` falls back to the connection's
`CommandTimeoutInSeconds` (default `5`); `0` = no timeout; negative throws.

Linear backoff with default values: `100ms`, `200ms`, `300ms`.

### Decision flow for adding a new database

```
1. Pick the per-database package: Sqlite / Postgresql / MySql
2. Define IFooDatabaseFactory : IDataConnectionFactory
3. Define FooDatabaseFactory(DataConnecionString) : <PerDb>ConnectionFactory(connection), IFooDatabaseFactory
4. Build the DataConnecionString in the installer (template-based; raw only when needed)
5. services.AddSingleton<IFooDatabaseFactory>(new FooDatabaseFactory(connectionString))
6. ServiceLocator.SetLocatorProvider(services.BuildServiceProvider())
7. Inject IFooDatabaseFactory in repositories
```
