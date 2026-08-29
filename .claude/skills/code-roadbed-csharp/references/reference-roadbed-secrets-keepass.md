# Roadbed.Secrets.KeePass Reference

Read-only access to KeePass2 (`.kdbx`) databases. Designed for **startup-time** use by the host application: open the database once, eagerly load every entry's standard string fields into an in-memory cache, then serve `Read(title)` lookups from the cache. The file is opened exactly once per `KeePassReader` instance and never re-opened.

Typical use case: hydrating SMTP credentials, HMAC keys, third-party API tokens, etc. into options POCOs in `Program.cs` before the DI container is built.

## Type catalog (3 types)

| Type               | Kind                  | Purpose                                                                                              |
| ------------------ | --------------------- | ---------------------------------------------------------------------------------------------------- |
| `IKeePassOptions`  | Interface             | Two strings the reader needs: `MasterKey` and `DatabasePath`. Host application owns how they are sourced. |
| `KeePassSecret`    | Sealed POCO           | Plain CLR snapshot of one entry — `Title`, `UserName`, `Password`, `Url`, `Notes`. `init`-only properties. |
| `KeePassReader`    | Class (**not sealed**) | Opens the database in its constructor, caches entries, serves `Read(string entryTitle)` from the cache. |

`KeePassReader` is intentionally **not sealed** so consumers can declare a one-line subclass per database when they need to manage multiple KeePass databases in the same DI container. Subclasses use the `protected` constructor (which accepts a non-generic `ILogger`) so they can pass their own typed `ILogger<TSubclass>`.

## MUST

- **MUST** treat `KeePassReader` as a **singleton**. The constructor opens the database, walks every entry, and closes the file. Constructing per-request would re-open the file on every resolve.
- **MUST** construct `KeePassReader` (and any subclass) at **startup**, before the DI container hands instances to consumers. Construction-time errors (missing file, blank options, wrong master key, malformed database) surface as exceptions — fail fast.
- **MUST** validate the result of `Read(...)` is what you expect; the call throws `InvalidOperationException` when the title doesn't exist in the cache. Do not swallow that exception silently.
- **MUST** treat the Title comparison as **case-sensitive ordinal**. `"Foo Smtp"` and `"foo smtp"` are different keys.
- **MUST** use a marker subinterface of `IKeePassOptions` for each distinct database (e.g., `IFooKeePassOptions : IKeePassOptions`) when the host application reads from more than one KeePass file. The marker is what lets DI register two different options instances side-by-side.
- **MUST** pair each marker options interface with a `KeePassReader` subclass (e.g., `FooKeePassReader : KeePassReader`) so consumers can inject a strongly-typed reader. The subclass body is one primary-constructor line.
- **MUST** source `MasterKey` from a real secret store (environment variable, vault, OS secret manager). The library does not enforce this — it just consumes whatever the options object provides.

## MUST NOT

- **MUST NOT** register `KeePassReader` as transient or scoped. Each resolution would re-open the database.
- **MUST NOT** call `new KeePassReader(...)` in request-handling code. The cache must be built once at startup.
- **MUST NOT** mutate `IKeePassOptions` after the reader is constructed. The reader has already loaded the cache; later changes are silently ignored.
- **MUST NOT** rely on duplicate-Title behavior. KeePass allows duplicate titles; on collision **the first entry wins** (insertion order from a recursive group walk). If you need uniqueness, enforce it in the `.kdbx` itself.
- **MUST NOT** reach for keyed singletons (`AddKeyedSingleton<KeePassReader>("foo", ...)`) when you have multiple databases — use the marker-interface + subclass pattern below instead. Keyed DI loses IntelliSense for the database name and collapses every reader's logger category to `KeePassReader`.
- **MUST NOT** subclass `KeePassReader` and override behavior — there are no `virtual` members. The subclass exists to give DI a distinct type to register and to give the logger its own category.
- **MUST NOT** put the `MasterKey` string in source control or committed `appsettings.json`. The interface accepts the value at runtime — supply it from the host's secret store.

## Code patterns

### Single-database on-ramp

When the host application reads from exactly one KeePass file, you do not need marker interfaces. Implement `IKeePassOptions` directly and register both the options and the reader as singletons.

```csharp
namespace Foo.Web;

using Roadbed.Secrets.KeePass;

internal sealed class FooKeePassOptions : IKeePassOptions
{
    public string MasterKey { get; init; } = string.Empty;

    public string DatabasePath { get; init; } = string.Empty;
}
```

