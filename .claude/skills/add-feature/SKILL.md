---
name: add-feature
description: Implement a tinyprobe feature end to end (format/engine/CLI, tests, docs, examples, VS Code extension impact) on its own branch, verified with scripts/dev.sh check. Use when asked to add or change a tinyprobe capability.
---

# Add a tinyprobe feature

Arguments: a roadmap task id (`M1-7`) or a short description of the feature.

Find the task in `ROADMAP.md`. If it changes the YAML format, the CLI surface or the protocol, it needs an *Approved* spec in `docs/specs/`. Without one, run `/spec` first and stop: the format is a public contract read by people and coding agents. With one, the spec is the source of truth. If implementing shows it is wrong, update the spec and ask, don't silently diverge. For a task outside the roadmap, ask whether to add it first.

## 1. Design

- Write the user-facing example first: the YAML a user writes, or the command line, and the output (console and, if relevant, ndjson / `inspect --json`).
- Keep the format flat and predictable for LLMs: one obvious way to write things, snake_case keys, values typed like YAML.
- Decide the validation errors: each one has a `file:line:column`, a clear message, and a "did you mean" when it is a typo.
- Check `docs/workflow-format.md` "Not supported yet": the feature may already be listed there.

## 2. Branch

`git switch -c feature/<short-name>` from an up-to-date `main` (`fix/`, `docs/`, `perf/`, `ci/` for other kinds).

## 3. Implement (see CLAUDE.md for the map)

1. Model in `src/Tinyprobe.Core/Definitions/Definitions.cs`.
2. Parsing and validation in `src/Tinyprobe.Core/Parsing/WorkflowLoader.cs`: add the key to the right keys array, parse it, collect problems (don't throw on the first one).
3. Behavior in `src/Tinyprobe.Core/Execution/`.
4. Results: `Models/WorkflowReport.cs`, `Protocol/ProtocolJson.cs` (new keys only; never remove or rename), `src/Tinyprobe.Cli/ConsoleReporter.cs`.
5. New command or option: `src/Tinyprobe.Cli/Program.cs`, including the `Usage` text, which `docs/cli.md` copies.
6. AOT: no reflection, no reflection-based JSON. Hand-written JSON with `Utf8JsonWriter` like the existing code.

## 4. Tests

- Loader: the valid form, and each invalid form with its exact message and location (`tests/Tinyprobe.Core.Tests/Parsing`).
- Engine: behavior against `FakeServer` (`tests/Tinyprobe.Core.Tests/Execution`), asserting the requests sent and the report.
- Protocol: `tests/Tinyprobe.Core.Tests/Protocol/ProtocolJsonTests.cs` when JSON changes.
- End to end: extend `docs/example/workflows/*.yaml` when the test server can exercise the feature; add a route to `tests/Tinyprobe.RealServer.Tests/Program.cs` if needed. Workflows must leave the server's data as they found it.

## 5. Docs

- `docs/workflow-format.md`: reference section; remove the item from "Not supported yet".
- `docs/llm-guide.md`: how an agent should use it, and a "Common mistakes" entry if it is easy to get wrong. It is embedded in the binary (`tinyprobe guide`).
- `README.md`: the steps table and the examples; `docs/cli.md`: the usage block, CI and the other command-line reference.
- `docs/protocol.md` for any new JSON key.
- Every ```yaml block in the docs is loaded by `DocumentationTests`: it must be valid.

## 6. VS Code extension (`../tinyprobe-vscode`)

Ask: should the extension show it (run view, diagnostics, CodeLens, call prompts) or pass it (new CLI option)? If yes, do it on a branch with the same name there, put protocol-only logic in `src/protocol.js` with unit tests, raise `MINIMUM_VERSION` if the extension depends on the new binary, update its README and CHANGELOG, then run `../tinyprobe-vscode/scripts/dev.sh check`.

## 7. Verify and hand over

- `scripts/dev.sh check` must pass. Report the test counts and anything skipped.
- Mark the task `[x]` in `ROADMAP.md` (with the PR link once there is one) and set the spec to *Implemented*.
- Commit with an imperative title and a body that says what and why. Do not push or open a PR unless asked; when asked, the PR body describes the user-facing change with a YAML example.
