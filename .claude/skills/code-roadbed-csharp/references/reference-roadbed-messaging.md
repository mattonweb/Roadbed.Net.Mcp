# Roadbed.Messaging Reference

Standardized, strongly-typed message envelopes for pub/sub messaging systems (AWS SNS / SQS, Azure Service Bus, RabbitMQ, etc.). Transport-agnostic — produces and consumes JSON envelopes that any broker can carry.

Every envelope shares the same shape: a UUIDv7 identifier (36-char canonical hex string from `Guid.CreateVersion7()`), publisher metadata, type codename, source and envelope timestamps, and a typed payload `T`.

## Type catalog (4 types)

| Type                          | Kind           | Purpose                                                                             |
| ----------------------------- | -------------- | ----------------------------------------------------------------------------------- |
| `BaseMessagingMessage<T>`     | Abstract class | Shared envelope shape. Concrete request and response classes inherit it.            |
| `MessagingMessageRequest<T>`  | Class          | Concrete envelope for messages sent **to** a system (commands, events).             |
| `MessagingMessageResponse<T>` | Class          | Concrete envelope for **replies**. Adds `OriginalRequestIdentifier` for correlation. |
| `MessagingPublisher`          | Class          | Identifies the publishing process: per-instance UUIDv7 + service name (`CommonBusinessKey`). |

## MUST

- **MUST** use `System.Text.Json` (`[JsonPropertyName(...)]`) for serialization. The envelope properties are decorated with STJ attributes; `Newtonsoft.Json` ignores them and produces the wrong wire format.
- **MUST** pass `Roadbed.RoadbedJson.Options` to every `JsonSerializer.Serialize`/`Deserialize` call. Roadbed's envelope-property metadata is keyed by that single options instance; allocating fresh options per call both breaks STJ's reflection cache and risks losing the null-omission / case-insensitive / lenient-number behavior the framework was tuned for.
- **MUST** set `MessageTypeCodename` on every message you publish. Use the constructor overload that takes `(publisher, typeCodename)` or `(publisher, typeCodename, identifier, data)`. Routing and broker-side filtering depend on it.
- **MUST** construct **one** `MessagingPublisher` per process and reuse it for every message published from that process. The `Identifier` is the per-instance UUIDv7; treat it as instance identity.
- **MUST** set `OriginalRequestIdentifier` on response messages. This is the only way consumers correlate replies with the originating request.
- **MUST** validate `Data` is not null on the consumer side before processing. `Data` is nullable to permit envelope-only messages.
- **MUST** use the parameterless constructor on `MessagingMessageRequest<T>` and `MessagingMessageResponse<T>` only via `JsonSerializer.Deserialize<...>(..., RoadbedJson.Options)` — not by hand. The publishing code path uses the parameterized constructors. `Identifier` and `CreatedOn` are marked `[JsonInclude]` so STJ binds their internal setters during deserialization; without that the parameterless constructor's fresh UUIDv7 would silently survive the round-trip.

## MUST NOT

- **MUST NOT** use `Newtonsoft.Json` for envelope serialization. The wire format breaks — `[JsonProperty]` is silently ignored and properties bind under C# names instead of `snake_case`.
- **MUST NOT** allocate a `JsonSerializerOptions` per call when serializing envelopes. Reuse `Roadbed.RoadbedJson.Options` — STJ keys its reflection-derived metadata cache by options instance, so per-call options are the #1 STJ perf footgun.
- **MUST NOT** construct a `MessagingPublisher` per message. Every message would get a different `Identifier` UUIDv7, defeating instance correlation.
- **MUST NOT** assign `Identifier` directly on a message — it has `internal set` on `BaseMessagingMessage<T>`. Use the constructor overload that accepts an identifier.
- **MUST NOT** skip the null-check on `Data` after deserialization. A consumer that processes `request.Data.SomeField` will throw `NullReferenceException` for envelope-only messages.
- **MUST NOT** mix casing in `MessageTypeCodename`. `Foo.Created` and `foo.created` are different keys to a broker filter.
- **MUST NOT** use `Identifier` from `MessagingMessageResponse` to correlate the reply with the originating request — that's `OriginalRequestIdentifier`. The `Identifier` is the response's own UUIDv7.

## Code patterns

### Construct one publisher per process

