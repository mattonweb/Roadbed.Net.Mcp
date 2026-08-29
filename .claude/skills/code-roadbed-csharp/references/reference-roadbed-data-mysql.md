# Roadbed.Data.MySql Reference

Concrete MySQL implementation of the `Roadbed.Data` abstractions. Built on the `MySqlConnector` ADO.NET driver, **not** Oracle's `MySql.Data`. Provides a connection factory and a Dapper-based query executor with retry for transient MySQL errors.

The connection-string template enables `AutoEnlist=true` (the default for MySqlConnector), which lets connections automatically enlist in the ambient `System.Transactions.Transaction` for `TransactionScope` use. **Oracle's `MySql.Data` does not support this** — that's the primary reason MySqlConnector was chosen.

Read [`reference-roadbed-data.md`](reference-roadbed-data.md) first for the marker-interface pattern and `DataExecutorRequest` semantics.

## Type catalog

| Type                     | Kind         | Purpose                                                                                       |
| ------------------------ | ------------ | --------------------------------------------------------------------------------------------- |
| `MySqlConnectionFactory` | Class        | Concrete `IDataConnectionFactory`. Creates `MySqlConnector.MySqlConnection` instances.        |
| `MySqlExecutor`          | Static class | `ExecuteAsync`, `QueryAsync<T>`, `QuerySingleOrDefaultAsync<T>`, `ExecuteScalarAsync<T>` with retry. |

## MUST

- **MUST** use the `MySqlConnector` package as the driver. This is what `Roadbed.Data.MySql` already takes a `PackageReference` to.
- **MUST** call `MySqlExecutor.*Async` from repository methods. The executor handles connection lifecycle, retries on transient error numbers, and structured logging.
- **MUST** create a marker interface (`IFooDatabaseFactory : IDataConnectionFactory`) and a marker implementation (`FooDatabaseFactory : MySqlConnectionFactory, IFooDatabaseFactory`) — see `reference-roadbed-data.md`.
- **MUST** use MySQL syntax: `1`/`0` for booleans (`TINYINT(1)`), `AUTO_INCREMENT` for surrogate keys, `LAST_INSERT_ID()` to retrieve the ID of an inserted row, `INSERT … ON DUPLICATE KEY UPDATE col = VALUES(col)` for upsert.
- **MUST** wrap multi-statement transactions in `using var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled)` and call `scope.Complete()` to commit. Each open connection auto-enlists because the template sets `AutoEnlist=true`.

## MUST NOT

- **MUST NOT** use Oracle's `MySql.Data` package. It does **not** enlist in `System.Transactions.Transaction`, so `TransactionScope` silently bypasses MySQL operations through it — leading to data integrity bugs that are easy to miss in development.
- **MUST NOT** wrap `MySqlExecutor.*Async` calls in your own retry loop. The executor retries 16 transient codes (server-side `1xxx`, lock/deadlock `1205`/`1213`, client-side `2xxx`).
- **MUST NOT** use PostgreSQL syntax (`true`/`false`, `RETURNING`, `BIGSERIAL`, `ON CONFLICT … DO UPDATE … EXCLUDED`).
- **MUST NOT** enable `UseXaTransactions=true` unless you know you need true XA two-phase commit across multiple resource managers (e.g., MySQL + a second DB + MSMQ in the same `TransactionScope`). XA requires `XA_RECOVER_ADMIN` on the server, holds row locks longer, and adds operational complexity. The default `AutoEnlist` is sufficient for single-MySQL-connection enlistment.
- **MUST NOT** forget `TransactionScopeAsyncFlowOption.Enabled` when using `TransactionScope` with async code. Without it, the ambient transaction does not flow across `await` boundaries.

## Code patterns

### Repository using `MySqlExecutor`

```csharp
namespace Foo.Database;

using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Roadbed;
using Roadbed.Data;
using Roadbed.Data.MySql;

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

        return await MySqlExecutor.QuerySingleOrDefaultAsync<Foo>(
            request,
            this._connectionFactory,
            this.Logger,
            cancellationToken);
    }

    public async Task<Foo> CreateAsync(Foo entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        // MySQL has no RETURNING. Use a multi-statement command and let Dapper
        // collapse it into a single round-trip.
        var request = new DataExecutorRequest(
            @"INSERT INTO foo (name, is_active)
              VALUES (@Name, @IsActive);
              SELECT
                   id
                  ,name
                  ,is_active
              FROM
                  foo
              WHERE
                  id = LAST_INSERT_ID()
              ;")
        {
            Parameters = new { entity.Name, IsActive = entity.IsActive ? 1 : 0 },
        };

        var inserted = await MySqlExecutor.QuerySingleOrDefaultAsync<Foo>(
            request,
            this._connectionFactory,
            this.Logger,
            cancellationToken);

        return inserted!;
    }
}
```

### Upsert with `ON DUPLICATE KEY UPDATE`

```csharp
var request = new DataExecutorRequest(
    @"INSERT INTO foo (external_id, name, is_active)
      VALUES (@ExternalId, @Name, @IsActive)
      ON DUPLICATE KEY UPDATE
           name = VALUES(name)
          ,is_active = VALUES(is_active)
      ;")
{
    Parameters = new { entity.ExternalId, entity.Name, IsActive = entity.IsActive ? 1 : 0 },
};

await MySqlExecutor.ExecuteAsync(request, this._connectionFactory, this.Logger, cancellationToken);
```

### Distributed transactions with `TransactionScope`

