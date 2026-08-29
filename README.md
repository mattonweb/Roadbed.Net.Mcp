# Roadbed.Net.Mcp

A shared, guarded HTTP fetch primitive for AI agents, exposed as a
[Model Context Protocol](https://modelcontextprotocol.io) (MCP) server over stdio.

One tool, `Get`: HTTPS-only retrieval with a four-zone destination validation pipeline
(of which only the network gate is the security boundary), redirects followed manually and
re-validated, per-host politeness spacing, and a per-session usage cap. It returns the
**raw response body — never a rendered DOM**.

It is not a crawler and not a sandbox: it narrows one path — outbound HTTP — and makes that
path uniform, observable and bounded.

**Status: MVP built.** `net10.0`, MSTest, 180 tests.

- The design is [docs/SDD.md](docs/SDD.md). It is written to be implementable without
  further context — read it in full before changing code.
- The SDD's four open decisions (§11) are ruled in [docs/decisions.md](docs/decisions.md).
- `src/` carries vendored Roadbed framework assemblies. `Roadbed.Common` supplies the
  logging base class. `Roadbed.Net`'s `NetHttpClient` is deliberately unused: it owns its
  own redirect, retry and deserialization policy, and SDD §5–6 require redirects disabled,
  hop-by-hop re-validation and a resolver seam, which need `HttpClient` directly.

## The tool

`Get(url)` returns one JSON object:

| field | notes |
|---|---|
| `ok` | True only when `outcome` is `ok`. |
| `outcome` | `ok`, `refused`, `blocked`, `notFound`, `error`, `throttled`. **Read this, not `status`.** |
| `requestedUrl` | Exactly what was asked for. |
| `finalUrl` | After redirects. May differ; read this one. |
| `redirectCount` | |
| `status` | HTTP status of the final response. Null when nothing was sent. |
| `contentType` | As the server declared it. Not trusted, not enforced. |
| `contentLength` | Body bytes read off the wire. |
| `body` | The **raw** response. Textual types decoded; everything else base64. |
| `truncated` | True when the size cap stopped the read. |
| `refusalReason` | Names the rule, when `outcome` is `refused`. |

`blocked` means a bot wall answered — which is **not** the same as a page with nothing on
it. Both arrive as HTTP 200 with valid markup, and a caller that conflates them records
"nothing here" as a settled fact about a destination that merely refused it. Challenge
markers live in [ChallengeMarkers.cs](src/Roadbed.Net.Mcp/Fetching/ChallengeMarkers.cs),
versioned, not in an agent's prompt.

## Configuration

Optional. Create `.Roadbed.Net.Mcp` in your home directory; absent, the ruled defaults
apply. A file that exists but is malformed fails startup rather than half-applying.

```json
{
  "maxCallsPerSession": 50,
  "maxResponseBytes": 10485760,
  "requestTimeoutSeconds": 30,
  "maxRedirects": 5,
  "minHostIntervalMilliseconds": 1000,
  "circuitFailureThreshold": 5,
  "circuitCooldownSeconds": 60,
  "productName": "Your.Agent",
  "productVersion": "1.0",
  "contactUrl": "https://example.com/bots",
  "contactEmail": "bots@example.com"
}
```

The default `User-Agent` is self-identifying:
`Mozilla/5.0 (compatible; {ProductName}/{Version}; +{ContactUrl}; {ContactEmail})`.
Full browser impersonation is possible via a verbatim `userAgent` override and is a
different posture — it forfeits the contactable half and does not clear challenge-based
protections anyway. That choice belongs to the owner of each consuming repo; it is never
this repo's default.

## Known limitations (by design, MVP)

- **Per-host spacing and the usage counter are in-memory, therefore per process — and under
  stdio, per agent.** The politeness guarantee is per-agent, not global; agents that fetch
  are scheduled apart. See docs/decisions.md, decision 3.
- **`maxCallsPerSession` is not a wall-clock window.** The counter resets when the server is
  respawned. The name says what it actually bounds.
- **DNS rebinding is narrowed, not eliminated.** Zone 3 resolves before the request and the
  connect callback re-checks every address at the moment the socket opens, so the window is
  the length of one connect rather than one request. Closing it entirely means pinning the
  connection to a pre-validated address, which the SDD does not specify.
- **`GetRendered` is deferred** (SDD §8) and, if ever built, is a separately named tool —
  never a flag on `Get`.

## Building

```bash
dotnet test src/Roadbed.Net.Mcp.slnx
```
