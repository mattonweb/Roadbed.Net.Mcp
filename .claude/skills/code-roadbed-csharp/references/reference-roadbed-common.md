# Roadbed.Common Reference

Foundation library for every Roadbed package. Provides level-checked logging base classes, auto-discovery module registration, and shared value-object types.

## Type catalog

| Type                             | Kind           | Namespace                    | Purpose                                                                                       |
| -------------------------------- | -------------- | ---------------------------- | --------------------------------------------------------------------------------------------- |
| `BaseClassWithLogging`           | Abstract class | `Roadbed`                    | Base class with level-checked logging convenience methods. Takes `ILogger`.                   |
| `BaseClassWithLoggingFactory<T>` | Abstract class | `Roadbed`                    | Extends `BaseClassWithLogging` with `ILoggerFactory` and typed `ILogger<T>`.                  |
| `IServiceCollectionInstaller`    | Interface      | `Roadbed`                    | One-per-assembly contract for module registration.                                            |
| `ServiceCollectionExtensions`    | Static class   | `Roadbed`                    | `InstallModulesInAppDomain()` and `InstallFromAssembly<T>()` extension methods.               |
| `ServiceLocator`                 | Static class   | `Roadbed`                    | Static service provider for cross-assembly resolution. `GetService<T>()`, `SetLocatorProvider()`. |
| `CommonBusinessKey`              | Partial record | `Roadbed.Common`             | Validated uppercase business key (regex-enforced).                                             |
| `CommonKeyValuePair<TKey,TValue>`| Sealed class   | `Roadbed.Common`             | Non-unique key/value pair with full equality and JSON support.                                 |
| `CommonEmbeddedResourceResponse` | Class          | `Roadbed.Common`             | Result wrapper for embedded resource read operations.                                          |
| `CommonEnvironmentType`          | Enum           | `Roadbed`                    | `Unknown`, `Local`, `Development`, `Qa`, `Staging`, `Production`.                              |
| `CommonAssemblyExtension`        | Static class   | `Roadbed`                    | `Assembly.ReadTextResource(name)`, `IsAssemblyLoaded(name)`.                                   |

## MUST

