# SDD — Roadbed.Net.Mcp

**A shared, guarded HTTP fetch primitive exposed to AI agents over MCP (stdio).**

Status: proposed. Target repo: `Roadbed.Net.Mcp` (public).
Author: Pebble session, 2026-08-29. Requested by the Owner.

---

## 1. Why this exists

Agents currently reach the web by **writing ad-hoc scripts at run time**. That has
two costs, and only one of them is obvious.

The obvious one: those scripts crash, and a crashing subprocess has taken the
harness down with every other agent running under it.

The less obvious one: **every agent reimplements the same outbound policy, badly
and differently.** Timeouts, redirect handling, user agent, size caps and
destination validation are decided afresh in each throwaway script, by an agent
optimising for getting an answer rather than for being a good citizen or for not
reaching somewhere it should not.

This tool replaces both with one audited surface. The agent asks for a URL and
receives bytes; every policy decision has already been made, in code, once.

*** IT IS NOT A SECURITY SANDBOX FOR THE AGENT. *** An agent that can still run
arbitrary code can still do arbitrary things. This narrows one specific path —
outbound HTTP — and makes that path uniform, observable and bounded. The harness
isolation problem is separate and is not solved here.

## 2. Scope

**In scope (MVP).** One tool: `Get`. Plain HTTP, raw bytes returned to the
caller, destination validated, redirects followed manually and re-validated,
usage capped.

**Out of scope, deliberately.**

- **`GetRendered` (browser/Playwright).** Tabled. See §8 — it is specified so the
  seam exists, and it is not built.
- POST, PUT, or any method other than GET. Retrieval only.
- Authentication of any kind. No cookie jar, no credentials, no bearer tokens.
  A destination that needs a login is out of reach by design.
- Parsing. The tool returns bytes. What they mean is the caller's problem.
- Caching. Every call is a live fetch.

**Non-goal worth stating: this is not a crawler.** It fetches URLs it is given,
one at a time. It does not follow links, enumerate, or discover.

## 3. Dependency rule

`Roadbed.Net.Mcp` sits in the framework layer. **It must not reference any
product assembly.** Nothing about job boards, companies, or any consuming
domain appears in this repo. If a rule here can only be justified by one
product's use case, it does not belong here.

## 4. The tool surface

### `Get`

**Input**

| field | type | notes |
|---|---|---|
| `url` | string, required | Absolute. Validated per §5 before anything is contacted. |

**Output**

| field | type | notes |
|---|---|---|
| `ok` | bool | Whether bytes were retrieved. |
| `outcome` | enum | See §7. Never inferred by the caller from `status` alone. |
| `requestedUrl` | string | Exactly what was asked for. |
| `finalUrl` | string | After redirects. **May differ; callers must read this one.** |
| `redirectCount` | int | Hops **followed**. A refusal at the first hop reports 0, which does not mean no redirect happened. |
| `status` | int? | HTTP status of the final response. Null when nothing was sent. |
| `contentType` | string? | As the server declared it. Not trusted, not enforced. |
| `contentLength` | int | Bytes actually returned. |
| `body` | string | **The raw response body.** See below. |
| `truncated` | bool | True when the size cap stopped the read. |
| `refusalReason` | string? | Present when `outcome` is `refused`; names the rule. |
| `refusedUrl` | string? | Present when `outcome` is `refused`; the URL the rule was applied to. On a redirect refusal it is the declined hop in **resolved absolute form**, not the raw `Location` header and not `requestedUrl`/`finalUrl`, which both still name a URL that passed. |
| `refusalStage` | string? | Present when `outcome` is `refused`; `request` only when the refused URL is the caller's own and no hop was followed, otherwise `redirect`. The reason alone cannot separate the two: an `http://` hop and an `http://` request both yield `scheme_not_https`. |

*** `body` IS THE RAW RESPONSE. NOT A RENDERED DOM, EVER. *** This is the single
most important line in the document. A consumer reasoning about post-JavaScript
markup is reasoning about content its own production code will never receive.
The distinction must remain visible in the API surface, which is why a future
rendered variant is a **separately named tool** (§8) and never a flag on this
one — a flag can be set by accident, a different tool name cannot.

Binary responses are returned base64-encoded with `contentType` intact. The
caller decides what to do; the tool does not guess.

## 5. URL validation

Four zones. **Only zone 3 is a security boundary.** Zones 1 and 2 are cheap
filters that kill the obvious early and produce good refusal messages; they must
never be described, in code or in comments, as the thing keeping the fetch safe.