```csharp
namespace Foo.Sdk;

using Roadbed;
using Roadbed.Messaging;

public static class FooMessagingHost
{
    public static MessagingPublisher CreatePublisher()
    {
        return new MessagingPublisher(
            name: new CommonBusinessKey("foo-service", "FooService"),
            identifier: Environment.MachineName);
    }
}
```

Register the publisher as a singleton in DI so every component shares the same instance.

### Define payload POCOs with `[JsonPropertyName]`

```csharp
namespace Foo.Sdk;

using System.Text.Json.Serialization;

public sealed class FooCreatedPayload
{
    [JsonPropertyName("foo_id")]
    public long FooId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

public sealed class FooProcessedPayload
{
    [JsonPropertyName("foo_id")]
    public long FooId { get; set; }

    [JsonPropertyName("result")]
    public string? Result { get; set; }
}
```

### Publish a request

```csharp
public sealed class FooPublisher
{
    private readonly MessagingPublisher _publisher;
    private readonly IFooBroker _broker;

    public FooPublisher(MessagingPublisher publisher, IFooBroker broker)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(broker);

        this._publisher = publisher;
        this._broker = broker;
    }

    public async Task PublishFooCreatedAsync(
        FooCreatedPayload payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var message = new MessagingMessageRequest<FooCreatedPayload>(
            this._publisher,
            "foo.created")
        {
            Data = payload,
        };

        string json = JsonSerializer.Serialize(message, RoadbedJson.Options);

        await this._broker.PublishAsync(json, cancellationToken);
    }
}
```

### Consume a request and reply

```csharp
public sealed class BarConsumer
{
    private readonly MessagingPublisher _publisher;
    private readonly IBarBroker _broker;

    public BarConsumer(MessagingPublisher publisher, IBarBroker broker)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(broker);

        this._publisher = publisher;
        this._broker = broker;
    }

    public async Task HandleAsync(string body, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(body);

        var request = JsonSerializer.Deserialize<MessagingMessageRequest<FooCreatedPayload>>(
            body,
            RoadbedJson.Options);

        // Always validate Data is not null before processing.
        if (request?.Data is null)
        {
            return;
        }

        // ... do work ...
        var resultPayload = new FooProcessedPayload
        {
            FooId = request.Data.FooId,
            Result = "success",
        };

        var response = new MessagingMessageResponse<FooProcessedPayload>(
            this._publisher,
            "bar.foo_processed")
        {
            Data = resultPayload,
            OriginalRequestIdentifier = request.Identifier,
        };

        string responseJson = JsonSerializer.Serialize(response, RoadbedJson.Options);
        await this._broker.PublishAsync(responseJson, cancellationToken);
    }
}
```

### Round-trip is supported

Both `MessagingMessageRequest<T>` and `MessagingMessageResponse<T>` have parameterless constructors specifically so `JsonSerializer.Deserialize<...>(json, RoadbedJson.Options)` can rehydrate them. You do not need any custom converter. `Identifier` and `CreatedOn` carry `[JsonInclude]` so STJ binds their internal setters during deserialization.

## Common pitfalls

### Using `Newtonsoft.Json`

```csharp
// ❌ Ignores [JsonPropertyName]; produces "Identifier" instead of "message_identifier".
using Newtonsoft.Json;
string json = JsonConvert.SerializeObject(message);

// ❌ STJ without the shared options misses the null-omission / case-insensitive /
//    lenient-number behavior Roadbed callers expect, and allocates fresh metadata
//    on every call.
using System.Text.Json;
string json = JsonSerializer.Serialize(message);

// ✅
using System.Text.Json;
using Roadbed;
string json = JsonSerializer.Serialize(message, RoadbedJson.Options);
```

### Missing `OriginalRequestIdentifier` on a response

```csharp
// ❌ Consumer can't tell which request this reply belongs to.
var response = new MessagingMessageResponse<FooProcessedPayload>(this._publisher, "bar.foo_processed")
{
    Data = resultPayload,
};

// ✅
var response = new MessagingMessageResponse<FooProcessedPayload>(this._publisher, "bar.foo_processed")
{
    Data = resultPayload,
    OriginalRequestIdentifier = request.Identifier,
};
```

### Missing `MessageTypeCodename`

