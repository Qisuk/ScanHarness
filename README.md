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

## Agent brains

Pick the brain from the **Brain** menu at the top of the harness page:

- **Keyword brain**: rule-based, instant, always available. Used as the fallback.
- **LLM brains**: one per model served by an **OpenAI-compatible** endpoint (`/v1/models`,
  `/v1/chat/completions`). Ollama is the default, but LM Studio, llama.cpp's server, vLLM,
  Docker Model Runner or a hosted API work by changing the base URL.

The model only chooses a tool and its arguments. Whether a tool needs approval comes from
`Services/ToolCatalog.cs`, so an LLM can't skip the attack confirmation, and any URL or
location the user didn't actually type is discarded rather than acted on.

## Logging

Every request, decision, approval and tool call is recorded with the **brain** that handled it:
shown on your requests in the activity feed, in the History tab's Brain column and in the tool
output. The audit log (`logs/toolcalls-<date>.log` next to the app binaries) is tab-separated:
`timestamp  level  tool-call-id  brain  message`, with `-` for empty columns.

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
| `DefaultModel` | empty | Model selected when the page opens; empty starts on the keyword brain. |
| `DecisionMode` | `JsonSchema` | `JsonSchema` (structured output; most accurate in our evals and works with any chat model) or `Tools` (OpenAI function calling). |
| `Temperature` | `0` | Keeps decisions repeatable. |
| `TimeoutSeconds` | `180` | Per decision; the first call to a model includes loading it. |
| `ExcludeModelPatterns` | `["embed"]` | Hides models whose id contains these (e.g. embedding models). |

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
