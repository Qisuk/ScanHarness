# ScanHarness

A demo of how an AI assistant calls tools. You ask the agent in plain language to scan a URL,
run a (simulated) attack against it, or get a weather forecast; a pluggable **brain** decides
which tool to call, attacks wait for your approval, and every call is logged and shown in the UI.

The tools are simulations and never touch the network: the scanner and attack return canned
responses from `DemoToolAPI_1/dummy_responses.json`, and the weather forecast is random.

| Tool | API (`DemoToolAPI_1`) | Approval |
|---|---|---|
| Scanner (`url`) | `GET /api/tools/simple-scanner` | no |
| Attack (`url`) | `POST /api/tools/attack` | **yes** |
| Weather forecast (optional `location`) | `GET /WeatherForecast` | no |

Tools are defined in `ScanHarness/Services/ToolCatalog.cs`; add a tool there and in
`ToolCallService` and every brain can use it.

## Projects

| Project | What it is |
|---|---|
| `ScanHarness` | Blazor Server UI and the agent: brains, tool calling, approval, logging. |
| `DemoToolAPI_1` | Simulated tool API (scanner, attack, weather forecast) the harness calls. |
| `ScanHarness.Tests` / `DemoToolAPI_1.Tests` | xUnit unit tests. |
| `ScanHarness.UITests` | Playwright UI tests (always use the keyword brain). |
| `ScanHarness.Eval` | Compares brains/models on a fixed prompt set. |

## Running

1. Start the tool API: `dotnet run --project DemoToolAPI_1 --launch-profile https` (serves `https://localhost:7031`).
2. Start the harness: `dotnet run --project ScanHarness` and open `/harness`.

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
| **ToolRule** | The tool call the brain chose, before the tool runs | The tool isn't run |

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

- **stage**: `ChatIn`, `ChatOut` or `ToolRule`. **defaultMode**: `Off`, `Advise` or `Block`, used
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
*Contains "ignore previous"* (a regex, any case or spacing) and Chat Out *Contains "&lt;script&gt;"*
(any case), all defaulting to Block and applying to any brain. There are no ToolRule controls yet.

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
- **Tools**: `simple_scanner` and `attack` are internal, and `weather_forecast` is external by
  default. Override them in `Tools:Classifications`, keyed by tool function name. The tool chip on
  each reply shows the classification.

```json
"Llm":   { "Classification": "External", "ModelClassifications": { "llama3.2:1b": "Internal" } },
"Tools": { "Classifications": { "weather_forecast": "Internal" } }
```

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
`Services/ToolCatalog.cs`, so an LLM can't skip the attack confirmation, and any URL or
location the user didn't actually type is discarded rather than acted on.

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