```csharp
using System.Transactions;

public async Task TransferAsync(long fromId, long toId, decimal amount, CancellationToken cancellationToken = default)
{
    using var scope = new TransactionScope(
        TransactionScopeOption.Required,
        new TransactionOptions { IsolationLevel = IsolationLevel.ReadCommitted },
        TransactionScopeAsyncFlowOption.Enabled);   // required for async work

    await this.DebitAsync(fromId, amount, cancellationToken);
    await this.CreditAsync(toId, amount, cancellationToken);

    scope.Complete();   // commit; without this call, both operations roll back
}
```

Each `MySqlExecutor.*Async` call opens a new connection, which auto-enlists in the ambient transaction because the template sets `AutoEnlist=true`. Failure inside the scope causes both operations to roll back.

### Executor method signatures

```csharp
MySqlExecutor.ExecuteAsync(
    DataExecutorRequest request,
    IDataConnectionFactory connectionFactory,
    ILogger? logger = null,
    CancellationToken cancellationToken = default);   // Task<int>

MySqlExecutor.QueryAsync<T>(...);                     // Task<IEnumerable<T>>
MySqlExecutor.QuerySingleOrDefaultAsync<T>(...);      // Task<T?>
MySqlExecutor.ExecuteScalarAsync<T>(...);             // Task<T?>
```

## Common pitfalls

### Using `MySql.Data` instead of `MySqlConnector`

```csharp
// ❌ MySql.Data does not enlist in TransactionScope.
// Operations inside `using var scope = new TransactionScope(...)` are NOT part of the
// ambient transaction. Roll-back will silently miss them.
using MySql.Data.MySqlClient;

// ✅ Roadbed.Data.MySql uses MySqlConnector, which auto-enlists.
using MySqlConnector;
```

### Using `RETURNING` clause

```csharp
// ❌ MySQL has no RETURNING — this throws a SQL syntax error.
var request = new DataExecutorRequest(
    "INSERT INTO foo (name) VALUES (@Name) RETURNING id, name");

// ✅ Use a multi-statement command with LAST_INSERT_ID().
var request = new DataExecutorRequest(
    @"INSERT INTO foo (name) VALUES (@Name);
      SELECT id, name FROM foo WHERE id = LAST_INSERT_ID();");
```

### PostgreSQL upsert syntax

```csharp
// ❌ ON CONFLICT is PostgreSQL syntax.
var request = new DataExecutorRequest(
    @"INSERT INTO foo (id, name) VALUES (@Id, @Name)
      ON CONFLICT (id) DO UPDATE SET name = EXCLUDED.name");

// ✅ MySQL uses ON DUPLICATE KEY UPDATE with VALUES().
var request = new DataExecutorRequest(
    @"INSERT INTO foo (id, name) VALUES (@Id, @Name)
      ON DUPLICATE KEY UPDATE name = VALUES(name)");
```

### Forgetting `TransactionScopeAsyncFlowOption.Enabled`

```csharp
// ❌ Ambient transaction does not flow across await; second call runs outside the scope.
using var scope = new TransactionScope();
await MySqlExecutor.ExecuteAsync(insertRequest, this._factory, this.Logger, cancellationToken);
await MySqlExecutor.ExecuteAsync(updateRequest, this._factory, this.Logger, cancellationToken);
scope.Complete();

// ✅
using var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
await MySqlExecutor.ExecuteAsync(insertRequest, this._factory, this.Logger, cancellationToken);
await MySqlExecutor.ExecuteAsync(updateRequest, this._factory, this.Logger, cancellationToken);
scope.Complete();
```

### Boolean comparison with `true`/`false`

```csharp
// ❌ MySQL TINYINT(1) is treated as a number; mixing literal `true` with column comparison can confuse readers.
var request = new DataExecutorRequest("SELECT id FROM foo WHERE is_active = true");

// ✅ MySQL idiom is 1/0.
var request = new DataExecutorRequest("SELECT id FROM foo WHERE is_active = 1");
```

## Quick reference

### Using statements

```csharp
using Roadbed.Data;          // DataExecutorRequest, IDataConnectionFactory
using Roadbed.Data.MySql;    // MySqlExecutor, MySqlConnectionFactory
using MySqlConnector;        // MySqlException (for testing only)
```

### Method-selection cheat sheet

```
INSERT / UPDATE / DELETE / DDL              → ExecuteAsync()                  → int rows affected
INSERT then read inserted row               → QuerySingleOrDefaultAsync<T>()
                                              (multi-statement with LAST_INSERT_ID())
SELECT multiple rows                        → QueryAsync<T>()                 → IEnumerable<T>
SELECT zero or one row                      → QuerySingleOrDefaultAsync<T>()  → T?
SELECT a single value (COUNT, MAX, etc.)    → ExecuteScalarAsync<T>()         → T?
```

### Transient MySQL error numbers (retried automatically)

| Category                          | Numbers                                                                        |
| --------------------------------- | ------------------------------------------------------------------------------ |
| Server connection / resource      | `1040`, `1042`, `1043`, `1077`, `1129`, `1158`, `1159`, `1160`, `1161`, `1184` |
| Lock / deadlock                   | `1205`, `1213`                                                                 |
| Client-side connection            | `2002`, `2003`, `2006`, `2013`                                                 |

### MySQL-specific syntax

| Concept              | MySQL syntax                                                          |
| -------------------- | --------------------------------------------------------------------- |
| Boolean column       | `TINYINT(1)` (`1` / `0`)                                              |
| Auto-increment ID    | `BIGINT AUTO_INCREMENT`                                               |
| Get inserted ID      | `SELECT LAST_INSERT_ID()` (multi-statement after the INSERT)          |
| Upsert               | `INSERT … ON DUPLICATE KEY UPDATE col = VALUES(col)`                  |
| TransactionScope flag| `AutoEnlist=true` (template default)                                  |