```
   caller supplies:  urlString
          │
┌─────────▼──────────────────────────────────────────────────────────────┐
│ ZONE 1 · STRING GATE — before Uri.TryCreate.  Cheap. Heuristic.        │
├────────────────────────────────────────────────────────────────────────┤
│  1  length <= 2048                                    else REFUSE      │
│  2  every char in 0x21..0x7E   (no space, CR, LF, NUL, unicode)        │
│  3  starts with "https://"  (case-insensitive)        else REFUSE      │
│       kills  http://  file://  ftp://  data:  javascript:  //host      │
│  4  authority := text between "https://" and the first '/'             │
│  5  authority contains '@'                            -> REFUSE        │
│       https://evil.com@10.0.0.1  ->  host really IS 10.0.0.1           │
│  6  authority contains ':'                            -> REFUSE        │
│  7  authority contains '%' or '\'                     -> REFUSE        │
│  8  authority all [0-9.]  OR starts '['  OR starts '0x'  -> REFUSE     │
│       kills  10.0.0.1   2130706433   0x7f000001   [::1]                │
│  9  authority has >= 4 consecutive numeric labels     -> REFUSE        │
│       kills  10.0.0.1.nip.io  and the whole embedded-IP-DNS family     │
│ 10  authority contains at least one '.'               else REFUSE      │
│       kills  intranet   localhost                                      │
│ 11  final label alphabetic, length >= 2               else REFUSE      │
│ 12  authority not *.local  *.internal  *.home.arpa  *.localhost        │
└─────────┬──────────────────────────────────────────────────────────────┘
          │ passes
┌─────────▼──────────────────────────────────────────────────────────────┐
│ ZONE 2 · PARSE GATE — Uri.TryCreate, then re-assert on the PARSED      │
│ object, because the parser need not agree with your string reading.    │
├────────────────────────────────────────────────────────────────────────┤
│   Uri.TryCreate(s, Absolute, out u)                   else REFUSE      │
│   u.Scheme == "https"                                 else REFUSE      │
│   u.HostNameType == UriHostNameType.Dns               else REFUSE      │
│   string.IsNullOrEmpty(u.UserInfo)                    else REFUSE      │
│   u.Port == 443                                       else REFUSE      │
└─────────┬──────────────────────────────────────────────────────────────┘
          │ passes — the SHAPE is trustworthy.  The DESTINATION is not.
╔═════════▼══════════════════════════════════════════════════════════════╗
║ ZONE 3 · NETWORK GATE — *** THE ACTUAL SECURITY BOUNDARY ***           ║
╠════════════════════════════════════════════════════════════════════════╣
║   Resolve the host.  EVERY returned address must be public.            ║
║     v4 reject  0/8  10/8  100.64/10  127/8  169.254/16  172.16/12      ║
║                192.0.0/24  192.168/16  198.18/15  224/4  240/4         ║
║     v6 reject  ::1   fc00::/7   fe80::/10   ff00::/8                   ║
║        unwrap ::ffff:a.b.c.d and re-check the inner v4                 ║
║                                                                        ║
║   ONLY THIS CATCHES  careers.example.com -> 10.0.0.1                   ║
║   Zones 1-2 cannot: that string is indistinguishable from a real one.  ║
╚═════════┬══════════════════════════════════════════════════════════════╝
          │ fetch with automatic redirects DISABLED
┌─────────▼──────────────────────────────────────────────────────────────┐
│ ZONE 4 · EVERY REDIRECT — re-enter at ZONE 1 with the Location value.  │
│ Max 5 hops.  Third-party-controlled input: trust it least.             │
└────────────────────────────────────────────────────────────────────────┘
```

### Why zones 1 and 2 are written the way they are

Measured against .NET's `Uri` on 2026-08-29:

| input | parsed host | userInfo | hostNameType |
|---|---|---|---|
| `https://evil.com@10.0.0.1/x` | **10.0.0.1** | evil.com | IPv4 |
| `https://2130706433/x` | **127.0.0.1** | – | IPv4 |
| `https://0x7f000001/x` | **127.0.0.1** | – | IPv4 |
| `https://[::1]/x` | `[::1]` | – | IPv6 |
| `https://intranet/x` | intranet | – | Dns |
| `https://10.0.0.1.nip.io/x` | 10.0.0.1.nip.io | – | **Dns** |
| `https://example.com\@10.0.0.1/x` | *rejected by .NET* | | |

Two consequences drive the design.

**A credentialed URL hides its destination from a human reader.** In
`https://evil.com@10.0.0.1/x` the host is `10.0.0.1`; `evil.com` is the
username. Anyone scanning a log sees a domain and reads it as the destination.
Rule 5 exists for that, and the zone-2 `UserInfo` check exists because a string
reading and a parser reading can disagree.

**`HostNameType` cannot catch DNS that points inward.** `10.0.0.1.nip.io` parses
as `Dns` and looks like an ordinary hostname. Public DNS services resolve
embedded addresses as a feature, and any domain owner can point a record at
private space with no numeric pattern at all. Rule 9 catches the visible family;
**zone 3 is what catches the rest**, and nothing above it can.

### Rejected: a TLD allowlist

Restricting to a fixed TLD list was considered and **should not be built**. It
provides no defence — a `.com` resolves into private space exactly as easily as
any other TLD, so zone 3 does all the work either way — while silently blocking
legitimate destinations. A survey of one consuming product's live integrations
found real, in-production hosts on `.co`, `.link`, `.org` and `.jobs`, none of
which a plausible allowlist would have included. The failure mode is bad: a
refusal that looks like a policy decision rather than a bug.

## 6. Outbound behaviour

**Redirects.** Automatic following **off**. Follow manually so each `Location`
re-enters zone 1. Cap 5. A redirect to a non-https scheme is a refusal, not a
downgrade.