- **MUST** inherit from `BaseClassWithLogging` for repositories, services, domain objects, and most regular classes that need logging. Take `ILogger<T>` in the constructor and pass it to `base(logger)`.
- **MUST** inherit from `BaseClassWithLoggingFactory<T>` only when the class genuinely needs to create additional loggers from `ILoggerFactory` (e.g., scheduled jobs, HTTP client wrappers).
- **MUST** call the inherited convenience methods: `this.LogTrace`, `this.LogDebug`, `this.LogInformation`, `this.LogWarning`, `this.LogError`, `this.LogCritical`. They check `IsEnabled` before formatting the message string.
- **MUST** create exactly one `IServiceCollectionInstaller` per assembly. It registers all of that assembly's services.
- **MUST** end every `ConfigureServices` body with `ServiceLocator.SetLocatorProvider(services.BuildServiceProvider());` when other code (especially Roadbed.Crud's dual-constructor services) will need to resolve services from this assembly.
- **MUST** use `ArgumentNullException.ThrowIfNull(param)` for reference-type parameters and `ArgumentException.ThrowIfNullOrWhiteSpace(param)` for string parameters in every constructor and method.
- **MUST** write log messages with structured placeholders: `this.LogDebug("Processed {Count} foos in {DurationMs}ms", count, ms);`.

## MUST NOT

- **MUST NOT** call `this.Logger.LogDebug(...)` or `this._logger.LogDebug(...)`. They format the message string before checking the level.
- **MUST NOT** use string interpolation inside log messages: `this.LogDebug($"Processed {count} foos")`. The string is built even when Debug is disabled.
- **MUST NOT** inject `ILoggerFactory` when only `ILogger<T>` is needed.
- **MUST NOT** register services manually in `Program.cs` for code that has its own installer.
- **MUST NOT** call `BuildServiceProvider()` more than once per installer — it allocates a fresh provider each call.
- **MUST NOT** depend on `ServiceLocator` from constructors of classes that are themselves resolved through DI. Use it only when constructor injection is impossible (e.g., the public constructor of a dual-constructor service).
- **MUST NOT** create a `CommonBusinessKey` from lowercase input without `cleanAndFormat: true`. The factory throws `ArgumentException` for lowercase letters.

## Code patterns

### Inherit `BaseClassWithLogging` (default choice)

```csharp
namespace Foo.Sdk;

using Microsoft.Extensions.Logging;
using Roadbed;

public sealed class FooProcessor : BaseClassWithLogging
{
    private readonly IBarService _barService;

    public FooProcessor(
        IBarService barService,
        ILogger<FooProcessor> logger)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(barService);
        this._barService = barService;
    }

    public async Task ProcessAsync(CancellationToken cancellationToken = default)
    {
        this.LogDebug("Starting foo processing");

        var bars = await this._barService.ListAsync(cancellationToken);

        foreach (var bar in bars)
        {
            using (this.BeginScope("barId", bar.Id))
            {
                this.LogTrace("Processing bar");
                // business logic
            }
        }

        this.LogInformation("Processed {Count} bars", bars.Count);
    }
}
```

### Inherit `BaseClassWithLoggingFactory<T>` (only when factory or `ILogger<T>` property is needed)

```csharp
namespace Foo.Sdk;

using Microsoft.Extensions.Logging;
using Roadbed;

public sealed class FooHost : BaseClassWithLoggingFactory<FooHost>
{
    public FooHost(ILoggerFactory loggerFactory)
        : base(loggerFactory)
    {
    }

    public void Spawn(string childCategory)
    {
        // Create a logger for a different category from the factory:
        var childLogger = this.LoggerFactory.CreateLogger(childCategory);

        // Use the typed Logger property if you must call ILogger directly:
        this.Logger.LogInformation("Spawning {Category}", childCategory);

        // Prefer the convenience methods inherited from BaseClassWithLogging:
        this.LogInformation("Spawning {Category}", childCategory);
    }
}
```

### Module installer (one per assembly)

```csharp
namespace Foo.Sdk.Installers;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Roadbed;

public sealed class InstallFooSdk : IServiceCollectionInstaller
{
    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<IFooRepository, FooRepository>();
        services.AddScoped<IBarService, BarService>();

        ServiceLocator.SetLocatorProvider(services.BuildServiceProvider());
    }
}
```

### Application startup (the only DI line the host needs)

```csharp
var builder = WebApplication.CreateBuilder(args);

// Discovers every IServiceCollectionInstaller across all loaded assemblies,
// including framework installers (logging, Quartz, HTTP clients) and
// application-level installers (Foo, Bar).
builder.Services.InstallModulesInAppDomain(builder.Configuration);

var app = builder.Build();
app.Run();
```

### Resolve a service via `ServiceLocator` (only inside framework code, not application code)

```csharp
// Inside the public constructor of a dual-constructor service:
public FooService(ILogger<FooService> logger)
    : base(
        ServiceLocator.GetService<IFooRepository>(),  // resolves from current snapshot
        logger)
{
}
```

### Construct a `CommonBusinessKey`

```csharp
// Pre-validated uppercase value:
var key = CommonBusinessKey.FromString("FOO-SERVICE/BAR");

// Lowercase or mixed input — let the factory clean it:
var key = CommonBusinessKey.FromString("foo service", cleanAndFormat: true);
// Result: Key = "FOO_SERVICE"
```

### Read an embedded resource

```csharp
var response = this.GetType().Assembly.ReadTextResource("Foo.Sdk.Resources.FooSchema.sql");

if (response.IsReadSuccessful)
{
    string sql = response.Data;
    // ...
}
else
{
    this.LogWarning("Failed to read FooSchema.sql: {Error}", response.ErrorMessage);
}
```

### Convert a string to a `CommonEnvironmentType`

```csharp
CommonEnvironmentType env = "production".GetCommonEnvironment();
// env == CommonEnvironmentType.Production
```

## Common pitfalls

### Calling the logger directly bypasses level check

```csharp
// ❌ String is formatted even when Debug is disabled.
this._logger.LogDebug("Processing {Count} foos", foos.Count);

// ✅ this.LogDebug() checks IsEnabled first.
this.LogDebug("Processing {Count} foos", foos.Count);
```

### Missing `this.` prefix

```csharp
// ❌ Allowed by C#, forbidden by Roadbed convention.
public FooService(IFooRepository repository, ILogger<FooService> logger) : base(logger)
{
    _repository = repository;
}

// ✅
public FooService(IFooRepository repository, ILogger<FooService> logger) : base(logger)
{
    ArgumentNullException.ThrowIfNull(repository);
    this._repository = repository;
}
```

### Old null-validation pattern

```csharp
// ❌
this._repository = repository ?? throw new ArgumentNullException(nameof(repository));

// ✅
ArgumentNullException.ThrowIfNull(repository);
this._repository = repository;
```

### Inheriting the generic factory base class without needing it

```csharp
// ❌ Pulls in ILoggerFactory dependency for no reason.
public sealed class FooRepository : BaseClassWithLoggingFactory<FooRepository>
{
    public FooRepository(ILoggerFactory loggerFactory) : base(loggerFactory) { }
}

// ✅ Default to BaseClassWithLogging with ILogger<T>.
public sealed class FooRepository : BaseClassWithLogging
{
    public FooRepository(ILogger<FooRepository> logger) : base(logger) { }
}
```

### Forgetting `SetLocatorProvider` in an installer

```csharp
// ❌ Other modules' dual-constructor services can't resolve from this assembly.
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

### Creating a lowercase `CommonBusinessKey`

```csharp
// ❌ Throws ArgumentException at runtime.
var key = CommonBusinessKey.FromString("foo-service");

// ✅
var key = CommonBusinessKey.FromString("FOO-SERVICE");

// ✅
var key = CommonBusinessKey.FromString("foo service", cleanAndFormat: true);
```

## Quick reference

### Using statements

```csharp
using Roadbed;          // BaseClassWithLogging, IServiceCollectionInstaller, ServiceLocator, extensions
using Roadbed.Common;   // CommonBusinessKey, CommonKeyValuePair, value-object types
```

### Base-class decision

```
Need ILoggerFactory or ILogger<T> property?
    ├── Yes → BaseClassWithLoggingFactory<T> (constructor takes ILoggerFactory)
    └── No  → BaseClassWithLogging          (constructor takes ILogger<T>)
```

### Logging method signatures

| Method                                                | Parameters                                |
| ----------------------------------------------------- | ----------------------------------------- |
| `this.LogTrace(message [, params object[]])`          | `string [, args]`                         |
| `this.LogDebug(message [, params object[]])`          | `string [, args]`                         |
| `this.LogInformation(message [, params object[]])`    | `string [, args]`                         |
| `this.LogWarning(message [, params object[]])`        | `string [, args]`                         |
| `this.LogWarning(exception, message [, params])`      | `Exception, string [, args]`              |
| `this.LogError(message [, params object[]])`          | `string [, args]`                         |
| `this.LogError(exception, message [, params])`        | `Exception, string [, args]`              |
| `this.LogCritical(message [, params object[]])`       | `string [, args]`                         |
| `this.LogCritical(exception, message [, params])`     | `Exception, string [, args]`              |
| `this.BeginScope(key, value)`                         | `string, object` → `IDisposable?`         |
