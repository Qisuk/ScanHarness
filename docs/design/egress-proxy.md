# Design: network egress control through a proxy

**Status:** Implemented, phases 1–5 (see [Implementation notes](#implementation-notes)) · **Date:** 2026-10-22

## Context

ScanHarness decides in code which targets a tool may be pointed at
(`ScopeGuard` in `Services/Engagements/Scope.cs`), but that check only covers the **argument the
harness passes to the tool**. What the tool then does on the network is unchecked. A tool can:

- follow a redirect or a link to a site outside the scope (a crawler on `shop.acme.test` that
  follows a link to `evil.example`);
- be told to by its input (a scanned page that injects instructions into a tool that uses a model,
  or a malicious payload that exploits the tool itself);
- be compromised or buggy (a third-party scanner with a vulnerable dependency, or a plugin that
  phones home);
- leak data (exfiltration by HTTP, or by DNS lookups such as `<data>.attacker.example`).

The tools are simulations today (`DemoToolAPI_1` never touches the network), so this gap is
theoretical now and becomes real the moment a real scanner (ZAP, nuclei, a crawler) is plugged in.
The same applies to the harness itself: when `Llm:BaseUrl` points at a hosted provider, prompts
leave the machine, and we can't see that traffic outside the harness's own logs.

**We want a network-level control that stops any tool reaching a host not on an allow-list,
whatever the tool or the model asked for, and that records all traffic in and out of the harness.**

## Goals

1. A tool can only open connections to hosts the policy allows, and nothing else. This holds even
   if the tool is malicious, misconfigured or ignores proxy settings.
2. The allow-list for scan and attack tools comes from the **approved engagement** (its domains and
   testing window), so the network enforces the same scope as `ScopeGuard`, independently.
3. Each tool gets its own allow-list (the weather tool may reach the weather API and nothing else).
4. Every connection attempt, allowed or denied, is logged with the tool call, session, user and
   engagement it belongs to, and shown in the harness alongside the tool call.
5. A denied connection is a high-signal security event: it shows up as a **Block** like a tripped
   control, and admin can find and alert on it.
6. Fail closed: if the proxy or its policy is unavailable, there is no egress.
7. LLM agnostic and tool agnostic: nothing depends on which model chose the call or how the tool is
   written, beyond the tool being able to use a proxy (or being forced to).

## Non-goals

- Replacing `ScopeGuard`. It stays: it runs first, gives the user a clear reason, and stops the
  call before anything starts. The proxy is a second, independent layer.
- Inspecting or filtering attack payloads (what a permitted attack sends to an in-scope host is
  the pentester's business, within the ROE).
- Protecting against a compromised host machine or Docker daemon.

## Threat model

| Threat | Example | Covered by |
|---|---|---|
| Out-of-scope target | Crawler follows a link to a third-party site | Allow-list per call (§3) |
| Redirect off-scope | `302 Location: https://evil.example/` | The new connection is checked like any other |
| Proxy bypass | Tool ignores `HTTPS_PROXY`, opens a socket directly | No route out except the proxy (§2) |
| Allowed name, private address (SSRF / DNS rebinding) | `in-scope.acme.test` resolves to `169.254.169.254` or `10.0.0.5` | Proxy resolves names and blocks private ranges (§4) |
| IP literals | `https://203.0.113.7/` | Denied unless a rule allows that address |
| DNS exfiltration | Lookups of `secret-data.attacker.example` | Tools get no DNS resolver; only the proxy resolves |
| Non-HTTP protocols | Raw TCP to port 25, UDP | Only allowed ports; non-HTTP needs the L4 gateway (§8) |
| Unanchored regex | `example\.com` also matches `example.com.evil.net` | Rules matched against the whole host; validation rejects unanchored patterns (§5) |
| Look-alike names | `exаmple.com` with a Cyrillic а | Hosts normalised to punycode before matching (§5) |
| Domain fronting | `CONNECT cdn.allowed.example` then `Host: evil.example` inside TLS | Only detectable with TLS inspection (§7); noted as a residual risk |
| Data leaving to a hosted LLM | Prompts with customer data sent to a provider | The harness's own traffic goes through the proxy too (§6) |

## Design

### 1. Overview

```
                 internal network (no route out)               │  outside
                                                               │
 ┌──────────┐  tool call +    ┌──────────────┐  HTTP(S) via    │
 │ Harness  │──egress token──▶│ Tool API(s)  │──── proxy ────┐ │
 └────┬─────┘                 └──────────────┘               ▼ │
      │  policy: engagements,                       ┌─────────────────┐   allowed
      │  tool allow-lists, tokens  ◀──── authorise ─│  Egress proxy   │──────────▶ in-scope hosts
      │                                             │ (only container │
      │  egress events         ◀──── access log ────│  on both nets)  │──✕ denied: logged, 403
      ▼                                             └─────────────────┘
   SQLite: egress_events
```

- **Data plane:** a mature forward proxy that every tool's traffic must pass through.
- **Control plane:** the harness owns the policy (it already knows the engagements, tools and tool
  calls) and answers "may this connection go ahead?".
- **Enforcement by topology:** the tools sit on a network with no route to the outside. The only
  way out is the proxy, so a tool that ignores the proxy simply can't connect.

### 2. Network layout (no bypass)

The tool APIs move into containers in `docker-compose.yml`, on a Docker network declared with
`internal: true`, which has no default gateway. The proxy is the only container attached to both
that network and an outbound network.

```yaml
networks:
  tools:    { internal: true }   # tool containers: no route out, no external DNS
  egress:   {}                   # proxy only

services:
  egress-proxy:
    networks: [tools, egress]
  tool-api:
    networks: [tools]
    environment:
      HTTP_PROXY:  http://egress-proxy:3128
      HTTPS_PROXY: http://egress-proxy:3128
      NO_PROXY:    ""            # nothing goes direct
```

- Docker's embedded DNS on an internal network only resolves container names, so tools can't look
  up (or leak data through) external names. Only the proxy resolves them.
- The harness reaches the tool APIs over the `tools` network (or a published port for local runs).
- Works the same on Docker Desktop for Windows, Linux and in CI.

**Local runs without Docker** (today's `dotnet run`) can still set the proxy variables, but nothing
stops a tool ignoring them. That mode is labelled **advisory** in the UI and the log, never
**enforced**. On Windows, an outbound firewall rule per tool executable (allow only the proxy's
port) can make it enforced; that's a system setting the operator applies, not something the
harness does.

### 3. Policy: what each connection may reach

A connection is allowed only if **every** check passes:

1. The port is allowed (default 80 and 443).
2. The host matches an **allow** rule that applies to the caller, and no **deny** rule.
3. The resolved address isn't private, loopback, link-local or multicast (§4), unless a rule
   explicitly allows that range.
4. For a tool with `requiresScope`, the host is inside the engagement that the tool call was
   approved under, and today is inside its testing window.

Allow rules come from three places:

| Source | Applies to | Example |
|---|---|---|
| Engagement (approved, in window) | Tool calls of `requiresScope` tools made under it | `acme.test` and its subdomains, 1 Oct to 31 Dec |
| `egress` block on a tool in `tools.json` | Every call of that tool | Weather tool → `api.open-meteo.com` |
| `egress.json` | Named callers, or everyone | Harness → the hosted LLM endpoint |

Deny rules always win. `egress.json` is loaded and validated at startup like `controls.json`: an
invalid file stops the app, listing every problem.

```json
{
  "defaultAction": "Deny",
  "allowedPorts": [80, 443],
  "blockPrivateAddresses": true,
  "rules": [
    {
      "id": "llm-provider",
      "action": "Allow",
      "appliesTo": ["harness:llm"],
      "match": { "type": "domain", "value": "api.example-llm.com" }
    },
    {
      "id": "acme-cdn",
      "action": "Allow",
      "appliesTo": ["simple_scanner"],
      "match": { "type": "regex", "value": "^([a-z0-9-]+\\.)*cdn\\.acme\\.test$" }
    },
    {
      "id": "cloud-metadata",
      "action": "Deny",
      "match": { "type": "cidr", "value": "169.254.0.0/16" }
    }
  ]
}
```

And on a tool in `tools.json`:

```json
"egress": { "domains": ["api.open-meteo.com"], "ports": [443], "engagementScope": false }
```

`engagementScope` defaults to the tool's `requiresScope`. A tool with no `egress` block and no
scope gets **no** network access, so a new tool is closed by default.

### 4. Name resolution and addresses

The proxy resolves the hostname itself, checks every resolved address against the private-range
list, and connects to the address it checked (not a second lookup), which defeats DNS rebinding.
Blocked by default: `10/8`, `172.16/12`, `192.168/16`, `127/8`, `169.254/16`, `100.64/10`,
`0/8`, `::1`, `fc00::/7`, `fe80::/10`, and multicast. IP-literal targets are denied unless a
`cidr` rule allows them (the engagement validator already refuses IP addresses in scope).

### 5. Matching rules, including regex

Hosts are normalised before matching, the same way for every rule type: lower case, trailing dot
removed, IDNs converted to punycode (`IdnMapping`), port stripped.

| `match.type` | Matches | Notes |
|---|---|---|
| `domain` | The domain and its subdomains | Same semantics as `ScopeGuard.IsWithin`, sharing the code |
| `exact` | Only that host | For APIs where subdomains shouldn't count |
| `regex` | The **whole** normalised host | .NET regex, case-insensitive, 250 ms timeout counts as a match for deny rules and a miss for allow rules (fail closed both ways) |
| `cidr` | The resolved address | For deny lists, and the rare IP-based allow |

Validation at startup rejects: regexes that don't parse, patterns not anchored with `^` and `$`,
a `.` that isn't escaped next to a letter (`example.com` almost always means `example\.com`), and
allow regexes that match an obvious canary such as `evil.invalid` (a sign of `.*`). The Controls
page lists the rules read-only, so it's clear what is allowed.

### 6. Who is calling: egress tokens

To apply the right allow-list and link traffic to a tool call, the proxy has to know which tool
call a connection belongs to. Source IP only identifies the container, not the call.

- When a tool call is about to run (after approval and `ScopeGuard`), the harness mints an
  **egress token**: tool call id, session, user, tool, engagement id and version, the allowed
  hosts, and an expiry (the agent timeout). It is signed with a key the proxy's policy check
  verifies.
- `ToolCallService` sends it to the tool API as a header (`X-Egress-Token`), and the tool sends it
  on its outgoing requests as `Proxy-Authorization`. For third-party tools this is their standard
  upstream-proxy credential setting (ZAP, nuclei, curl and .NET's `WebProxy` all support it).
- **No token, or an invalid or expired one:** the proxy falls back to the static allow-list for
  that container (by source address), with no engagement scope. A scanner without a token can't
  reach any target.
- The harness's own outgoing calls (the LLM client, and later ownership checks) use the caller
  `harness:llm` and so on, with their own `egress.json` rules.

Tokens are short-lived and scoped to one call, so a leaked token only grants what that call was
already allowed.

### 7. Logging and monitoring

Every connection attempt produces an **egress event**:

| Field | Example |
|---|---|
| time, duration | `2026-10-22T10:14:03Z`, 812 ms |
| tool call id, session, user, tool, engagement | from the token, or `-` |
| caller (container or `harness:llm`) | `tool-api` |
| method, host, port, resolved address | `CONNECT shop.acme.test:443 → 198.51.100.4` |
| decision and rule | `Allowed: engagement Acme v2` / `Denied: no rule matches` |
| bytes sent and received, status | 2.1 kB / 48 kB |

- The policy check records the decision when it's made, and the proxy's JSON access log supplies
  bytes and duration. Both are joined into a new `egress_events` table by `IActivityStore`, and
  denials also become log entries (so they appear in the activity feed and reopened sessions, like
  handoffs) and lines in the tab-separated audit log.
- **In the UI:** the tool output pane gets a **Network** section listing the connections a call
  made; a denied connection gets a red pip (stage **Egress**) and counts as a block for the
  session's colour; admin's **All activity** gains an egress filter (denied only, by host, by tool);
  the session summary gets a **Network activity** section (hosts contacted, totals, denials).
- **Alerts:** a denial from a tool with a valid token is the strongest signal (a tool tried to
  leave its scope mid-call). Also worth flagging: bytes sent far above bytes received (possible
  exfiltration), and a burst of denials.

**Without TLS inspection** (the default), HTTPS traffic is tunnelled, so the proxy sees the host,
port, timing and sizes, but not paths, headers or bodies. **With TLS inspection** (optional, per
tool), the proxy terminates TLS with a harness CA that the tool containers trust, which adds full
request and response logging (useful as evidence of exactly what an attack sent), Host header
checks against the CONNECT host (stops domain fronting), and path-level rules. It's off by default:
it breaks certificate pinning, some scanners dislike it, and it captures customer data that then
needs retention rules.

### 8. Choosing the proxy

| Option | Strengths | Weaknesses |
|---|---|---|
| **Envoy** (recommended) | Dynamic forward proxy with CONNECT; `ext_authz` calls the harness for every connection, so engagement, token and window checks live in the harness's C#; structured JSON access logs; optional TLS termination | More configuration to learn |
| Squid | Mature, simple; `dstdomain` and `dstdom_regex` ACLs; SNI peek-and-splice; external ACL helper for dynamic checks | Policy split between Squid config and a helper; reloads for static changes |
| Smokescreen (Stripe) | Built for exactly this: egress allow-lists per role, blocks private ranges by default | Roles are static YAML; no per-engagement or per-call policy without a fork |
| mitmproxy | Python add-ons, easy full inspection | Better as a debugging and evidence tool than as the enforcement point |
| Write one in .NET | Same language as the harness | A security boundary we'd own the parsing bugs of; not worth it |

**Recommendation:** Envoy as the data plane, with `ext_authz` calling a small **egress policy
endpoint** in the harness (or a separate ASP.NET service sharing the policy code, if the harness
shouldn't be on the tools network). Envoy fails closed when `ext_authz` is unreachable
(`failure_mode_allow: false`). Squid is the fallback if Envoy proves too heavy for the demo.

Tools that don't speak HTTP (nmap-style scanners, raw TCP or UDP) can't use an HTTP proxy. For
those, a later **L4 gateway** container does the same job at the packet level: an `nftables`
allow-list of the engagement's resolved addresses and ports, refreshed when engagements change,
with connection logging. Same policy, different enforcement point.

## Mapping to the current code

| Today | Becomes |
|---|---|
| `ScopeGuard` | Unchanged; `IsWithin` shared with the egress policy's `domain` match |
| `ToolDefinition` (`tools.json`) | Gains an optional `egress` block, validated at startup |
| `ToolCallService.CallToolApiAsync` | Mints the egress token and sends it to the tool API |
| `OpenAiCompatibleClient` | Its `HttpClient` uses the proxy (caller `harness:llm`) when `Egress:ProxyUrl` is set |
| `ControlStage` | Gains `Egress` for pips and log entries (configured in `egress.json`, not `controls.json`) |
| `IActivityStore` | Adds `egress_events` |
| `docker-compose.yml` | Adds the `tools` and `egress` networks, the proxy and containerised tool APIs |
| New | `Services/Egress/EgressPolicy.cs` (rules, matching, validation), the policy endpoint, `egress.json`, token signing |

New settings (section `Egress`): `Mode` (`Off`, `Advisory`, `Enforced`), `ProxyUrl`,
`DefinitionsPath` (`egress.json`), `TokenSigningKey` (user secrets), `TlsInspection` (per tool).

## Phased plan

Each phase is useful on its own.

1. **Policy in code.** `EgressPolicy` with rule types, normalisation, regex validation, private
   ranges, engagement and window checks, and `egress.json` / `tools.json` loading. Unit-tested,
   no proxy yet. Surface the rules on the Controls page.
2. **Proxy with a static allow-list, enforced by topology.** Envoy (or Squid) in Docker Compose,
   tool APIs containerised on the internal network, allow-list generated from the files. Give the
   demo scanner an optional real fetch (`Scanner:LiveFetch`) so there's traffic to control.
   Access log to a file.
3. **Engagement-aware, per-call decisions.** Egress tokens, the `ext_authz` policy endpoint, the
   `egress_events` table, the Network section, Egress pips and the session colour.
4. **Harness egress.** Route the LLM client (and future ownership checks) through the proxy;
   "Data shared externally" in the session summary gains the actual bytes sent to each provider.
5. **Monitoring.** Admin filters, the session summary's Network activity section, and alert rules.
6. **Optional:** TLS inspection per tool for evidence capture, and the L4 gateway for non-HTTP tools.

Phases 1 and 2 already deliver the core control: no tool can reach a host outside the list.

## Testing

- **Unit (`ScanHarness.Tests`):** every rule type; anchored and unanchored regexes; regex timeouts
  failing closed; IDN and look-alike hosts; trailing dots and ports; IP literals; each private
  range; engagement not approved, outside the window, or a different engagement's token; expired
  and tampered tokens; deny beating allow; a tool with no egress block getting nothing.
- **Integration (Docker Compose, in CI):** from inside a tool container,
  - an allowed host succeeds and is logged with the tool call id;
  - a host off the list gets a 403 and a Denied event;
  - a direct connection that ignores the proxy fails (no route), as does an external DNS lookup;
  - an allowed name resolving to `169.254.169.254` or `127.0.0.1` is denied;
  - a redirect from an allowed host to a denied one is denied at the second connection;
  - stopping the policy endpoint denies everything.
- **UI (`ScanHarness.UITests`):** a denied connection shows a red Egress pip on the reply and turns
  the session red.
- **Eval (`ScanHarness.Eval`):** add injection cases where a scanned page tells the tool to fetch
  an off-scope URL; the expected result is a Denied event, whatever the brain.

## Implementation notes

Phases 1–5 are implemented and were checked end to end against real sites with Docker Desktop on
Windows. Phase 6 (TLS inspection, the L4 gateway) is not. Where the code differs from the design above:

- **Lua, not `ext_authz`.** Envoy's HTTP `ext_authz` forwards the original method, which for an HTTPS
  tunnel is `CONNECT` with no path. Instead, a small Lua filter (`egress/envoy.yaml`) POSTs the method,
  target, proxy credential and request id to `/egress/authorize` as headers, and refuses with the
  policy's status (403/407), or with 502 when the policy can't be reached or answers anything else.
  Refusals carry an `x-egress-refused` header so a site's own 403 isn't mistaken for one.
- **Envoy connects to the checked address.** Instead of Envoy's dynamic forward proxy (which would resolve
  the name again), the route goes to an `ORIGINAL_DST` cluster that reads the upstream from
  `x-envoy-original-dst-host`. The Lua filter removes any client-supplied value and sets it only from the
  policy's answer, so the address that was checked is the address connected to. This closes the DNS
  rebinding gap rather than leaving it as a residual risk.
- **The policy endpoint lives in the harness** (`Services/Egress/EgressAuthorizer.cs`), mapped at
  `/egress/authorize`, outside HTTPS redirection, and protected by `Egress:PolicyKey`. The harness both
  mints and checks tokens, so the signing key can be generated per run. The proxy reaches it at
  `host.docker.internal:5285`; Envoy is told to prefer IPv4 there, because Docker Desktop lists an IPv6
  address first that container networks can't route.
- **The 407 challenge.** .NET (like most clients) only sends proxy credentials after the proxy asks for
  them, so a token-less request gets a 407 with `Proxy-Authenticate`, and the client retries with its token.
  Each tunnel therefore starts with a recorded challenge. They're kept (a tool that never answers one is
  worth knowing about), but the admin Network tab hides them unless "No token" is chosen.
- **Connections reach the session through the log.** After a tool call returns, `ToolCallService` reads the
  call's recorded connections and logs each one (Info if allowed, an Egress Block if refused), with the
  connection in the entry's `egress` data. The transcript, pips, session colour, session summary and
  reopened sessions all work from that, as with handoffs. The tool output's Network section reads the store
  directly, because traffic figures arrive later from the access log.
- **Stage.** `ControlStage.Egress` was added for pips and reports. `controls.json` can't use it: network
  rules live in `egress.json`, are always Block, and apply to everyone, so the Controls page lists them
  read-only.
- **Live scanner.** To have real traffic to control, `DemoToolAPI_1`'s scanner gained a live mode
  (`Scanner:LiveFetch`, on in the container only). It follows the first link to each host (up to
  `Scanner:FollowLinks`) rather than the first N links, so links that leave the site are exercised.
- **Harness LLM traffic** goes through the proxy only for non-loopback endpoints with
  `Egress:RouteLlmTraffic` on. A local Ollama is called directly: the proxy couldn't reach it from its
  container, and nothing leaves the machine. The harness isn't on the internal network itself, so for it
  this is advisory: checked and logged, not enforced.
- **Not done yet:** alert rules (outbound-heavy connections, bursts of refusals). The data is in
  `egress_events` with byte counts; only views exist. Also not done: an Eval case for injected off-scope
  fetches, and running `scripts/test-egress.ps1` in CI (it needs Docker and a running harness).
- **Tested** by unit tests (`EgressPolicyTests`, `EgressIntegrationTests`, `LiveScannerTests`), UI tests
  (`EgressUITests`), and `scripts/test-egress.ps1` against the real containers. That script covers:
  no external DNS, no direct route, a 407 without a token, a 403 for a forged token, the metadata address
  refused, and refusals marked by the proxy. Failing closed (policy endpoint down gives 502) was checked by
  hand.

## Alternatives considered

- **Check URLs only in the harness (today's `ScopeGuard`).** Necessary but not sufficient: it sees
  the argument, not what the tool does with it.
- **Set `HTTP_PROXY` and trust the tools to use it.** Rejected as the control (any tool can ignore
  it); kept as the advisory mode for local runs.
- **Host firewall rules only (Windows Firewall or iptables by IP).** Works for a fixed list but
  can't follow DNS for CDN-hosted targets, can't tell tool calls apart, and logs little. Kept for
  the L4 gateway and for pinning tools to the proxy on bare-metal runs.
- **A commercial secure web gateway or cloud egress firewall.** Reasonable in production (and the
  policy endpoint could feed one), but it ties the demo to a vendor and can't see tool call ids.

## Open questions

- Should the policy endpoint live in the harness, or in a separate service so the harness isn't on
  the tools network?
- Is TLS inspection acceptable to customers for evidence capture, and how long would captured
  traffic be kept?
- Should a denial stop the tool call immediately (the harness cancels it) or let it finish and
  report?
- Do engagements need per-domain port lists (e.g. 8443) and IP ranges, which the validator refuses
  today?
- Who may edit `egress.json` rules once there's real authentication: admin only, with changes
  logged like the Permissions tab?
