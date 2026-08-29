# Roadbed.IO Reference

A thin, testable abstraction over file-system save operations. Provides an abstract `IoFile` base class and an `IoFileInfo` DTO that wraps `System.IO.FileInfo` without exposing the full surface.

Roadbed.IO is the substrate that typed-file libraries (e.g., `Roadbed.IO.Csv`) build on top of. Use it directly when writing your own typed file format handler.

## Type catalog (2 types)

| Type         | Kind           | Purpose                                                                                                   |
| ------------ | -------------- | --------------------------------------------------------------------------------------------------------- |
| `IoFile`     | Abstract class | Base class for typed file handlers. Provides `Save(string)`, `SaveAsync(string)`, and `ValidateFileInfo`. |
| `IoFileInfo` | Class          | DTO wrapping `System.IO.FileInfo`. Exposes `FullPath` and `Extension`.                                    |

## MUST

- **MUST** inherit from `IoFile` when writing a new typed file handler. Do not re-implement save semantics.
- **MUST** call `IoFile.ValidateFileInfo(this.FileInfo)` at the top of every path-based method on a subclass. The base `Save`/`SaveAsync` already do this — your subclass methods must too.
- **MUST** set `IoFileInfo.FullPath` to populate the underlying `System.IO.FileInfo`. The setter rebuilds it.
- **MUST** validate constructor inputs with `ArgumentNullException.ThrowIfNull` and `ArgumentException.ThrowIfNullOrWhiteSpace`.
- **MUST** treat `Save`/`SaveAsync` returning `string.Empty` as a deliberate no-op for blank content — not as a failure.

## MUST NOT

- **MUST NOT** assign `IoFileInfo.FileInfo` directly. Its setter is `internal`. Set `FullPath` instead.
- **MUST NOT** call `Save` without first setting `FileInfo` (or constructing `IoFile` with an `IoFileInfo`). It throws `ArgumentNullException`.
- **MUST NOT** assume `Save` appends. It overwrites the file each call.
- **MUST NOT** read `IoFileInfo.Extension` before `FullPath` is set — it returns `null` and your enforcement check will pass when it shouldn't.

## Code patterns

### Use `IoFile.Save` directly (rare — typically you inherit)

```csharp
namespace Foo.Sdk;

using Roadbed.IO;

public sealed class FooFile : IoFile
{
    public FooFile(IoFileInfo fileInfo)
        : base(fileInfo)
    {
    }
}

var file = new FooFile(new IoFileInfo(@"C:\Data\foo.txt"));
string savedPath = file.Save("hello world");
// savedPath = "C:\Data\foo.txt"
```

### Subclass blueprint (mirrors how `Roadbed.IO.Csv` is built)

```csharp
namespace Foo.Sdk;

using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Roadbed.IO;

public class IoFooFile<T>
    : IoFile
{
    #region Protected Constructors

    protected IoFooFile(IFooEntityMapper<T> dataMapper)
    {
        ArgumentNullException.ThrowIfNull(dataMapper);

        this.DataRows = new List<T>();
        this.DataMapper = dataMapper;
    }

    protected IoFooFile(IoFileInfo fileInfo, IFooEntityMapper<T> dataMapper)
        : base(fileInfo)
    {
        ArgumentNullException.ThrowIfNull(dataMapper);

        ValidateFileInfo(fileInfo);

        if (!string.Equals(fileInfo?.Extension, ".foo", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("File extension isn't '.foo'.", nameof(fileInfo));
        }

        this.DataRows = new List<T>();
        this.DataMapper = dataMapper;
    }

    #endregion Protected Constructors

    #region Public Properties

    public IFooEntityMapper<T>? DataMapper { get; set; }
    public IList<T> DataRows { get; set; }

    #endregion Public Properties

    #region Public Methods

    /// <summary>Static factory for path-backed instances.</summary>
    public static IoFooFile<T> FromFile(string path, IFooEntityMapper<T> dataMapper)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(dataMapper);

        var file = new IoFooFile<T>(new IoFileInfo(path), dataMapper);
        file.LoadDataRowsFromFile();
        return file;
    }

    public string Save() => this.Save(this.SerializeDataRows());
    public Task<string> SaveAsync() => this.SaveAsync(this.SerializeDataRows());

    #endregion Public Methods

    #region Private Methods

    private void LoadDataRowsFromFile()
    {
        ValidateFileInfo(this.FileInfo!);
        ArgumentNullException.ThrowIfNull(this.DataMapper);

        this.DataRows = new List<T>();

        foreach (var line in File.ReadAllLines(this.FileInfo!.FullPath!))
        {
            var entity = this.DataMapper!.MapEntity(line);
            if (entity is not null)
            {
                this.DataRows.Add(entity);
            }
        }
    }

    private string SerializeDataRows()
    {
        // ... convert DataRows back into the on-disk wire format ...
        return string.Empty;
    }

    #endregion Private Methods
}
```