**Caps.** Response size cap (default 10 MB, configurable) — exceeded returns
`truncated: true` with what was read, not an error. Per-request timeout (default
30s).

**Per-host spacing and circuit breaking.** Minimum interval between requests to
the same host, and an open-circuit cooldown after repeated failures.

*** SPACING STATE IS PER PROCESS, AND UNDER STDIO THAT MEANS PER AGENT. *** If
several agents each spawn their own server instance, each gets an independent
limiter and the guarantee is per-agent rather than global. Two agents scheduled
apart is a scheduling answer to that, not a technical one. If concurrent agents
ever become normal, the state must move somewhere shared — noted here so the
limit is known rather than discovered.

**Identity.** The `User-Agent` is configuration, not a constant, with a
self-identifying default of the form:

```
Mozilla/5.0 (compatible; {ProductName}/{Version}; +{ContactUrl}; {ContactEmail})
```

*** THIS IS A POSTURE DECISION AND BELONGS TO THE OWNER OF EACH CONSUMING
REPO. *** Browser-compatible enough to clear the WAFs that block bare
framework agents, while still telling a publisher who we are and how to reach
us before they block us. Full browser impersonation is possible by
configuration and is a different posture: it forfeits the contactable half, and
in practice does not clear challenge-based protections anyway, which key on
script execution and TLS fingerprints rather than the agent string.

## 7. Outcomes

The caller must never infer these from `status` alone.

| outcome | meaning |
|---|---|
| `ok` | 2xx, bytes returned. |
| `refused` | Failed validation. **Never contacted.** `refusalReason` names the rule. |
| `blocked` | Contacted, and the response is a bot wall or challenge, not content. |
| `notFound` | 404 / 410. |
| `error` | Transport failure, timeout, or 5xx. |
| `throttled` | Usage cap reached (§9). Nothing was sent. |

*** `blocked` MUST BE DISTINGUISHABLE FROM AN EMPTY OR UNINTERESTING PAGE. ***
A challenge page returns 200 with valid markup and no useful content. A caller
that cannot tell those apart will record "nothing here" as a settled fact about
a destination that simply refused it — a wrong conclusion that looks finished
and never gets revisited. Detection is by challenge markers in the body, and the
markers belong in this repo, versioned, not in an agent's prompt.

## 8. Deferred: `GetRendered`

Not built. Specified only so the shape is agreed.

When a page's content is assembled by script and the raw response carries
nothing useful, a rendered fetch is the only option. It stays deferred because
it is expensive, because browser processes are themselves a stability risk of
exactly the kind this tool exists to reduce, and because it is rarely needed —
in the survey behind this document, four destinations in five were fully
answered by a plain GET.

If built:

- A **separately named tool**, never a flag on `Get`.
- Agent instructions must say: try `Get` first; escalate only on failure.
- *** DO NOT FILTER SUBRESOURCES — ISOLATE THE NETWORK. *** A page loads dozens
  of resources nobody named, and policing that request graph is fragile in a way
  the top-level check is not. Run the browser where private ranges are not
  routable at all.
- Single browser instance, page per call, hard timeouts, periodic recycling.

## 9. Usage limiting

An in-memory counter caps calls per rolling window, refusing with `throttled`.

*** IN-MEMORY MEANS PER PROCESS, NOT PER WALL-CLOCK HOUR. *** Under stdio the
counter resets whenever the server is respawned, so in practice it bounds a
*session*. That is likely the desired behaviour for scheduled agents, but the
semantics differ from what "per hour" implies and should be named as such in
configuration.

**Size the cap to the work, not to a round number.** Real usage is rarely one
call per subject: a page is fetched, and then something that page named is
fetched too. A cap set at one call per subject silently halves what an agent can
finish, and the failure looks like the agent giving up rather than being cut
off. Budget roughly 4–5 calls per subject in a batch and set the cap from there.

## 10. Testing

- **Validation is table-driven** over hostile inputs — every row of the §5 table,
  plus each rejected range in zone 3 — asserting the specific `refusalReason`,
  not merely that something was refused.
- **A redirect chain ending at a private address must be refused**, and there
  must be a test proving it, because that is the path the string gate cannot see
  and the one most likely to regress.
- **Zone 3 must be unit-testable without DNS**, behind a resolver seam, so the
  range arithmetic is tested directly rather than through the network.
- **A `blocked` fixture and an empty-but-legitimate fixture must both exist**,
  asserting they produce different outcomes. They are the pair most likely to be
  conflated.

## 11. Open decisions

1. **Usage cap value and window.** Recommend sizing to batch (§9) rather than a
   flat number.
2. **Default `User-Agent` per consuming repo** — self-identifying, or
   impersonating. Recommend self-identifying; it is a per-repo owner decision.
3. **Whether spacing state must be shared across agents**, or whether scheduling
   them apart is a sufficient answer for now. Recommend scheduling for MVP, with
   the limitation documented.
4. **Response size cap default.** 10 MB proposed; some legitimate careers and
   listing pages exceed 8 MB, so a smaller cap will truncate real content.
