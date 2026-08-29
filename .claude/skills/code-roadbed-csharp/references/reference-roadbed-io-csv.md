# Roadbed.IO.Csv Reference

Strongly-typed CSV file handler. `IoCsvFile<T>` maps CSV rows to and from POCO instances using consumer-supplied `ICsvEntityMapper<T>` implementations and the `CsvHelper` library.

Read [`reference-roadbed-io.md`](reference-roadbed-io.md) first for the `IoFile` save semantics this builds on.

## Type catalog (2 types)

| Type                  | Kind          | Namespace    | Purpose                                                                                  |
| --------------------- | ------------- | ------------ | ---------------------------------------------------------------------------------------- |
| `ICsvEntityMapper<T>` | Interface     | `Roadbed.IO` | Contract for mapping a single CSV row (via `CsvReader`) into a typed entity.             |
| `IoCsvFile<T>`        | Generic class | `Roadbed.IO` | Inherits `IoFile`. Holds the in-memory `DataRows` and provides factories for load/save.  |

Both types live in `Roadbed.IO` — the project's `RootNamespace` is `Roadbed.IO`, not `Roadbed.IO.Csv`. Consumers only need `using Roadbed.IO;`.

## MUST

- **MUST** implement `ICsvEntityMapper<T>` for every row type. The framework does not auto-map; mapping is explicit.
- **MUST** construct `IoCsvFile<T>` via the static factories — `FromFile`, `FromFileAsync`, `FromString`, `FromStringAsync`. The protected constructors are not for external use.
- **MUST** ensure path-based factories receive a `.csv`-extension path (case-insensitive). Other extensions throw `ArgumentException` from the path-backed constructor.
- **MUST** return `null` from `MapEntity` to skip a row. Returning a default-initialized entity adds a row of zeros/nulls.
- **MUST** repoint `FileInfo` before calling `Save`/`SaveAsync` if you want to write somewhere other than the original load path.

## MUST NOT

- **MUST NOT** call `IoCsvFile<T>` constructors directly — they are `protected`.
- **MUST NOT** call `Save` after `FromString`. There's no `FileInfo` set; `Save` throws. Either assign `FileInfo` first or use `ExportDataRowsAsContentString()` to get the CSV text.
- **MUST NOT** use a stateful `ICsvEntityMapper<T>` across threads. Mappers should be stateless; the framework calls `MapEntity` once per row sequentially during a single load.
- **MUST NOT** retain references to `DataRows` across a `LoadDataRows*` call. The collection is replaced every load.

## Code patterns

### Define a mapper

```csharp
namespace Foo.Sdk;

using CsvHelper;
using Roadbed.IO;

public sealed class FooCsvMapper : ICsvEntityMapper<Foo>
{
    public Foo? MapEntity(CsvReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return new Foo
        {
            Id = reader.GetField<int>("Id"),
            Name = reader.GetField<string>("Name"),
            Price = reader.GetField<decimal>("Price"),
        };
    }
}
```

### Skip a row by returning `null`

```csharp
public Foo? MapEntity(CsvReader reader)
{
    if (reader.GetField<string>("Status") == "ARCHIVED")
    {
        return null;   // skipped — does NOT appear in DataRows
    }

    return new Foo
    {
        Id = reader.GetField<int>("Id"),
        Name = reader.GetField<string>("Name"),
        Price = reader.GetField<decimal>("Price"),
    };
}
```

### Load from disk (synchronous)

```csharp
var file = IoCsvFile<Foo>.FromFile(@"C:\Data\foos.csv", new FooCsvMapper());

foreach (var foo in file.DataRows)
{
    Console.WriteLine($"{foo.Name}: ${foo.Price}");
}
```

### Load from disk (asynchronous)

```csharp
var file = await IoCsvFile<Foo>.FromFileAsync(@"C:\Data\foos.csv", new FooCsvMapper());
```

### Load from an in-memory string

```csharp
string csvContent = """
Id,Name,Price
1,Widget,9.99
2,Gadget,19.99
""";

var file = IoCsvFile<Foo>.FromString(csvContent, new FooCsvMapper());
// file.DataRows now has two Foo instances
```

### Export to a CSV string (no file write)

```csharp
var file = IoCsvFile<Foo>.FromFile(@"C:\Data\foos.csv", new FooCsvMapper());

// Mutate file.DataRows ...
file.DataRows.Add(new Foo { Id = 3, Name = "Sprocket", Price = 4.99m });

string csv = file.ExportDataRowsAsContentString();
// Send via HTTP, log, etc., without touching disk.
```

### Save back to disk

```csharp
string savedPath = file.Save();
// Or: await file.SaveAsync();
```

### Save with a custom `CsvConfiguration`

```csharp
var config = new CsvConfiguration(CultureInfo.InvariantCulture)
{
    HasHeaderRecord = true,
    Delimiter = ";",                  // semicolon-delimited
    Encoding = Encoding.UTF8,
};

string savedPath = file.Save(config);
```

