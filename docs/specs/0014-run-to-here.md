# 0014 — Run to here

- **Status:** Implemented
- **Roadmap:** M4-10
- **Repositories:** tinyprobe, tinyprobe-vscode
- **PRs:** [#31](https://github.com/kikutano/tinyprobe/pull/31), [tinyprobe-vscode#6](https://github.com/kikutano/tinyprobe-vscode/pull/6)

## Problem

A single call is stateless ([decision 0003](../decisions/0003-endpoints-declare-workflows-run.md)): `tinyprobe call` and ▶ Call remember nothing. To look at the response of the fourth step of a workflow, the one that needs the token and the order id of the steps before it, a user has two options today:

- run the whole workflow and dig the step out of the report;
- or edit the workflow to cut it short.

Postman users are used to sending requests one by one, with the state the previous ones left. Decision 0003 rules that out, and promised "Run to here" instead: replay the steps before, every time, so nothing is hidden.

## Proposal

### CLI

```bash
tinyprobe run tinyprobe/workflows/orders.yaml --to 4 -e tinyprobe/env.local.yaml
tinyprobe run tinyprobe/workflows/orders.yaml --to "Create an order" -e tinyprobe/env.local.yaml
```

```text
  ✓ Log in › Log in        POST   200    35.10ms  /api/login
  ✓ List products          GET    200    12.40ms  /api/products
  ✓ Add to cart            POST   201    20.02ms  /api/cart
  ✓ Create an order        POST   201    41.77ms  /api/orders

  steps...............: 4/4 passed  (stopped after step 4 of 9: --to)

   PASSED   4 steps
```

- `--to <step>` runs the workflow up to that step, **included**, then stops.
- `<step>` is the step's number, as `tinyprobe inspect` lists it (1-based, sleeps and the steps of sequences included), or its name. A name must match one step exactly; a name shared by several steps is an error that lists their numbers.
- The steps after it are not run, and they are not counted: the summary, the exit code and the JUnit report cover the steps up to the target. Exit `0` when they all passed.
- A step before the target that fails stops the run, as always: exit `1`.
- **`--to` takes one workflow file.** With a folder or several files it is a usage error (exit `2`).
- Combined with `-v` or `--reporter ndjson`, the target step's request and response are the ones to look at. `tinyprobe run … --to 4 -v` prints them.
- A `for_each` step as the target runs all its items. A step of a sequence can be the target, by its number or its prefixed name.

### MCP

The `run` tool gets an optional `to` argument (a number or a name), with one path only. An agent can then look at a step in its context without running the rest of a workflow that, for example, deletes what it created.

### Extension

- **▶ Run to here**, a CodeLens on each step of a workflow, next to the step's line.
  - It runs `tinyprobe run <file> --to <n> --reporter ndjson` with the selected environment.
  - It opens the run view on that step, showing its request and response.
- The steps of a sequence share the `use` line, so the lens on that line runs up to the last step of the sequence.
- The lens appears only when the binary supports `--to` (0.18.0 or newer), like the features gated today on `tinyprobe schema` and `mcp`. Older binaries keep everything else: no new `MINIMUM_VERSION`.

## Validation

```text
$ tinyprobe run orders.yaml --to 12
Error: --to 12: the workflow has 9 steps

$ tinyprobe run orders.yaml --to "Creat an order"
Error: --to "Creat an order": no step has this name (did you mean "Create an order"?)

$ tinyprobe run orders.yaml --to "Log in"
Error: --to "Log in": 2 steps have this name (1, 5): use the number

$ tinyprobe run tinyprobe/workflows --to 3
Error: --to runs one workflow file, not a folder
```

All exit `2`, before any request.

## Protocol

New keys only, protocol `1`.

- `run_started` gets `"to"`: the index (0-based, as the steps' `index`) of the last step that will run, or `null`.
- `run_finished` and the summary count only the steps up to it. A new summary key, `"stopped_after"`, holds that index, or `null`.

## Extension

As above:

- the "Run to here" lens, gated on the binary's version;
- the run view selects the target step when the run ends;
- the README and the CHANGELOG describe it.

The extension's own `scripts/dev.sh check` covers the gating and the command line it builds.

## Docs

- `README.md`: `--to` in the usage block and in "Explore an API".
- `docs/workflow-format.md`: no change; the CLI reference is in the README.
- `docs/llm-guide.md`: in the procedure, check a single step of a flow with `run --to` instead of copying its request into a `call`.
- `docs/protocol.md`: `to`, `stopped_after`, the MCP `to` argument.
- `Usage` text of the binary.

## Out of scope

- Running from a step (`--from`): its earlier steps' saved values would be missing, which is the hidden state decision 0003 avoids. Continuing a run from the state it gives is [spec 0022](0022-resume-a-run.md).
- Running one step with the values of the last run (a cache of saved values).
- Breakpoints, or a step-by-step mode that waits between steps.

## Acceptance criteria

- [x] `--to` by number and by name, the step included, with every error above.
- [x] The summary, the exit code, ndjson and JUnit cover only the steps up to the target.
- [x] MCP `run` with `to`.
- [x] Unit and CLI tests:
  - number, name, duplicate name, out of range, a folder;
  - a failing step before the target;
  - a `for_each` step and a sequence's step as the target.
- [x] Extension: the lens on each step, its command line, and the gating on the version, with tests.
- [x] `scripts/dev.sh check` passes in both repositories, and the performance guard.

## Open questions

1. **How a step is named.** The proposal takes the number that `inspect` lists, or the exact name. The extension uses numbers; people type names. The alternative is numbers only: simpler, but `--to 7` in a README or a chat says nothing to the reader. *Recommendation: both.*
2. **The step included.** "Run to here" runs the step and stops after it: its response is what the user wants to see. *Recommendation: included.*
3. **The summary and the exit code.** Steps after the target are left out, so a run that reached its target with every step passing exits `0`. The alternative counts them as "not run", which makes every `--to` run look failed. *Recommendation: left out.*
4. **The extension in this task.** The roadmap task covers both repositories. Doing the CLI and the extension together means the feature ships whole in one release. *Recommendation: both in this task, the extension released after the CLI.*

## Decisions

Approved on 2026-10-28 with the recommendations:

1. `--to` takes the step's number, as `inspect` lists it, or its exact name.
2. The target step runs.
3. Steps after the target are left out of the summary and the exit code.
4. The extension's ▶ Run to here is part of this task, released after the CLI.