```csharp
// ❌ No routing key; broker filters and dead-letter triage are blind.
var message = new MessagingMessageRequest<FooCreatedPayload>(this._publisher)
{
    Data = payload,
};

// ✅
var message = new MessagingMessageRequest<FooCreatedPayload>(this._publisher, "foo.created")
{
    Data = payload,
};
```

### Skipping the null-check on `Data`

```csharp
// ❌ NullReferenceException for envelope-only messages.
var request = JsonSerializer.Deserialize<MessagingMessageRequest<FooCreatedPayload>>(
    body,
    RoadbedJson.Options);
var fooId = request.Data.FooId;

// ✅
var request = JsonSerializer.Deserialize<MessagingMessageRequest<FooCreatedPayload>>(
    body,
    RoadbedJson.Options);
if (request?.Data is null)
{
    return;
}
var fooId = request.Data.FooId;
```

### Building a new publisher per message

```csharp
// ❌ Every message gets a different publisher Identifier — instance correlation is impossible.
public async Task PublishAsync(FooCreatedPayload payload, CancellationToken ct)
{
    var publisher = new MessagingPublisher(new CommonBusinessKey("foo-service", "FooService"));
    var message = new MessagingMessageRequest<FooCreatedPayload>(publisher, "foo.created")
    {
        Data = payload,
    };
    // ...
}

// ✅ Inject one publisher constructed at startup.
private readonly MessagingPublisher _publisher;

public FooPublisher(MessagingPublisher publisher)
{
    ArgumentNullException.ThrowIfNull(publisher);
    this._publisher = publisher;
}

public async Task PublishAsync(FooCreatedPayload payload, CancellationToken ct)
{
    var message = new MessagingMessageRequest<FooCreatedPayload>(this._publisher, "foo.created")
    {
        Data = payload,
    };
    // ...
}
```

### Mixing casing in `MessageTypeCodename`

```csharp
// ❌ Broker prefix filters won't match consistently.
"Foo.Created"
"foo.created"
"FOO_CREATED"

// ✅ Pick one style and stay there.
"foo.created"
"foo.updated"
"foo.deleted"
```

## Quick reference

### Using statements

```csharp
using System.Text.Json;                // JsonSerializer.Serialize / Deserialize
using System.Text.Json.Serialization;  // [JsonPropertyName] on payload POCOs
using Roadbed;                         // CommonBusinessKey, RoadbedJson.Options
using Roadbed.Messaging;               // BaseMessagingMessage, request/response, publisher
```

### Wire-format property names

| C# property                            | JSON name                     |
| -------------------------------------- | ----------------------------- |
| `Identifier`                           | `message_identifier`          |
| `MessageTypeCodename`                  | `message_type`                |
| `Publisher`                            | `publisher`                   |
| `Data`                                 | `data`                        |
| `CreatedOn`                            | `message_create_on`           |
| `SourceCreatedOn`                      | `source_create_on`            |
| `OriginalRequestIdentifier` (response) | `original_request_identifier` |
| `MessagingPublisher.Identifier`        | `publisher_identifier`        |
| `MessagingPublisher.Name`              | `publisher_name`              |

### Type-codename conventions

| Pattern                  | Examples                                       |
| ------------------------ | ---------------------------------------------- |
| `entity.action`          | `foo.created`, `foo.updated`, `foo.deleted`    |
| `entity.action.outcome`  | `bar.processed.success`, `bar.processed.failure` |
| `domain.entity.action`   | `inventory.foo.restocked`, `billing.bar.invoiced` |

### Identifier semantics (UUIDv7 — 36-char canonical lowercase hex, from `Guid.CreateVersion7()`)

UUIDv7's first 48 bits are a big-endian millisecond timestamp, so the
canonical hex string sorts chronologically — the contract this library
relied on the older Cysharp `Ulid` package for. `Guid.CreateVersion7()`
is BCL-native (.NET 9+), so Roadbed.Messaging carries no NuGet
dependency for id generation.

| Property                        | Meaning                                                                |
| ------------------------------- | ---------------------------------------------------------------------- |
| `BaseMessagingMessage.Identifier` | The message's own UUIDv7. Lexicographically sortable by creation time. |
| `MessagingPublisher.Identifier` | The **publishing instance's** UUIDv7 — process / container / machine ID. |
| `MessagingMessageResponse.OriginalRequestIdentifier` | The `Identifier` of the request being replied to.   |
