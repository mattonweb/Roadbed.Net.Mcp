# Ruled decisions — SDD §11

The SDD ([SDD.md](SDD.md)) is kept byte-exact as authored; its four open decisions are
ruled here. Ruled by the Co-Founder seat, 2026-08-28, delegated by the Owner the same day.

## 1. Usage cap: default 50 calls per session, config-named as per-session

The cap is sized to the batch, not a round number, per SDD §9: budget ~4–5 calls per
subject and ~10 subjects per scheduled session. Default **50**, configurable per consuming
repo.

The config key must say what the counter actually bounds: name it `maxCallsPerSession`
(or equivalent), never "per hour" — under stdio the in-memory counter resets on process
respawn, so it bounds a session, and the name must not imply wall-clock semantics the
implementation does not have.

## 2. User-Agent: self-identifying default; impersonation never ships as a default

The shipped default is the SDD §6 form:

```
Mozilla/5.0 (compatible; {ProductName}/{Version}; +{ContactUrl}; {ContactEmail})
```

Full browser impersonation remains possible by explicit configuration only, and that
posture belongs to the owner of each consuming repo — it is never this repo's default.
Rationale accepted as written in the SDD: impersonation forfeits the contactable half and
does not clear challenge-based protections anyway.

## 3. Spacing state: per-process accepted for MVP; the answer is scheduling, not shared state

No shared cross-agent spacing state is built. The fleet's scheduled agents run staggered,
which is the operational form of "schedule them apart." The limitation is documented in
the README and in this file so it is known rather than discovered.

Trigger to revisit: if concurrently-running fetching agents become normal, shared spacing
state is commissioned as its own piece of work — not patched in.

## 4. Response size cap: 10 MB default, configurable, truncate-not-error

10 MB accepted as proposed. Legitimate listing pages exceed 8 MB, so a smaller default
truncates real content. Exceeding the cap returns `truncated: true` with what was read,
per SDD §6.

---

## Also settled at repo setup (not SDD §11)

- **Vendored assemblies.** `src/` carries `Roadbed.Common` and `Roadbed.Net` Release
  builds, following the `Roadbed.Logging.Mcp` precedent. Using them is the implementer's
  choice; the SDD's redirect and resolver requirements may argue for `HttpClient`
  directly. Vendoring them removes a dependency stall, it does not mandate a dependency.
- **The SDD's own recommendations were accepted on all four decisions** — they were
  well-reasoned and nothing in the fleet's operating model argued otherwise.
