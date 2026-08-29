# Roadbed.Net.Mcp

A shared, guarded HTTP fetch primitive for AI agents, exposed as a
[Model Context Protocol](https://modelcontextprotocol.io) (MCP) server over stdio.

One tool, `Get`: HTTPS-only retrieval with a four-zone destination validation pipeline
(of which only the network gate is the security boundary), redirects followed manually and
re-validated, per-host politeness spacing, and a per-session usage cap. It returns the
**raw response body — never a rendered DOM**.

It is not a crawler and not a sandbox: it narrows one path — outbound HTTP — and makes that
path uniform, observable and bounded.

**Status: pre-build.**

- The design is [docs/SDD.md](docs/SDD.md). It is written to be implementable without
  further context — read it in full before writing code.
- The SDD's four open decisions (§11) are ruled in [docs/decisions.md](docs/decisions.md).
- `src/` carries vendored Roadbed framework assemblies (`Roadbed.Common`, `Roadbed.Net`);
  whether the implementation uses them is the implementer's choice — the validation
  pipeline in SDD §5–6 requires low-level control (redirects disabled, resolver seam) that
  may be cleaner against `HttpClient` directly.

## Known limitation (by design, MVP)

Per-host spacing and the usage counter are in-memory, therefore **per process — and under
stdio, per agent**. The politeness guarantee is per-agent, not global; agents that fetch
are scheduled apart. See docs/decisions.md, decision 3.
