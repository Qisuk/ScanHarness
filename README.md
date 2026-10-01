# ScanHarness

A demo of how an AI assistant calls tools. You ask the agent in plain language to scan a URL,
run a (simulated) attack against it, get a weather forecast, or ask other **agents** to analyse the
findings or estimate the cost; a pluggable **brain** decides which tool or agent to call, attacks
wait for your approval, scans and attacks only run inside an approved engagement's scope, and
every call is logged and shown in the UI.

The tools are simulations and never touch the network: the scanner and attack return canned
responses from `DemoToolAPI_1/dummy_responses.json`, and the weather forecast is random.

| Tool | API (`DemoToolAPI_1`) | Approval |
|---|---|---|
| Scanner (`url`) | `GET /api/tools/simple-scanner` | no |
| Attack (`url`) | `POST /api/tools/attack` | **yes** |
| Weather forecast (optional `location`) | `GET /WeatherForecast` | no |

Tools are defined in `ScanHarness/tools.json` and agents in `ScanHarness/agents.json`; add one
there and every brain can use it (see [Tools, agents and trust boundaries](#tools-agents-and-trust-boundaries)).

## Projects

| Project | What it is |
|---|---|
| `ScanHarness` | Blazor Server UI and the agent: brains, tool calling, approval, logging. |
| `DemoToolAPI_1` | Simulated tool API (scanner, attack, weather forecast) the harness calls. |
| `ScanHarness.Tests` / `DemoToolAPI_1.Tests` | xUnit unit tests. |
| `ScanHarness.UITests` | Playwright UI tests (keyword brain, plus a built-in fake reasoning model; never a real LLM). |
| `ScanHarness.Eval` | Compares brains/models on a fixed prompt set. |

Design docs live in `docs/design`, for example
[multi-agent orchestration and trust boundaries](docs/design/multi-agent-boundaries.md).

## Running

1. Start the tool API: `dotnet run --project DemoToolAPI_1 --launch-profile https` (serves `https://localhost:7031`).
2. Start the harness: `dotnet run --project ScanHarness` and open `/harness`.
3. Before scanning or attacking, agree an engagement: on the **Scoping** page describe the customer,
   domains and dates (e.g. *"We are Acme Ltd. Please test example.com from 2026-10-01 to 2026-12-31."*),
   submit it, switch to **admin** and approve it, then pick it in the Harness's **Engagement** menu.
   (Set `Engagements:EnforceScope` to `false` to skip this.)

Or open `ScanHarness/ScanHarness.slnx` in Visual Studio and start both projects.

## Chat and tool output

The **Main** tab is a chat with the agent. Your questions appear on the right and the agent's
replies on the left. Each reply shows the brain's decision, the tool it ran with that call's status,
and the summary of the result, and the chat stays scrolled to the newest message.

Press **Enter** to send and **Shift+Enter** for a new line (the input grows up to six lines).
Enter does nothing while the agent is busy or the message is empty, and while an input method
is composing text it confirms the text instead of sending. **↑** and **↓** recall earlier prompts
when the message is a single line.

The **Tool output** pane below shows the latest tool call. Select any reply that ran a tool (click
it, or focus it and press Enter or Space) to show that request's output instead. The pane's
border then reads *selected request*, with **Show latest** to go back. The next request switches
back to the latest call automatically.

The chat is built from the session's logged entries and tool calls (`Services/ChatTranscript.cs`),
so a reopened session shows the same conversation.

## Controls (guardrails)

The **Controls** page (in the menu) lists guardrails, grouped by where they apply:

| Stage | Applied to | Block means |
|---|---|---|
| **Chat In** | Your message, before it's passed to the brain | It's never sent to the brain |
| **Chat Out** | The brain's reply, each action summary and the session summary | The text is withheld (the original stays in the log data) |
| **ToolRule** | The tool or agent call the brain chose, before it runs | It isn't run |
| **ToolOut** | A tool's or agent's result, before any brain reads it | Model brains never see it; the rule-based summary is used |

ToolRule also runs two built-in checks that are always Block: the [engagement scope](#engagements-and-scope)
for tools that need one, and whether the tool is enabled on the [Permissions tab](#tools-agents-and-trust-boundaries).
ToolOut exists because tool output is controlled by the target: a scanned page can contain text aimed at
the brain. The shipped ToolOut control flags instruction-like text in results.

Each control is **Off** (no action), **Advise** (an orange pip on the message and a **Warning**
log entry) or **Block** (a red pip, a **Block** log entry, and the message is stopped as above).
Click a pip to see which control(s) triggered and what they found. Settings are saved per user
in the database and changes are logged.

### Defining controls

Controls are read at startup from **`ScanHarness/controls.json`** (set by `Controls:DefinitionsPath`,
relative to the app's content root). Add a control there and it appears on the Controls page and
is applied at its stage:

```json
{
  "controls": [
    {
      "id": "chat-in-ignore-previous",
      "stage": "ChatIn",
      "name": "Contains \"ignore previous\"",
      "description": "Flags a message that tries to override earlier instructions.",
      "defaultMode": "Block",
      "rule": { "type": "regex", "value": "\\bignore\\s+previous\\b" }
    }
  ]
}
```

- **stage**: `ChatIn`, `ChatOut`, `ToolRule` or `ToolOut`. **defaultMode**: `Off`, `Advise` or `Block`, used
  until a user changes it.
- **rule.type**: `contains` (plain text) or `regex` (a .NET regular expression).
  **rule.caseSensitive** defaults to `false`.
- **appliesTo**: `Any` (the default), `Internal` or `External`. It limits the control to
  [internal or external](#internal-and-external) subjects: for Chat In, the brain the message is
  sent to; for Chat Out, the brain that wrote the text (the decision brain for replies, the summary
  brain for summaries); for ToolRule, the tool about to run. For example, a Chat In control with
  `"appliesTo": "External"` checks only messages going to an external model. When a subject's
  classification isn't known, the control is applied anyway (it fails closed).
- **id** keys each user's saved setting, so keep it stable when editing a control.
- A regex that takes too long on a message (over 250 ms) counts as a match, so a slow
  pattern can't let text through unchecked.
- The file is validated at startup. If anything is wrong (a bad regex, a duplicate id, a missing
  field, an unknown stage or mode) the app won't start, and the error lists every problem.

The shipped controls are Chat In *Contains "BadInstruction"* (exact text), Chat In
*Contains "ignore previous"* (a regex, any case or spacing), Chat Out *Contains "&lt;script&gt;"*
(any case) and ToolOut *Instructions in tool output* (a regex), all defaulting to Block and applying
to any brain. There are no configurable ToolRule controls yet (the built-in scope and enabled checks always run).

The Controls page shows what each control applies to (e.g. "Applies to external brains"), and lists
each tool's classification in the ToolRule box. Pips and log entries name the classification
of the subject, e.g. "On the request to an external brain".

### Internal and external

Every brain and tool is classified **internal** (runs on infrastructure you control) or
**external** (a third-party service), and controls can be scoped by it (see `appliesTo` above).

- **Brains**: the keyword brain is internal. LLM brains are internal when `Llm:BaseUrl` is a
  loopback address (e.g. a local Ollama) and external otherwise. `Llm:Classification` sets
  all models from the endpoint, and `Llm:ModelClassifications` sets individual models by model id.
  The brain menus show each brain's classification, e.g. "qwen2.5:3b · internal".
- **Tools**: each tool's `classification` is set in `tools.json` (`simple_scanner` and `attack`
  internal, `weather_forecast` external). Override them in `Tools:Classifications`, keyed by tool
  function name. The tool chip on each reply shows the classification.
- **Agents**: follow the brain that runs them (rule-based agents are internal).
- Any of these can also be changed at runtime on the **Permissions** tab (below).

```json
"Llm":   { "Classification": "External", "ModelClassifications": { "llama3.2:1b": "Internal" } },
"Tools": { "Classifications": { "weather_forecast": "Internal" } }
```

## Tools, agents and trust boundaries

The harness follows [docs/design/multi-agent-boundaries.md](docs/design/multi-agent-boundaries.md):
every brain, tool and agent has a trust policy, and every time data crosses from one to another it
goes through one broker (`Services/Boundaries/BoundaryBroker.cs`), which applies the policy and
records the handoff.

### Defining tools and agents

- **`ScanHarness/tools.json`**: each tool API the brains may call: its description, parameters,
  confirmation text, HTTP method and path (relative to `Tools:BaseUrl`), whether it needs approval
  or an engagement scope, and its classification, output label and output trust. Adding an HTTP
  tool is a JSON edit; LLM brains are offered it straight away, and `keywords` let the keyword
  brain choose it.
- **`ScanHarness/agents.json`**: each agent: its kind (which fixes its input and output contract),
  its brain (`rule-based` or an LLM model id), clearance, output trust, whether it needs approval,
  and whether the harness's brain may call it. Agents offered to the harness appear as tools.

Both files are validated at startup like `controls.json`: the app won't start with a broken file,
and the error lists every problem.

The shipped agents follow the harness's pattern: each has a rule-based implementation and can
instead run on an LLM, falling back to the rule-based answer (and saying why) if the model fails.

| Agent | Called from | Receives (built by code, never by a model) | Returns |
|---|---|---|---|
| **Findings analyst** (`analyse_findings`) | The harness ("analyse the findings", "recommend fixes") | The session's scan and attack results | Prioritised recommendations; a model's answer is checked against the input and anything not found there is dropped |
| **Cost estimator** (`estimate_cost`) | The harness ("what would it cost?") | Host count and findings by severity, with no hostnames | Days and total from a fixed formula (`Agents:DayRate`); a model may only write the narrative |
| **Scoping agent** | The **Scoping** page only | The customer conversation | A scope proposal, which code validates and admin approves |

### Permissions

The **Permissions** tab on the Controls page lists every brain, tool and agent with:

- **Hosting**: internal or external.
- **Clearance**: the most sensitive data it may receive (`Public` < `Engagement` <
  `CustomerConfidential` < `Secret`). Defaults: customer confidential for internal components,
  engagement for external ones.
- **Output**: how sensitive its results are, and whether they're **trusted**. Model output and
  anything a scanned target can influence are untrusted: shown, but never able to cause an action
  on their own.
- **Enabled** (tools and agents): a disabled one is blocked before it runs.
- **Brain** (agents): switch an agent between rule-based and any served model.

For the demo, everything on the tab is editable by any user. Edits apply to everyone immediately,
are saved in the database and logged, and each row (or everything) can be reset to the defaults.

When data reaches something not cleared for it, the broker:

- **shares** it as it is when the receiver is cleared;
- **pseudonymises** customer-confidential data for a receiver cleared for engagement data:
  hostnames, emails and IP addresses become `HOST_1`, `EMAIL_1`, `IP_1`, and the answer is mapped
  back before anyone sees it (for example, the summary brain or an agent running on an external model);
- otherwise **blocks** it: the rule-based summary is used, or the agent doesn't run.

### Engagements and scope

Scans and attacks (tools with `requiresScope`) only run against a domain in the session's
**engagement**, inside its testing window. No model can change that; it is checked in code.

1. On the **Scoping** page, play the customer and tell the scoping agent who you are, the domains,
   the dates and any rules. It drafts a proposal (untrusted), and code checks it: no wildcards, IP
   addresses or private names, and each domain's ownership must be verified. Domains in
   `Engagements:VerifiedDomains` (and their subdomains) count as verified; for any other domain the
   page shows the DNS TXT record the customer would publish and a **Check record** button. The check
   is simulated, like the demo tools: it always passes, and is saved and logged.
2. **Submit for approval** records it as *Proposed*. Domains that failed a check are left out, with the reason.
3. **admin** approves or rejects it. Approved engagements never change: a new scope is a new version.
4. On the Harness page, pick the engagement in the **Engagement** menu. A scan or attack outside its
   domains or window is blocked, with the reason in the chat and the log.

Set `Engagements:EnforceScope` to `false` to turn the scope check off (the UI tests do).

### Audit

Each handoff is logged with what was offered, its label, the receiver's hosting and clearance, and
whether it was shared, pseudonymised (with how many placeholders, never the values) or blocked.
Blocked handoffs count as blocks for the session's colour. The session summary has a **Data shared
externally** section listing every handoff to an external component.

| Setting | Default | Meaning |
|---|---|---|
| `Tools:BaseUrl` | `https://localhost:7031` | The tool API (DemoToolAPI_1). |
| `Tools:DefinitionsPath` / `Agents:DefinitionsPath` | `tools.json` / `agents.json` | Definition files, relative to the content root. |
| `Agents:MaxDepth` | `2` | How deep calls may chain. |
| `Agents:TimeoutSeconds` | `120` | Longest an agent's model may take before the rule-based answer is used. |
| `Agents:DayRate` / `Agents:Currency` | `1200` / `GBP` | Used by the cost estimator. |
| `Engagements:EnforceScope` | `true` | Scans and attacks need an approved, in-window engagement. |
| `Engagements:VerifiedDomains` | `example.com`, `example.org`, `acme.test` | Domains whose ownership counts as verified (simulated). |

## Agent brains

A brain has two jobs, each picked from its own menu at the top of the harness page:

- **Decide**: choose which tool to call (and its arguments) for your request.
- **Summary**: after every action (completed, failed, or rejected at the approval step), summarise
  what you asked for and what the tool returned, e.g. *"The user asked me to run a simulated attack
  against https://test.example.com. The attack tool found 8 high-severity vulnerabilities, including
  Remote code execution risk…"*. Defaults to **Same as decision**; pick a different brain to, say,
  decide with the instant keyword brain and summarise with an LLM.

Summaries are shown under the agent's reply and in the tool output, and logged. The keyword brain
builds its summary from the tool's response with fixed rules; an LLM brain writes its own from
the same response, is told to use only facts in it, and falls back to the rule-based summary
(logging why) if the model fails.

**Summarise session** (next to the tabs) opens the **Session** tab with a report on everything
done so far:

- **Session summary**: a summary of all the per-action summaries, written by the brain selected
  in the **Summary** menu and capped at 500 words (enforced in code, whatever the brain returns).
  LLM brains fall back to the rule-based summary if the model fails. A row of pips shows the
  controls tripped in the session: one per warning and block (up to 3 of each, then "+N more"),
  each opening its details.
- **Controls tripped**: every control that fired, in order, with its action (Warning or Block),
  the control and stage, and the message that tripped it.
- **Tools called**: every tool call in order, with its time, parameters, status and brain.

The two lists are built directly from the session's log, so the same session always gives the
same lists. All three are also logged.

Available brains:

- **Keyword brain**: rule-based, instant, always available. Used as the fallback.
- **LLM brains**: one per model served by an **OpenAI-compatible** endpoint (`/v1/models`,
  `/v1/chat/completions`). Ollama is the default, but LM Studio, llama.cpp's server, vLLM,
  Docker Model Runner or a hosted API work by changing the base URL.

The model only chooses a tool and its arguments. Whether a tool needs approval comes from
`tools.json`, so an LLM can't skip the attack confirmation, and any URL or
location the user didn't actually type is discarded rather than acted on.

### Reasoning

When a brain or agent runs on a **reasoning model** (e.g. `qwen3`, `deepseek-r1`), its thinking is
logged with the action it belongs to: the decision, any agent's work, and the summary. Providers
return reasoning in different ways (a `reasoning_content` or `reasoning` field, or `<think>` blocks
in the answer), and `Services/Llm/LlmReasoning.cs` gathers them into one place and removes think
blocks from the answer itself.

- **Drill down**: a reply with reasoning shows a collapsed **Reasoning (n steps)** section, and the
  tool output pane shows the reasoning behind the selected action, step by step (stage, brain, time).
- **Saved**: each step is a `Reasoning` log entry, linked to the request and (for summaries and
  agents) to its tool call, so reopened sessions show it too. The activity feed shows a one-line preview.
- **Checked**: reasoning is model output shown to users, so Chat Out controls check it; a blocked
  step is withheld from display but kept in the log. Reasoning from an external brain given
  placeholders has them mapped back.

Models that don't reason (like `llama3.1`) produce none, so their replies have no Reasoning section.
The scoping agent's and the admin activity summary's reasoning is logged, but only shown in the activity feed.

## Themes

The app opens in **dark mode**. Change it with the **Theme** menu at the bottom of the sidebar:
Dark, Light, or System (follows your operating system). The choice is remembered per browser
(`localStorage`). Colours are defined once as CSS variables in `wwwroot/app.css`, with the dark
values under `:root[data-theme="dark"]`; `wwwroot/theme.js` applies the theme before the page is
drawn and also switches on Bootstrap's own dark mode (`data-bs-theme`).

## Users and sessions

Pick a **User** at the top of the harness: `chris`, `raccoon`, `blob` or `admin`. This is a
placeholder until proper authentication replaces it; it isn't a security boundary yet.

- Each user sees only their **own** sessions. The server enforces this when a session is opened,
  not just the UI.
- **admin** sees every user's sessions, and every entry shows the user who performed it (the
  request's label, a tag on other feed entries, and a User column in History and the session tool
  list). If admin acts in another user's session, those entries are recorded as admin.

The page starts on a **new session**. The **Session** menu lists your saved sessions (newest first,
titled by their first request). Opening one recreates it: the activity feed, tool calls and
history, the last tool output, the agent's reply and summary, and prompt history. You can carry on
working in it. **New** starts a fresh session.

Sessions are coloured by the most severe control tripped in them: **green** none, **amber** a
warning, **red** something was blocked. Their labels also give the counts (e.g. "2 warnings ·
1 block"), so the colour isn't the only signal.

### All activity (admin)

Admin opens on the **All activity** tab: every request by every user, newest first, with filters for
**user**, **session**, a **from/to** time range and **controls** (any, none tripped, warning,
block, or warning or block). For example: everything in a timeframe, everything one user did in a
timeframe, or every request that raised a warning, by anyone. Each row shows the request, its
tool call and status, and the controls it tripped (with a pip); rows are edged amber or red by
their most severe control. Select a row to open its session with that request's output shown.
The view shows the newest 500 matches and says when there are more. Times are entered in the
browser's local time, which matches the server while the app runs locally.

**Summarise activity** (in the results box) summarises everything matching the current filters,
i.e. the selected window, not just the rows shown. It shows the window, an overview (requests,
users, tool calls, warnings and blocks) and a summary under 500 words written by the brain
selected in the **Summary** menu (rule-based for the keyword brain; LLM brains fall back to it on
failure, and get at most the 200 most recent requests). Content a control blocked or withheld is
never passed to the summarising brain, only the fact that it was blocked. The summary itself is
checked by Chat Out controls, and the request and summary are logged in the admin's session.

## Persistence

Everything is saved to a local **SQLite** database (`ScanHarness/Data/scanharness.db`, set by
`Persistence:DatabasePath`; database files are git-ignored), and loaded from it when the app starts:

| Table | Holds |
|---|---|
| `sessions` | Owner, start time, last activity, title |
| `tool_calls` | Tool, action, parameters, status, the tool's full response, approval decision and who made it, error, summary, and every timestamp |
| `log_entries` | Every activity feed entry with its user, brain and timestamp, plus structured data (the prompt, the agent's decision, summaries) |

Together these are enough to recreate a session exactly. Calls left waiting or running when the
app stopped are marked **interrupted** at the next start, with an entry in that session's timeline.

Storage sits behind `IActivityStore` (`Services/Persistence`), so switching to another database
means writing one new implementation and changing its registration in `Program.cs`.

## Logging

Every request, decision, approval, tool call and summary is recorded with the **brain** that handled it:
shown on your requests in the activity feed, in the History tab's Brain column and in the tool
output. Besides the database, an audit log (`logs/toolcalls-<date>.log` next to the app binaries) is
tab-separated: `timestamp  level  session-id  user  tool-call-id  brain  message`, with `-` for
empty columns.

### Local LLM with Docker

```powershell
docker compose up -d            # Ollama on http://localhost:11434
./scripts/pull-models.ps1       # qwen2.5:3b, llama3.2:3b, llama3.2:1b
```

The container runs on the CPU unless you enable the NVIDIA section in `docker-compose.yml`.
On CPU, stick to models of roughly 3B parameters or smaller for responsive decisions; 7–8B models work but take several seconds each.
If Ollama is also installed natively (it already uses port 11434), set `$env:OLLAMA_PORT=11435`
before `docker compose up` and point `Llm:BaseUrl` at that port.

### Configuration (`ScanHarness/appsettings.json`, section `Llm`)

| Setting | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | `false` offers only the keyword brain. |
| `BaseUrl` | `http://localhost:11434/v1` | OpenAI-compatible base URL, including `/v1`. |
| `ApiKey` | empty | Bearer token for hosted APIs (use user secrets, not appsettings). |
| `DefaultModel` | empty | Decision brain when the page opens (model id or `keyword`); empty starts on the keyword brain. |
| `DefaultSummaryModel` | empty | Summary brain when the page opens (model id or `keyword`); empty uses the decision brain. |
| `DecisionMode` | `JsonSchema` | `JsonSchema` (structured output; most accurate in our evals and works with any chat model) or `Tools` (OpenAI function calling). |
| `Temperature` | `0` | Keeps decisions repeatable. |
| `TimeoutSeconds` | `180` | Per decision; the first call to a model includes loading it. |
| `ExcludeModelPatterns` | `["embed"]` | Hides models whose id contains these (e.g. embedding models). |
| `Classification` | empty | `Internal` or `External` for this endpoint's models; empty decides from `BaseUrl` (loopback is internal). |
| `ModelClassifications` | `{}` | Per-model overrides of `Classification`, keyed by model id. |

Any setting can be overridden with environment variables, e.g. `Llm__BaseUrl=http://localhost:1234/v1`.

## Comparing models

```powershell
dotnet run --project ScanHarness.Eval                          # every model, JSON mode (the app default)
dotnet run --project ScanHarness.Eval -- --mode both --repeat 3
dotnet run --project ScanHarness.Eval -- --models qwen2.5:3b,llama3.2:3b --base-url http://localhost:1234/v1
```

Each model runs the prompts in `ScanHarness.Eval/eval-cases.json` (scan, attack, missing URL,
off-topic and tricky phrasings). The eval prints accuracy and median/p95 latency next to the keyword
baseline, and writes a Markdown report with every miss to `TestResults/`. Add cases to the
JSON file as you find prompts that trip models up.

## Tests

```powershell
dotnet test ScanHarness.Tests          # unit tests; LLM calls use a fake HTTP handler
dotnet test ScanHarness.UITests        # Playwright; run `pwsh bin/Debug/net10.0/playwright.ps1 install` once
```

## CI and security testing

GitHub Actions workflows in `.github/workflows` run on every pull request and push to `master`:

| Workflow | Job | What it does | Fails the run on |
|---|---|---|---|
| `ci.yml` | Build and test | Builds `ScanHarness/ScanHarness.slnx` and runs the unit and Playwright UI tests | A build error or failing test |
| `security.yml` | SAST · Semgrep | Semgrep's C#, OWASP Top 10 and secrets rulesets | An ERROR (high) severity finding |
| | SAST · .NET analyzers and packages | Builds with every .NET security analyzer rule on, and checks NuGet packages (including transitive) for known vulnerabilities | A package with a High or Critical advisory |
| | DAST · ZAP baseline | Publishes and starts the ScanHarness UI (keyword brain, throwaway database) and runs an OWASP ZAP baseline (passive) scan against it | A High-risk alert, or a rule set to `FAIL` in `.zap/rules.tsv` |
| `codeql.yml` | SAST · CodeQL | GitHub CodeQL `security-extended` queries for C# | Nothing by itself (see below) |

`security.yml` also runs every Monday at 06:00 UTC, to catch newly published vulnerabilities.

Findings below the fail threshold are listed in each job's **summary** and don't block. The full
reports are attached to the run as artifacts: `semgrep-report`, `dotnet-sast-report` (SARIF per
project, plus the package check) and `zap-report` (HTML, JSON and Markdown, plus the app's log).
.NET analyzer rules have no severity, so their findings are always reported and never gate the run.

**Tuning:** to ignore or escalate a ZAP rule, add it to `.zap/rules.tsv` (`IGNORE`, `WARN` or
`FAIL`). To suppress a reviewed analyzer finding, use a `#pragma` or `.editorconfig` entry with a
justification. The security analyzers only run when the build is given `-p:SastSarifDir=...`
(`Directory.Build.props`), so local builds are unchanged.

**GitHub Code Security:** this repository is private, so the Security tab and CodeQL need GitHub
Code Security enabled. Once it is, set the repository variable `CODE_SECURITY_ENABLED` to `true`
(Settings › Secrets and variables › Actions › Variables). CodeQL then runs, and Semgrep and the
.NET analyzers also send their SARIF results to the Security tab. To block pull requests on
CodeQL alerts, add a code scanning rule to a branch ruleset for `master`.
