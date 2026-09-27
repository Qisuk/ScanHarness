# CLAUDE.md

Guidance for working in this repository. Derived from `copilot-instructions.md`.

## Project: ScanHarness

ScanHarness is a demonstration of how an AI assistant calls tools. It lets a user
interact with the assistant to perform **scanning** and **simulated attacks**
against a given URL, and to view a log of all tool calls through a UI.

The harness should be **modular and extensible** so new tools and functionality
can be added later. It must support a number of different **LLMs** as the "brain"
of the assistant, so users can select the model that best suits their needs.

> Note: The scanning/attack tools are a **simulation**. They return canned dummy
> responses from a local JSON file rather than performing real network activity.

## Tool-calling behavior

- On each user request, the assistant checks whether a valid tool exists for the task.
  - If a valid tool is found, it calls the tool and logs the request.
  - If no valid tool is found, it tells the user no suitable tool is available.
- After every tool call, the assistant reports **success or failure** to the user,
  including any error messages or relevant information returned by the tool.
- **Attack confirmation:** any time the `Attack` tool is called, prompt the user to
  confirm the action before proceeding, since it may have significant effects.

## Logging

- Every tool call is written to a **log file** for auditing and debugging.
- Each log entry includes: timestamp, tool called, parameters passed, and the
  response received.
- The log is viewable through a **UI served via an API endpoint**, with filtering
  and searching by criteria such as timestamp, tool name, or response status.

## DemoToolAPI_1

Provides two sets of endpoints:

- **SimpleScanner** — scans a given URL and returns a JSON list of links found on
  the site (link text + URL). Returns one of **10 dummy responses** (success,
  failure, and error messages), each accompanied by an array of links.
- **Attack** — performs a simulated attack on a given URL and returns JSON with the
  attack status and relevant information. Returns one of **10 dummy responses**
  (success, failure, and error messages).

Demo responses are stored in a local `.json` file included in the project. The
assistant reads responses from this file and returns them when a tool is called.

## Deliverables & standards

- **README** for every project: overview, setup/run instructions, and other
  relevant information — clear and concise.
- **Unit tests** covering successful tool calls, failure cases, and error handling.
- **Playwright tests** verifying the UI.
- Follow best practices for coding, testing, and documentation: clear descriptive
  names, modular and reusable code, and thorough documentation of functions and
  classes.