```csharp
// Program.cs (or an IServiceCollectionInstaller)
services.AddSingleton<IKeePassOptions>(_ => new FooKeePassOptions
{
    MasterKey = Environment.GetEnvironmentVariable("FOO_KEEPASS_KEY")
        ?? throw new InvalidOperationException("FOO_KEEPASS_KEY is not set."),
    DatabasePath = configuration["Foo:KeePass:DatabasePath"]
        ?? throw new InvalidOperationException("Foo:KeePass:DatabasePath is not set."),
});

services.AddSingleton<KeePassReader>();
```

```csharp
// Consumer
public sealed class FooStartupHydrator
{
    private readonly KeePassReader _reader;

    public FooStartupHydrator(KeePassReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        this._reader = reader;
    }

    public string ReadSmtpPassword()
    {
        return this._reader.Read("Foo SMTP").Password;
    }
}
```

### Multi-database with marker interfaces

When the host reads from two (or more) distinct KeePass files — for example, a `Foo` database with application secrets and a `Bar` database with shared infrastructure secrets — declare a marker subinterface of `IKeePassOptions` for each database, a matching options class, and a one-line `KeePassReader` subclass for each. Consumers inject the strongly-typed subclass.

```csharp
namespace Foo.Web.Secrets;

using Roadbed.Secrets.KeePass;

// Marker interfaces — the empty body is the point. They give DI a distinct
// service type per database so two registrations can coexist.
public interface IFooKeePassOptions : IKeePassOptions
{
}

public interface IBarKeePassOptions : IKeePassOptions
{
}
```

```csharp
namespace Foo.Web.Secrets;

internal sealed class FooKeePassOptions : IFooKeePassOptions
{
    public string MasterKey { get; init; } = string.Empty;

    public string DatabasePath { get; init; } = string.Empty;
}

internal sealed class BarKeePassOptions : IBarKeePassOptions
{
    public string MasterKey { get; init; } = string.Empty;

    public string DatabasePath { get; init; } = string.Empty;
}
```

```csharp
namespace Foo.Web.Secrets;

using Microsoft.Extensions.Logging;
using Roadbed.Secrets.KeePass;

// Per-database reader subclasses. Each is one primary-constructor line and
// inherits Read(...) unchanged. The subclass exists so:
//   1. DI has a distinct type to register per database.
//   2. The logger category becomes FooKeePassReader / BarKeePassReader,
//      so log filters can target one database without affecting the other.
public sealed class FooKeePassReader(
    IFooKeePassOptions options,
    ILogger<FooKeePassReader> logger)
    : KeePassReader(options, logger);

public sealed class BarKeePassReader(
    IBarKeePassOptions options,
    ILogger<BarKeePassReader> logger)
    : KeePassReader(options, logger);
```

The subclass primary-constructor call `: KeePassReader(options, logger)` resolves to the `protected` `KeePassReader(IKeePassOptions, ILogger)` constructor. `ILogger<FooKeePassReader>` IS-A `ILogger`, so it flows in cleanly without any cast at the call site.

### DI registration

```csharp
// In Program.cs or an IServiceCollectionInstaller
services.AddSingleton<IFooKeePassOptions>(_ => new FooKeePassOptions
{
    MasterKey = Environment.GetEnvironmentVariable("FOO_KEEPASS_KEY")
        ?? throw new InvalidOperationException("FOO_KEEPASS_KEY is not set."),
    DatabasePath = configuration["Foo:KeePass:DatabasePath"]
        ?? throw new InvalidOperationException("Foo:KeePass:DatabasePath is not set."),
});

services.AddSingleton<IBarKeePassOptions>(_ => new BarKeePassOptions
{
    MasterKey = Environment.GetEnvironmentVariable("BAR_KEEPASS_KEY")
        ?? throw new InvalidOperationException("BAR_KEEPASS_KEY is not set."),
    DatabasePath = configuration["Bar:KeePass:DatabasePath"]
        ?? throw new InvalidOperationException("Bar:KeePass:DatabasePath is not set."),
});

services.AddSingleton<FooKeePassReader>();
services.AddSingleton<BarKeePassReader>();
```

### Consumer injection

Inject the per-database subclass directly. There are no `[FromKeyedServices(...)]` attributes, no string keys, no factory delegates — DI resolves each subclass through its own primary constructor and constructs it once.

