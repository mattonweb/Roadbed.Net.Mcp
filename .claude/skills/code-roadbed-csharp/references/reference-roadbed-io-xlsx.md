# Roadbed.IO.Xlsx Reference

Streaming reader for large Excel workbooks (`.xlsx`/`.xlsb`/`.xls`). `IoXlsxFile<T>`
maps worksheet rows to POCOs using a consumer-supplied `IXlsxEntityMapper<T>` and
the **Sylvan.Data.Excel** library (MIT, forward-only `DbDataReader`).

Read [`reference-roadbed-io.md`](reference-roadbed-io.md) first for the `IoFile`
base. Unlike [`reference-roadbed-io-csv.md`](reference-roadbed-io-csv.md), this
library is **streaming-first**: it never loads the whole sheet into memory, so
there is no `DataRows` collection — you enumerate rows and feed a sink as they
arrive.

## Type catalog (3 types)

| Type                   | Kind          | Namespace    | Purpose                                                                                          |
| ---------------------- | ------------- | ------------ | ----------------------------------------------------------------------------------------------- |
| `IXlsxEntityMapper<T>` | Interface     | `Roadbed.IO` | Contract for mapping one positioned worksheet row (via `DbDataReader`) into a typed entity.      |
| `IoXlsxReadOptions`    | Class         | `Roadbed.IO` | Sheet selection (name/index) and header/banner-row handling.                                     |
| `IoXlsxFile<T>`        | Generic class | `Roadbed.IO` | Inherits `IoFile`. Streams rows via `ReadRowsAsync` / `ReadBatchesAsync`.                        |

All types live in `Roadbed.IO` — the project's `RootNamespace` is `Roadbed.IO`,
not `Roadbed.IO.Xlsx`. Consumers only need `using Roadbed.IO;`.

## MUST

- **MUST** implement `IXlsxEntityMapper<T>` for every row type. Mapping is explicit.
- **MUST** construct `IoXlsxFile<T>` via `FromFile`. The constructor is private.
- **MUST** consume rows by enumerating `ReadRowsAsync` (or `ReadBatchesAsync`) and
  feeding a sink — there is no buffered `DataRows`. Memory stays bounded to roughly
  the shared-strings table plus the current row/batch.
- **MUST** read cells defensively in the mapper: as **strings** with `TryParse`,
  guarding nulls with `reader.IsDBNull(ordinal)`. The reader exposes every column
  as a nullable string (`GetString` works for numeric cells too), which is the
  right fit for dirty government data.
- **MUST** put the header on the sheet's **first row** for headered reads. The
  header is bound when the worksheet opens, so it cannot follow banner rows. For a
  file whose real header is preceded by banner rows, set `HasHeaders = false`, set
  `SkipLeadingRows`, and map by ordinal. Passing `HasHeaders = true` together with
  `SkipLeadingRows > 0` throws `ArgumentException` from `FromFile`.
- **MUST** download remote workbooks to a local file first (see the recipe below).
  An `.xlsx` is a ZIP and needs a seekable stream; a temp file keeps memory bounded.

## MUST NOT

- **MUST NOT** call the `IoXlsxFile<T>` constructor directly — it is `private`; use `FromFile`.
- **MUST NOT** expect a `DataRows`-style buffered collection — this library does not
  materialize the sheet. Re-enumerate `ReadRowsAsync` if you truly need a second pass.
- **MUST NOT** drop the `[EnumeratorCancellation]`-flowed token: pass your
  `CancellationToken` to `ReadRowsAsync`/`ReadBatchesAsync` so cancellation works.
- **MUST NOT** read numeric-looking codes (ZIP, FIPS) with numeric getters — see the
  leading-zero pitfall below.
- **MUST NOT** reference the unrelated `ExcelDataReader` NuGet package; this library
  uses `Sylvan.Data.Excel` (whose reader type happens to share the name
  `ExcelDataReader`). Sylvan is confined to `Roadbed.IO.Xlsx`.

## Code patterns

### Define a mapper (headered, name-based)

```csharp
using System.Data.Common;
using Roadbed.IO;

public sealed class PlaceRowMapper : IXlsxEntityMapper<PlaceRow>
{
    public PlaceRow? MapEntity(DbDataReader reader)
    {
        string name = Read(reader, reader.GetOrdinal("Name"));
        if (string.IsNullOrWhiteSpace(name))
        {
            return null; // skip blank rows
        }

        return new PlaceRow
        {
            // ZIP/FIPS: read as string and re-pad — see the leading-zero pitfall.
            Fips = Read(reader, reader.GetOrdinal("Fips")).PadLeft(5, '0'),
            Name = name,
            Population = int.TryParse(Read(reader, reader.GetOrdinal("Population")), out int p) ? p : 0,
        };
    }

    private static string Read(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);
}
```

### End-to-end: download → stream → bulk insert (the canonical large-file ingest)