### Repoint before saving

```csharp
var file = await IoCsvFile<Foo>.FromFileAsync(@"C:\Data\source.csv", new FooCsvMapper());
// Mutate file.DataRows ...

file.FileInfo = new IoFileInfo(@"C:\Data\output.csv");
await file.SaveAsync();
// "C:\Data\source.csv" is unchanged; "C:\Data\output.csv" is written.
```

## Common pitfalls

### Constructing directly

```csharp
// ❌ Constructors are protected.
var file = new IoCsvFile<Foo>(new IoFileInfo(@"C:\Data\foos.csv"), new FooCsvMapper());

// ✅
var file = IoCsvFile<Foo>.FromFile(@"C:\Data\foos.csv", new FooCsvMapper());
```

### Loading a non-`.csv` path through `FromFile`

```csharp
// ❌ Throws ArgumentException — extension must be ".csv".
var file = IoCsvFile<Foo>.FromFile(@"C:\Data\foos.txt", new FooCsvMapper());

// ✅ Read the file yourself, then use FromString.
string content = File.ReadAllText(@"C:\Data\foos.txt");
var file = IoCsvFile<Foo>.FromString(content, new FooCsvMapper());
```

### Returning a default entity instead of `null`

```csharp
// ❌ Returns a Foo with default values — pollutes DataRows.
public Foo? MapEntity(CsvReader reader)
{
    if (reader.GetField<string>("Status") == "ARCHIVED")
    {
        return new Foo();
    }
    return new Foo { /* ... */ };
}

// ✅ Return null to skip.
public Foo? MapEntity(CsvReader reader)
{
    if (reader.GetField<string>("Status") == "ARCHIVED")
    {
        return null;
    }
    return new Foo { /* ... */ };
}
```

### `Save` after `FromString` without setting `FileInfo`

```csharp
// ❌ FromString does not assign FileInfo; Save throws.
var file = IoCsvFile<Foo>.FromString(csvContent, mapper);
file.Save();   // ArgumentNullException

// ✅ Assign FileInfo first, or use ExportDataRowsAsContentString.
var file = IoCsvFile<Foo>.FromString(csvContent, mapper);
file.FileInfo = new IoFileInfo(@"C:\Data\out.csv");
file.Save();

// or
var file = IoCsvFile<Foo>.FromString(csvContent, mapper);
string csvText = file.ExportDataRowsAsContentString();
```

### Save accidentally overwrites the source

```csharp
// ❌ Save uses the FileInfo from FromFile — overwrites source.csv.
var file = IoCsvFile<Foo>.FromFile(@"C:\Data\source.csv", mapper);
// ... mutate DataRows ...
file.Save();

// ✅ Repoint before saving.
file.FileInfo = new IoFileInfo(@"C:\Data\output.csv");
file.Save();
```

### Stateful mapper

```csharp
// ❌ Race condition under concurrent loads.
public sealed class FooCsvMapper : ICsvEntityMapper<Foo>
{
    private int _rowsSeen;

    public Foo? MapEntity(CsvReader reader)
    {
        this._rowsSeen++;
        return /* ... */;
    }
}

// ✅ Stateless mapper.
public sealed class FooCsvMapper : ICsvEntityMapper<Foo>
{
    public Foo? MapEntity(CsvReader reader) => /* pure function of reader */;
}
```

## Quick reference

### Using statements

```csharp
using CsvHelper;                 // CsvReader (in mapper implementations)
using CsvHelper.Configuration;   // CsvConfiguration (only when overriding defaults)
using Roadbed.IO;                // IoCsvFile<T>, ICsvEntityMapper<T>, IoFile, IoFileInfo
```

### Factory selection

| Factory                                | When                                                          |
| -------------------------------------- | ------------------------------------------------------------- |
| `FromFile(path, mapper)`               | Load from disk synchronously                                  |
| `FromFileAsync(path, mapper)`          | Load from disk asynchronously                                 |
| `FromString(content, mapper)`          | Load from an in-memory string synchronously                   |
| `FromStringAsync(content, mapper)`     | Load from an in-memory string asynchronously                  |

### Default `CsvConfiguration` (used when none is supplied)

```csharp
new CsvConfiguration(CultureInfo.InvariantCulture)
{
    HasHeaderRecord = true,
    Delimiter = ",",
    Encoding = Encoding.UTF8,
};
```

### Save methods

| Method                                  | Effect                                                                          |
| --------------------------------------- | ------------------------------------------------------------------------------- |
| `Save()`                                | Default config, writes to `FileInfo.FullPath`, returns the path                 |
| `Save(CsvConfiguration)`                | Custom config, writes, returns the path                                         |
| `SaveAsync()` / `SaveAsync(config)`     | Async equivalents                                                               |
| `ExportDataRowsAsContentString()`       | Returns CSV as a string without writing a file                                  |
| `ExportDataRowsAsContentString(config)` | Same with custom config                                                         |