```csharp
namespace Foo.Web.Secrets;

public sealed class StartupHydrator
{
    private readonly FooKeePassReader _fooReader;
    private readonly BarKeePassReader _barReader;

    public StartupHydrator(
        FooKeePassReader fooReader,
        BarKeePassReader barReader)
    {
        ArgumentNullException.ThrowIfNull(fooReader);
        ArgumentNullException.ThrowIfNull(barReader);

        this._fooReader = fooReader;
        this._barReader = barReader;
    }

    public void Hydrate(SmtpSettings smtp, ApiSettings api)
    {
        ArgumentNullException.ThrowIfNull(smtp);
        ArgumentNullException.ThrowIfNull(api);

        var smtpSecret = this._fooReader.Read("Foo SMTP");
        smtp.Username = smtpSecret.UserName;
        smtp.Password = smtpSecret.Password;

        var apiSecret = this._barReader.Read("Bar API Token");
        api.AccessToken = apiSecret.Password;
    }
}
```

### Reading the standard fields

`KeePassSecret` carries the five standard KeePass string fields. Custom string fields on a KeePass entry are not surfaced.

```csharp
var secret = this._fooReader.Read("Foo SMTP");

string title    = secret.Title;     // "Foo SMTP"
string username = secret.UserName;  // "smtp-user"
string password = secret.Password;  // "********"
string url      = secret.Url;       // "smtps://smtp.foo.example.com:465"
string notes    = secret.Notes;     // free-form
```

Empty fields surface as `string.Empty`, never `null`.

## Common pitfalls

### Registering the reader as transient or scoped

```csharp
// ❌ Re-opens the .kdbx file on every resolve. Throws on file-not-found
//    intermittently if the file is later moved, instead of failing fast at startup.
services.AddTransient<FooKeePassReader>();

// ✅ Open once, cache forever (until process restart).
services.AddSingleton<FooKeePassReader>();
```

### Using keyed DI for multiple databases

```csharp
// ❌ String-keyed registration loses the strongly-typed subclass and the
//    per-database logger category. Every consumer has to remember the right key.
services.AddKeyedSingleton<KeePassReader>("foo", (sp, _) =>
    new KeePassReader(sp.GetRequiredService<IFooKeePassOptions>(), /* logger */));

public sealed class StartupHydrator(
    [FromKeyedServices("foo")] KeePassReader fooReader,
    [FromKeyedServices("bar")] KeePassReader barReader) { /* … */ }

// ✅ Marker subinterface + subclass. Strongly typed, distinct logger
//    categories, no attributes at injection sites.
services.AddSingleton<FooKeePassReader>();
services.AddSingleton<BarKeePassReader>();

public sealed class StartupHydrator(
    FooKeePassReader fooReader,
    BarKeePassReader barReader) { /* … */ }
```

### Sharing one options class across multiple registrations

```csharp
// ❌ Both registrations resolve the SAME IKeePassOptions instance, so both
//    readers point at the same database — the second one is dead weight.
var sharedOptions = new FooKeePassOptions { MasterKey = key, DatabasePath = path };
services.AddSingleton<IKeePassOptions>(sharedOptions);
services.AddSingleton<FooKeePassReader>();
services.AddSingleton<BarKeePassReader>();   // Resolves IKeePassOptions → sharedOptions

// ✅ Distinct marker subinterfaces, distinct registrations.
services.AddSingleton<IFooKeePassOptions>(new FooKeePassOptions { /* … */ });
services.AddSingleton<IBarKeePassOptions>(new BarKeePassOptions { /* … */ });
```

### Hard-coding the master key in source

```csharp
// ❌ Master key is now in source control and the process binary.
internal sealed class FooKeePassOptions : IFooKeePassOptions
{
    public string MasterKey => "p@ssw0rd-in-source-control";   // 🔥
    public string DatabasePath { get; init; } = string.Empty;
}

// ✅ Pull from a real secret store at startup. The library treats MasterKey
//    as opaque — the host owns its provenance.
services.AddSingleton<IFooKeePassOptions>(_ => new FooKeePassOptions
{
    MasterKey = Environment.GetEnvironmentVariable("FOO_KEEPASS_KEY")
        ?? throw new InvalidOperationException("FOO_KEEPASS_KEY is not set."),
    DatabasePath = configuration["Foo:KeePass:DatabasePath"]!,
});
```

### Catching the missing-entry exception silently

```csharp
// ❌ The .kdbx is missing the entry, but the consumer carries on with a default
//    password that nobody set on purpose. Surfaces as "auth failed" later, not
//    "secret missing" now.
KeePassSecret secret;
try
{
    secret = this._fooReader.Read("Foo SMTP");
}
catch (InvalidOperationException)
{
    secret = new KeePassSecret { Password = "default" };
}

// ✅ Let the exception propagate at startup so the deploy fails fast.
var secret = this._fooReader.Read("Foo SMTP");
```