```csharp
// 1. Stream the workbook to a local file (bounded memory; see Roadbed.Net).
string localPath = Path.Combine(scratchDir, "places.xlsx");
NetHttpResponse<NetHttpDownloadResult> dl = await httpClient.DownloadFileAsync(
    new NetHttpDownloadRequest { HttpEndPoint = new Uri(sourceUrl), DestinationPath = localPath },
    cancellationToken);

if (!dl.IsSuccessStatusCode) { /* handle + return */ }

// Record provenance on the activity row before the file is deleted.
await activities.UpdateAsync(new LoggingActivityUpdateRequest
{
    ActivityId = scope.ActivityId,
    CreatedOn = scope.CreatedOn,
    ParametersJson = JsonSerializer.Serialize(new { sourceUrl, sha256 = dl.Data.ContentSha256 }, RoadbedJson.Options),
}, cancellationToken);

// 2. Stream rows in batches straight into the bronze bulk insert.
var xlsx = IoXlsxFile<PlaceRow>.FromFile(localPath, new PlaceRowMapper(),
    new IoXlsxReadOptions { SheetName = "Places" });

await foreach (IReadOnlyList<PlaceRow> batch in xlsx.ReadBatchesAsync(5000, cancellationToken))
{
    await bronze.BulkInsertAsync(activityId, batch, cancellationToken);
}

// 3. Consumer owns the temp file lifecycle.
File.Delete(localPath);
```

### Select a worksheet

```csharp
new IoXlsxReadOptions { SheetName = "Places" };  // by name (throws if not found)
new IoXlsxReadOptions { SheetIndex = 2 };        // by zero-based position
new IoXlsxReadOptions();                          // first sheet, headered
```

### Banner rows above the header (ordinal mapping)

```csharp
// Two banner lines + a label row, then data: skip 3, map by ordinal.
var xlsx = IoXlsxFile<PlaceRow>.FromFile(path, new OrdinalPlaceMapper(),
    new IoXlsxReadOptions { HasHeaders = false, SkipLeadingRows = 3 });
```

## Common pitfalls

### Numeric cells drop leading zeros (ZIP/FIPS)

Excel stores `01001` as the number `1001`, so it round-trips as `"1001"` (or `1`).
Read such columns **as strings and re-pad** — never as an int:

```csharp
// ❌ loses the leading zero
int fips = reader.GetInt32(reader.GetOrdinal("Fips"));

// ✅ string + re-pad
string fips = Read(reader, reader.GetOrdinal("Fips")).PadLeft(5, '0');
```

This hits the very first consumer file. (When the source stores the code as *text*,
the zero is preserved and re-padding is a no-op — so re-padding is always safe.)

### Mapper uses Sylvan's typed getters on dirty data

Government cells mix `"1"` vs `"1.0"`, stray text, and empty-string-as-null. Typed
getters throw on the first bad cell. Read strings and `TryParse`:

```csharp
// ❌ throws on a non-numeric or empty cell
decimal amount = reader.GetDecimal(reader.GetOrdinal("Amount"));

// ✅ forgiving
decimal amount = decimal.TryParse(Read(reader, reader.GetOrdinal("Amount")), out var a) ? a : 0m;
```

### Expecting a buffered `DataRows` collection

There isn't one — the read is forward-only and streaming. To make two passes,
call `ReadRowsAsync` again (it reopens the file).

### Headered read with banner rows

`HasHeaders = true` + `SkipLeadingRows > 0` throws — the header must be on row 1.
Use `HasHeaders = false` + `SkipLeadingRows` + ordinal mapping instead.

### Forgetting the trailing-batch flush (when hand-rolling batching)

Prefer `ReadBatchesAsync(batchSize)`; it yields the final partial batch for you.
A manual `ReadRowsAsync` buffer loop must flush the remainder after the loop.

## Quick reference

### Using statements

```csharp
using Roadbed.IO;              // IoXlsxFile<T>, IXlsxEntityMapper<T>, IoXlsxReadOptions
using System.Data.Common;     // DbDataReader (mapper parameter)
```

### `IoXlsxReadOptions` defaults

| Property          | Default | Meaning                                                        |
| ----------------- | ------- | ------------------------------------------------------------- |
| `SheetName`       | `null`  | Use `SheetIndex` instead.                                     |
| `SheetIndex`      | `0`     | First worksheet.                                              |
| `SkipLeadingRows` | `0`     | Banner rows to discard (only valid when `HasHeaders = false`).|
| `HasHeaders`      | `true`  | First row is the header; enables `GetOrdinal("Name")`.        |

### Read surface

| Need                              | Use                                                  |
| --------------------------------- | ---------------------------------------------------- |
| Stream mapped rows                | `await foreach (var r in file.ReadRowsAsync(ct))`    |
| Stream pre-batched rows           | `await foreach (var b in file.ReadBatchesAsync(n, ct))` |
| Skip a row                        | return `null` from `MapEntity`                       |
