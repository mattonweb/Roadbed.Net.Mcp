# Roadbed.Data.Sqlite Reference

Concrete SQLite implementation of the `Roadbed.Data` abstractions. Provides a connection factory, a Dapper-based query executor with built-in retry for transient SQLite errors, and a `KeepAlive()` extension for in-memory testing.

Read [`reference-roadbed-data.md`](reference-roadbed-data.md) first for the marker-interface pattern and `DataExecutorRequest` semantics.

## Type catalog

| Type                         | Kind         | Purpose                                                                                                |
| ---------------------------- | ------------ | ------------------------------------------------------------------------------------------------------ |
| `SqliteConnectionFactory`    | Class        | Concrete `IDataConnectionFactory`. Creates `Microsoft.Data.Sqlite.SqliteConnection` instances.         |
| `SqliteExecutor`             | Static class | `ExecuteAsync`, `QueryAsync<T>`, `QuerySingleOrDefaultAsync<T>`, `ExecuteScalarAsync<T>` with retry.   |
| `SqliteConnectionExtensions` | Static class | `KeepAlive()` extension to keep an in-memory database alive across multiple connections.               |

## MUST

- **MUST** call `SqliteExecutor.*Async` from repository methods rather than calling Dapper directly. The executor handles connection lifecycle, retries on transient errors, and structured logging.
- **MUST** create a marker interface (`IFooDatabaseFactory : IDataConnectionFactory`) and a marker implementation (`FooDatabaseFactory : SqliteConnectionFactory, IFooDatabaseFactory`) — see `reference-roadbed-data.md`.
- **MUST** pass `CancellationToken` through to every executor call.
- **MUST** use `KeepAlive()` in tests that share an in-memory database across multiple connections. Without it, the database is destroyed when the last connection closes.
- **MUST** use SQLite syntax: `1`/`0` for booleans, `INTEGER PRIMARY KEY AUTOINCREMENT` for surrogate keys, `INSERT OR REPLACE` (or `INSERT … ON CONFLICT`) for upsert, `LAST_INSERT_ROWID()` to retrieve the ID of an inserted row.

## MUST NOT

- **MUST NOT** wrap `SqliteExecutor.*Async` calls in your own retry loop. The executor already retries transient codes (`5` BUSY, `6` LOCKED, `10` IOERR, `13` FULL).
- **MUST NOT** wrap them in a `try/catch (SqliteException)` looking for transient codes — the executor catches and rethrows as `InvalidOperationException` after exhausting retries.
- **MUST NOT** use PostgreSQL syntax (`true`/`false`, `RETURNING`, `BIGSERIAL`, `ON CONFLICT … DO UPDATE … EXCLUDED`) — the syntax names look similar but the SQL doesn't compile against SQLite.
- **MUST NOT** open the connection yourself after `factory.CreateOpenConnectionAsync(...)`. The connection is already open.

## Code patterns

### Repository using `SqliteExecutor`

```csharp
namespace Foo.Database;

using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Roadbed;
using Roadbed.Data;
using Roadbed.Data.Sqlite;

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

        var request = new DataExecutorRequest(
            @"SELECT
                 f.id
                ,f.name
                ,f.is_active
             FROM
                 foo AS f
             WHERE
                 f.id = @Id
             ;")
        {
            Parameters = new { Id = id },
            RetriesEnabled = false,  // Read-only, fast-fail is fine
        };

        return await SqliteExecutor.QuerySingleOrDefaultAsync<Foo>(
            request,
            this._connectionFactory,
            this.Logger,
            cancellationToken);
    }

    public async Task<Foo> CreateAsync(Foo entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var insertRequest = new DataExecutorRequest(
            @"INSERT INTO foo (name, is_active)
              VALUES (@Name, @IsActive)
              ;")
        {
            Parameters = new { entity.Name, IsActive = entity.IsActive ? 1 : 0 },
        };

        await SqliteExecutor.ExecuteAsync(
            insertRequest,
            this._connectionFactory,
            this.Logger,
            cancellationToken);

        var idRequest = new DataExecutorRequest("SELECT LAST_INSERT_ROWID();")
        {
            RetriesEnabled = false,
        };

        long newId = await SqliteExecutor.ExecuteScalarAsync<long>(
            idRequest,
            this._connectionFactory,
            this.Logger,
            cancellationToken);

        entity.Id = newId.ToString();
        return entity;
    }
}
```

### Executor method signatures (all four share the same shape)

```csharp
SqliteExecutor.ExecuteAsync(
    DataExecutorRequest request,
    IDataConnectionFactory connectionFactory,
    ILogger? logger = null,
    CancellationToken cancellationToken = default);   // returns Task<int> (rows affected)

SqliteExecutor.QueryAsync<T>(...);                     // returns Task<IEnumerable<T>>
SqliteExecutor.QuerySingleOrDefaultAsync<T>(...);      // returns Task<T?>
SqliteExecutor.ExecuteScalarAsync<T>(...);             // returns Task<T?>
```

### In-memory database for tests (use `KeepAlive`)

