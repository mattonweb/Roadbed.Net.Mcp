# Roadbed.Data.Postgresql Reference

Concrete PostgreSQL implementation of the `Roadbed.Data` abstractions. Provides a connection factory and a Dapper-based query executor with retry for transient PostgreSQL errors (Npgsql `PostgresException` SQLSTATE codes).

Read [`reference-roadbed-data.md`](reference-roadbed-data.md) first for the marker-interface pattern and `DataExecutorRequest` semantics.

## Type catalog

| Type                          | Kind         | Purpose                                                                                  |
| ----------------------------- | ------------ | ---------------------------------------------------------------------------------------- |
| `PostgresqlConnectionFactory` | Class        | Concrete `IDataConnectionFactory`. Creates `Npgsql.NpgsqlConnection` instances.          |
| `PostgresqlExecutor`          | Static class | `ExecuteAsync`, `QueryAsync<T>`, `QuerySingleOrDefaultAsync<T>`, `ExecuteScalarAsync<T>` with retry. |

## MUST

- **MUST** call `PostgresqlExecutor.*Async` from repository methods. The executor handles connection lifecycle, retries on transient SQLSTATE codes, and structured logging.
- **MUST** create a marker interface (`IFooDatabaseFactory : IDataConnectionFactory`) and a marker implementation (`FooDatabaseFactory : PostgresqlConnectionFactory, IFooDatabaseFactory`) — see `reference-roadbed-data.md`.
- **MUST** use PostgreSQL syntax: `true`/`false` for booleans, `BIGINT GENERATED ALWAYS AS IDENTITY` (or `BIGSERIAL`) for auto-increment, `INSERT … RETURNING …` to get back the inserted row, `INSERT … ON CONFLICT (col) DO UPDATE SET col = EXCLUDED.col` for upsert.
- **MUST** use Dapper-style `@ParamName` placeholders. The executor converts them to Npgsql parameters automatically.
- **MUST** pass `CancellationToken` through to every executor call.

## MUST NOT

- **MUST NOT** wrap `PostgresqlExecutor.*Async` calls in your own retry loop. The executor retries 16 SQLSTATE codes across five error classes (08, 40, 53, 57, 58).
- **MUST NOT** wrap them in a `try/catch (PostgresException)` looking for transient codes — the executor catches and rethrows as `InvalidOperationException` after exhausting retries.
- **MUST NOT** use SQLite syntax (`1`/`0` for booleans, `INSERT OR REPLACE`, `AUTOINCREMENT`, `LAST_INSERT_ROWID()`).
- **MUST NOT** use `INSERT … RETURNING` with `ExecuteAsync` — `RETURNING` data is silently discarded. Use `QuerySingleOrDefaultAsync<T>` to capture the returned row.
- **MUST NOT** treat HTTP-style errors here. PostgreSQL connection-string templates use `Host=`, not `Server=`.

## Code patterns

### Repository using `PostgresqlExecutor`

```csharp
namespace Foo.Database;

using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Roadbed;
using Roadbed.Data;
using Roadbed.Data.Postgresql;

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

    public async Task<Foo?> ReadAsync(long id, CancellationToken cancellationToken = default)
    {
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
            RetriesEnabled = false,
        };

        return await PostgresqlExecutor.QuerySingleOrDefaultAsync<Foo>(
            request,
            this._connectionFactory,
            this.Logger,
            cancellationToken);
    }

    public async Task<Foo> CreateAsync(Foo entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        // Use RETURNING to get the inserted row in a single round-trip.
        var request = new DataExecutorRequest(
            @"INSERT INTO foo (name, is_active)
              VALUES (@Name, @IsActive)
              RETURNING
                   id
                  ,name
                  ,is_active
              ;")
        {
            Parameters = new { entity.Name, entity.IsActive },
        };

        var inserted = await PostgresqlExecutor.QuerySingleOrDefaultAsync<Foo>(
            request,
            this._connectionFactory,
            this.Logger,
            cancellationToken);

        return inserted!;
    }
}
```

### Upsert with `ON CONFLICT … DO UPDATE`

```csharp
var request = new DataExecutorRequest(
    @"INSERT INTO foo (external_id, name, is_active)
      VALUES (@ExternalId, @Name, @IsActive)
      ON CONFLICT (external_id) DO UPDATE SET
           name = EXCLUDED.name
          ,is_active = EXCLUDED.is_active
      RETURNING
           id
          ,external_id
          ,name
          ,is_active
      ;")
{
    Parameters = new { entity.ExternalId, entity.Name, entity.IsActive },
};

var result = await PostgresqlExecutor.QuerySingleOrDefaultAsync<Foo>(
    request, this._connectionFactory, this.Logger, cancellationToken);
```