### Trying to override `Read` in a subclass

```csharp
// ❌ Read is non-virtual on KeePassReader. The subclass method hides the base
//    member with a `new` slot but isn't called when the consumer holds a
//    KeePassReader-typed reference, leading to surprising behavior.
public sealed class FooKeePassReader(
    IFooKeePassOptions options,
    ILogger<FooKeePassReader> logger)
    : KeePassReader(options, logger)
{
    public new KeePassSecret Read(string entryTitle)
    {
        // … extra logging, audit, whatever …
        return base.Read(entryTitle);
    }
}

// ✅ Wrap the reader at the consumer layer instead — for example, a service
//    that injects FooKeePassReader and adds the cross-cutting behavior in
//    its own method. Keep the subclass empty.
```

### Passing an `ILogger<KeePassReader>` from a subclass

```csharp
// ❌ DI doesn't auto-resolve a logger typed for the base class when the
//    subclass is being constructed; the subclass's own ILogger<TSubclass>
//    is what flows in. This pattern just doesn't compile under primary
//    constructors and adds noise everywhere it's tried.
public sealed class FooKeePassReader(
    IFooKeePassOptions options,
    ILogger<KeePassReader> logger)   // wrong — won't reach the protected ctor cleanly
    : KeePassReader(options, logger);

// ✅ Use ILogger<TSubclass>. It IS-A ILogger, so the protected ctor
//    KeePassReader(IKeePassOptions, ILogger) accepts it directly, and
//    log filtering can target FooKeePassReader specifically.
public sealed class FooKeePassReader(
    IFooKeePassOptions options,
    ILogger<FooKeePassReader> logger)
    : KeePassReader(options, logger);
```

## Quick reference

### Using statements

```csharp
using Microsoft.Extensions.Logging;       // ILogger<TCategoryName> on subclasses
using Roadbed.Secrets.KeePass;            // IKeePassOptions, KeePassSecret, KeePassReader
```

### Public surface

| Member                                                             | Purpose                                                                             |
| ------------------------------------------------------------------ | ----------------------------------------------------------------------------------- |
| `IKeePassOptions.MasterKey { get; }`                               | The KeePass master key, supplied by the host's secret store.                        |
| `IKeePassOptions.DatabasePath { get; }`                            | Absolute path to the `.kdbx` file.                                                  |
| `KeePassReader(IKeePassOptions, ILogger<KeePassReader>)`           | Public constructor used by DI when `KeePassReader` is resolved directly.            |
| `KeePassReader(IKeePassOptions, ILogger)` (protected)              | Constructor for subclasses passing their own typed `ILogger<TSubclass>`.            |
| `KeePassReader.Read(string entryTitle) → KeePassSecret`            | Title-keyed dictionary lookup. Case-sensitive ordinal.                              |
| `KeePassSecret.Title / UserName / Password / Url / Notes` (`init`) | Five standard KeePass string fields. Empty → `string.Empty`, never `null`.          |

### Construction-time exceptions

| Failure mode                                                           | Exception type             |
| ---------------------------------------------------------------------- | -------------------------- |
| `settings` constructor argument is `null`                              | `ArgumentNullException`    |
| `IKeePassOptions.MasterKey` is null/empty/whitespace                   | `InvalidOperationException`|
| `IKeePassOptions.DatabasePath` is null/empty/whitespace                | `InvalidOperationException`|
| `IKeePassOptions.DatabasePath` points at a file that doesn't exist     | `InvalidOperationException`|
| Wrong master key, malformed `.kdbx`, or other KeePassLib failure       | Whatever KeePassLib throws (propagates from the underlying call) |

### `Read(...)` exceptions

| Failure mode                                                | Exception type             |
| ----------------------------------------------------------- | -------------------------- |
| `entryTitle` is null/empty/whitespace                       | `ArgumentException`        |
| No entry in the cache matches `entryTitle`                  | `InvalidOperationException`|

### Multi-database recipe — at a glance

```
1. Marker subinterface          IFooKeePassOptions : IKeePassOptions
2. Marker options class         internal sealed class FooKeePassOptions : IFooKeePassOptions
3. Reader subclass              public sealed class FooKeePassReader(...) : KeePassReader(opts, logger);
4. Singleton registration       services.AddSingleton<IFooKeePassOptions>(...);
                                services.AddSingleton<FooKeePassReader>();
5. Consumer injection           ctor(FooKeePassReader fooReader, BarKeePassReader barReader)
```

Repeat steps 1–4 for each additional database (`Bar`, `Baz`, …). Step 5's injection list grows by one strongly-typed parameter per database.