```csharp
namespace Foo.Tests;

using Microsoft.Data.Sqlite;
using Roadbed.Data;
using Roadbed.Data.Sqlite;

[TestClass]
public sealed class FooRepositoryTests
{
    private SqliteConnection _keepAliveConnection = null!;
    private IDisposable _keepAliveHandle = null!;
    private IFooDatabaseFactory _factory = null!;

    [TestInitialize]
    public async Task Setup()
    {
        var connectionString = new DataConnecionString(DataConnectionStringType.SQLiteInMemory)
        {
            DatabaseSource = "FooTestDb",
        };

        // Open and keep-alive a connection so the in-memory database survives
        // between connections opened by the factory.
        this._keepAliveConnection = new SqliteConnection(connectionString.ConnectionString);
        this._keepAliveHandle = this._keepAliveConnection.KeepAlive();

        this._factory = new FooDatabaseFactory(connectionString);

        // Run schema bootstrap...
    }

    [TestCleanup]
    public void Teardown()
    {
        this._keepAliveHandle.Dispose();
        this._keepAliveConnection.Dispose();
    }
}
```

### Upsert with `INSERT OR REPLACE`

```csharp
var request = new DataExecutorRequest(
    @"INSERT OR REPLACE INTO foo (id, name, is_active)
      VALUES (@Id, @Name, @IsActive)
      ;")
{
    Parameters = new { entity.Id, entity.Name, IsActive = entity.IsActive ? 1 : 0 },
};

await SqliteExecutor.ExecuteAsync(request, this._connectionFactory, this.Logger, cancellationToken);
```

## Common pitfalls

### Building a manual retry loop

```csharp
// ❌ Duplicates the executor's built-in logic.
for (int attempt = 0; attempt < 3; attempt++)
{
    try
    {
        using var connection = await this._connectionFactory.CreateOpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<Foo>(sql, parameters);
    }
    catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
    {
        await Task.Delay(100 * attempt);
    }
}

// ✅ Configure the request and let the executor handle retries.
var request = new DataExecutorRequest(sql) { Parameters = parameters };
return await SqliteExecutor.QuerySingleOrDefaultAsync<Foo>(
    request, this._connectionFactory, this.Logger, ct);
```

### PostgreSQL syntax in SQLite SQL

```csharp
// ❌ SQLite doesn't have a native boolean type and doesn't support RETURNING.
var request = new DataExecutorRequest(
    @"INSERT INTO foo (name, is_active)
      VALUES (@Name, true)
      RETURNING id;");

// ✅ Use SQLite syntax.
var request = new DataExecutorRequest(
    @"INSERT INTO foo (name, is_active) VALUES (@Name, 1);");
// Then SELECT LAST_INSERT_ROWID() in a follow-up call.
```

### Forgetting `KeepAlive` in tests

```csharp
// ❌ As soon as the test's connection closes, the in-memory database is destroyed.
var conn = new DataConnecionString(DataConnectionStringType.SQLiteInMemory)
{
    DatabaseSource = "FooTestDb",
};
var factory = new FooDatabaseFactory(conn);
using var schema = factory.CreateOpenConnection();
// ... create tables ...
// schema disposed here; tables are gone before the test repository runs.

// ✅ Hold a keep-alive connection for the lifetime of the test.
var keepAlive = new SqliteConnection(conn.ConnectionString);
using var handle = keepAlive.KeepAlive();
// schema setup, then test code, then handle.Dispose() at end of test.
```

### Forgetting `using` on a connection (when not going through the executor)

```csharp
// ❌ Connection leak.
var connection = await this._connectionFactory.CreateOpenConnectionAsync(cancellationToken);
return await connection.QuerySingleOrDefaultAsync<Foo>(sql, parameters);

// ✅
using var connection = await this._connectionFactory.CreateOpenConnectionAsync(cancellationToken);
return await connection.QuerySingleOrDefaultAsync<Foo>(sql, parameters);

// ✅ Or just use the executor, which manages the connection for you:
return await SqliteExecutor.QuerySingleOrDefaultAsync<Foo>(
    new DataExecutorRequest(sql) { Parameters = parameters },
    this._connectionFactory,
    this.Logger,
    cancellationToken);
```

## Quick reference

### Using statements

```csharp
using Roadbed.Data;          // DataExecutorRequest, IDataConnectionFactory
using Roadbed.Data.Sqlite;   // SqliteExecutor, SqliteConnectionFactory, SqliteConnectionExtensions
```

### Method-selection cheat sheet

```
INSERT / UPDATE / DELETE / DDL  → SqliteExecutor.ExecuteAsync()                  → int rows affected
SELECT multiple rows            → SqliteExecutor.QueryAsync<T>()                 → IEnumerable<T>
SELECT zero or one row          → SqliteExecutor.QuerySingleOrDefaultAsync<T>()  → T?
SELECT a single value           → SqliteExecutor.ExecuteScalarAsync<T>()         → T?
```

### Transient SQLite codes (retried automatically)

| Code | Constant         | Meaning                |
| ---- | ---------------- | ---------------------- |
| `5`  | `SQLITE_BUSY`    | Database is locked     |
| `6`  | `SQLITE_LOCKED`  | Table is locked        |
| `10` | `SQLITE_IOERR`   | Disk I/O error         |
| `13` | `SQLITE_FULL`    | Disk full              |

### SQLite-specific gotchas

| Concept              | SQLite syntax                              |
| -------------------- | ------------------------------------------ |
| Boolean column       | `INTEGER` (`1` / `0`)                      |
| Auto-increment ID    | `INTEGER PRIMARY KEY AUTOINCREMENT`        |
| Get last inserted ID | `SELECT LAST_INSERT_ROWID();`              |
| Upsert               | `INSERT OR REPLACE INTO ...`               |
| Concurrency model    | File-level locking (busy-loop on contention) |