### Construct an `IoFileInfo`

```csharp
var info = new IoFileInfo(@"C:\Data\foo.txt");
// info.FullPath  → "C:\Data\foo.txt"
// info.Extension → ".txt"
```

### Repoint a file before saving

```csharp
var file = MyFooFile.FromFile(@"C:\Data\source.foo", mapper);
// ... mutate file.DataRows ...
file.FileInfo = new IoFileInfo(@"C:\Data\output.foo");
file.Save();
// "C:\Data\source.foo" is unchanged; output written to "C:\Data\output.foo".
```

## Common pitfalls

### Calling `Save` without setting `FileInfo`

```csharp
// ❌ Throws ArgumentNullException because FileInfo is null.
var file = new MyTypedFile();
file.Save("content");

// ✅ Assign FileInfo first.
var file = new MyTypedFile { FileInfo = new IoFileInfo(@"C:\Data\foo.bar") };
file.Save("content");
```

### Trying to set `IoFileInfo.FileInfo` directly

```csharp
// ❌ Won't compile from outside the assembly — FileInfo has internal set.
var info = new IoFileInfo();
info.FileInfo = new FileInfo(@"C:\Data\foo.bar");

// ✅ Set FullPath; the wrapped FileInfo rebuilds.
var info = new IoFileInfo();
info.FullPath = @"C:\Data\foo.bar";
```

### Treating `string.Empty` from `Save` as failure

```csharp
// ❌ Save returns string.Empty when content is blank — not when it failed.
var path = file.Save(content);
if (path == string.Empty)
{
    throw new InvalidOperationException("Save failed!");  // misleading
}

// ✅
var path = file.Save(content);
if (string.IsNullOrEmpty(path))
{
    // No file written because content was blank — that's expected.
    return;
}
```

### Skipping `ValidateFileInfo` in a subclass method

```csharp
// ❌ Subclass method assumes FileInfo is valid.
public void Reload()
{
    foreach (var line in File.ReadAllLines(this.FileInfo!.FullPath!))   // NullRef if FileInfo is null
    {
    }
}

// ✅
public void Reload()
{
    ValidateFileInfo(this.FileInfo!);
    foreach (var line in File.ReadAllLines(this.FileInfo!.FullPath!))
    {
    }
}
```

### Expecting `Save` to append

```csharp
// ❌ Save overwrites — "first batch" is gone.
file.Save("first batch");
file.Save("second batch");

// ✅ Accumulate, then save once.
var sb = new StringBuilder();
sb.AppendLine("first batch");
sb.AppendLine("second batch");
file.Save(sb.ToString());
```

## Quick reference

### Using statements

```csharp
using Roadbed.IO;   // IoFile, IoFileInfo
```

### `IoFile` constructors

| Constructor             | When to use                                                                                       |
| ----------------------- | ------------------------------------------------------------------------------------------------- |
| `IoFile()`              | In-memory only — no path yet. Subclasses that load from a string use this path.                    |
| `IoFile(IoFileInfo)`    | Path-backed. Subclasses that load from disk use this path.                                         |

### `IoFileInfo` properties

| Property      | Get                                                                          | Set                                                                                  |
| ------------- | ---------------------------------------------------------------------------- | ------------------------------------------------------------------------------------ |
| `Extension`   | `FileInfo.Extension` (e.g., `.csv`); `null` if `FullPath` not set            | read-only                                                                            |
| `FileInfo`    | wrapped `System.IO.FileInfo`                                                 | `internal set` — only writable from within the assembly                              |
| `FullPath`    | `FileInfo.FullName`; `null` if not set                                       | non-blank → constructs new `FileInfo`; blank/null → clears `FileInfo` to `null`      |

### Save semantics

```
1. ValidateFileInfo(this.FileInfo)     → throws if null or Extension blank
2. fileContent null/whitespace?         → return string.Empty (no-op)
3. Open StreamWriter at FullPath        → overwrites existing file
4. Write fileContent
5. Return FullPath
```