### Executor method signatures

```csharp
PostgresqlExecutor.ExecuteAsync(
    DataExecutorRequest request,
    IDataConnectionFactory connectionFactory,
    ILogger? logger = null,
    CancellationToken cancellationToken = default);   // Task<int>

PostgresqlExecutor.QueryAsync<T>(...);                     // Task<IEnumerable<T>>
PostgresqlExecutor.QuerySingleOrDefaultAsync<T>(...);      // Task<T?>
PostgresqlExecutor.ExecuteScalarAsync<T>(...);             // Task<T?>
```

## Common pitfalls

### Using `ExecuteAsync` with `RETURNING`

```csharp
// ❌ The RETURNING data is thrown away — ExecuteAsync only returns rows-affected.
var request = new DataExecutorRequest("INSERT INTO foo (name) VALUES (@Name) RETURNING id, name")
{
    Parameters = new { Name = "Bar" },
};
int rowsAffected = await PostgresqlExecutor.ExecuteAsync(
    request, this._connectionFactory, this.Logger, cancellationToken);

// ✅ Use QuerySingleOrDefaultAsync<T> to capture the row.
Foo? inserted = await PostgresqlExecutor.QuerySingleOrDefaultAsync<Foo>(
    request, this._connectionFactory, this.Logger, cancellationToken);
```

### SQLite syntax in PostgreSQL queries

```csharp
// ❌ Native PostgreSQL boolean type expects true/false, not 1/0.
var request = new DataExecutorRequest("SELECT id FROM foo WHERE is_active = 1");

// ✅
var request = new DataExecutorRequest("SELECT id FROM foo WHERE is_active = true");
```

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
    catch (PostgresException ex) when (ex.SqlState is "40001" or "40P01")
    {
        await Task.Delay(100 * attempt);
    }
}

// ✅
var request = new DataExecutorRequest(sql) { Parameters = parameters };
return await PostgresqlExecutor.QuerySingleOrDefaultAsync<Foo>(
    request, this._connectionFactory, this.Logger, ct);
```

### Wrong connection-string keys

```csharp
// ❌ MySQL syntax — Npgsql doesn't recognize "Server=" or "User ID=".
var conn = new DataConnecionString(DataConnectionStringType.PostgreSQL,
    "Server=localhost;Database=foo;User ID=admin;Password=secret");

// ✅ Use the property template; it produces the right keys.
var conn = new DataConnecionString(DataConnectionStringType.PostgreSQL)
{
    ServerName = "localhost",
    DatabaseSource = "foo",
    Username = "admin",
    Password = "secret",
};
// Produces: Host=localhost;Database=foo;Username=admin;Password=secret;Timeout=20;
```

## Quick reference

### Using statements

```csharp
using Roadbed.Data;              // DataExecutorRequest, IDataConnectionFactory
using Roadbed.Data.Postgresql;   // PostgresqlExecutor, PostgresqlConnectionFactory
```

### Method-selection cheat sheet

```
INSERT / UPDATE / DELETE / DDL              → ExecuteAsync()                  → int rows affected
INSERT … RETURNING (single row)             → QuerySingleOrDefaultAsync<T>()  → T?
SELECT multiple rows                        → QueryAsync<T>()                 → IEnumerable<T>
SELECT zero or one row                      → QuerySingleOrDefaultAsync<T>()  → T?
SELECT a single value (COUNT, MAX, etc.)    → ExecuteScalarAsync<T>()         → T?
```

### Transient PostgreSQL SQLSTATE codes (retried automatically)

| Class | Category               | Codes                                       |
| ----- | ---------------------- | ------------------------------------------- |
| 08    | Connection Exception   | `08000`, `08001`, `08003`, `08004`, `08006` |
| 40    | Transaction Rollback   | `40001`, `40P01`                            |
| 53    | Insufficient Resources | `53000`, `53100`, `53200`, `53300`          |
| 57    | Operator Intervention  | `57P01`, `57P02`, `57P03`                   |
| 58    | System Error           | `58000`, `58030`                            |

### PostgreSQL-specific syntax

| Concept              | PostgreSQL syntax                                                |
| -------------------- | ---------------------------------------------------------------- |
| Boolean column       | `BOOLEAN` (`true` / `false`)                                     |
| Auto-increment ID    | `BIGINT GENERATED ALWAYS AS IDENTITY` or `BIGSERIAL`             |
| Get inserted row     | `INSERT … RETURNING id, …` (use `QuerySingleOrDefaultAsync<T>`)  |
| Upsert               | `INSERT … ON CONFLICT (col) DO UPDATE SET col = EXCLUDED.col`    |
| Concurrency model    | MVCC with row-level locking                                      |
