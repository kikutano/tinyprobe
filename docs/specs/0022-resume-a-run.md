# 0022 — Resume a run where it stopped

- **Status:** Draft
- **Roadmap:** M5-10
- **Repositories:** tinyprobe, tinyprobe-app (tinyprobe-vscode later, see the open questions)
- **PRs:** —

## Problem

▶ Up to here ([0014](0014-run-to-here.md)) replays every step before the target, every time. People build a flow one step at a time, from feedback on 2026-11-10:

1. ▶ Up to here on step 7 logs in, creates a user and reads it.
2. ▶ Up to here on step 8 does all of that again before it gets to step 8.

On a long flow this is slow. It also repeats the steps that create data: every click makes another user, another order.

What step 8 needs from the steps before it is small: the values they saved, the workflow `variables` already resolved (an email made with `${$uuid}` must stay the same), and the cookies. Today all of this is lost when `tinyprobe run` ends.

Decision [0003](../decisions/0003-endpoints-declare-workflows-run.md) chose to replay so that nothing is hidden, and 0014 left `--from` out for the same reason. This spec keeps that principle and changes how it is met. The values still come from the `save`s of the same workflow. tinyprobe gives them to the caller at the end of a run, and the caller hands them back to continue that same run. tinyprobe writes nothing to disk and keeps no session between commands, and it refuses a state whose steps have changed. The decision says it is revisited "if stepping through workflows proves not enough in practice": this feedback is that case.

## Proposal

### CLI

A run that stops at `--to` with every step passed gives its **state** in the `run_finished` event of `--reporter ndjson`. A later run of the same workflow takes that state back with `--resume` and starts at the step after it:

```bash
tinyprobe run tinyprobe/workflows/users.yaml --to 7 --reporter ndjson -e tinyprobe/env.local.yaml > run.ndjson
tail -n 1 run.ndjson | jq .state > state.json
tinyprobe run tinyprobe/workflows/users.yaml --resume state.json --to 8 -e tinyprobe/env.local.yaml
```

```text
  continuing after step 7 (Create a user): 3 values, 1 cookie

  ✓ Read the user           GET    200    11.80ms  /api/users/56

  steps...............: 1/1 passed  (continued after step 7; stopped after step 8 of 12: --to)

   PASSED   1 step
```

`--resume -` reads the state from standard input. tinyprobe-app passes it this way, so the token is never written to a file or shown in the list of processes.

Rules:

- **What the state holds:**
  - the values saved by `save`;
  - the workflow `variables` already resolved;
  - the cookies of the run's jar;
  - the step it stopped after;
  - a fingerprint of the steps that ran;
  - the workflow file and the environment file.
- **Where the run starts.** It starts at the step after the state's step. It runs to `--to`, or to the end without it.
- **Values.** The lookup order stays as it is:
  1. step arguments;
  2. saved values, restored from the state;
  3. the environment, read again from the file, so a value changed in the environment is used;
  4. the workflow `variables`. A variable already resolved in the state keeps its value; the others are resolved as usual.
- **Cookies** are put back in the jar before the first request.
- **OAuth2 tokens of an auth set** are not in the state. They are requested again, as at the start of any run: they belong to sending the request (decision 0003).
- **When `run_finished` has a state:** only after a run with `--to` in which every step that ran passed. That is the only case where there is something left to continue. A whole run, or a run that failed, has `"state": null`.
- **Counting.** The steps before the state are not run, sent or counted again. Summary, exit code and JUnit cover the steps of this run.
- **With `--to`:** the target must come after the state's step.
- **With `--skip`:**
  - a skipped step after the state's step is left out as usual;
  - a skipped step before it is ignored. It still gives the "was skipped" message when a later step needs the value it would have saved, so the app passes the same `--skip` list every time.
- **Inside a sequence.** A run can continue between two steps of the same `use`. The sequence's params are resolved again from its `with`: the result is the same unless a param uses a generated value such as `${$uuid}`.
- **One workflow file**, as with `--to` and `--skip`.

### State format

The state is JSON, written by tinyprobe and read back by tinyprobe. A tool keeps it as an opaque value and does not build it.

```json
{
  "state": 1,
  "file": "/home/me/project/tinyprobe/workflows/users.yaml",
  "env_file": "/home/me/project/tinyprobe/env.local.yaml",
  "after": 6,
  "steps": "sha256:4f1c…",
  "saved": { "token": "eyJ…", "userId": 56 },
  "variables": { "email": "smoke-1f3a…@example.com" },
  "cookies": [ { "name": "session", "value": "abc", "domain": "localhost", "path": "/", "expires": null, "secure": false, "http_only": true } ]
}
```

- **`after`** is the 0-based index of the last step that ran, as in `run_started.to`.
- **`steps`** is the fingerprint of steps 1 to `after+1` as tinyprobe loaded them. It covers each step's own definition and the endpoints and auth sets it calls, sequences included. Editing a later step, or anything those steps do not use, keeps the state good. This is the case of building a flow one step at a time.

### MCP

No change in this task (see the out-of-scope section).

### tinyprobe-app

- **▶ Up to here on a later step continues.** This happens when the last run:
  - stopped at `--to` with every step passed;
  - still has its state in the page;
  - and was made with the same environment and the same ⊘ Skip switches.

  The view keeps the earlier steps' results, dimmed and marked "from the previous run", and adds the new ones under them.
- **Otherwise it runs from the start, and says why** in one line next to the run. The reasons are:
  - an earlier step was chosen;
  - the steps that ran changed (tinyprobe refuses the state);
  - the environment or the switches changed;
  - the last run failed;
  - the page was reloaded.
- **↺ From the start** is always next to the run. Use it, for example, when a token has expired because time has passed.
- **▶ Run** runs the whole workflow from the start, as it does today.
- **The state is kept only in the page's memory.** It is never stored in `localStorage` and never written to disk, because it holds tokens. The launcher passes it with `--resume -` and drops it after the run starts.
- **The overview** records the outcome of the chain since the last run from the start, so "8/8 passed" covers the steps before as well.
- **Version.** The app needs the tinyprobe release with `--resume` and shows the new behavior only from that version on. With an older version, ▶ Up to here keeps running from the start.

## Validation

All of these exit with `2`, before any request:

```text
$ tinyprobe run users.yaml --resume state.json
Error: --resume: cannot read state.json: Could not find file '/home/me/project/state.json'.

$ tinyprobe run users.yaml --resume state.json
Error: --resume: not a tinyprobe state (expected the "state" of a run_finished event)

$ tinyprobe run orders.yaml --resume state.json
Error: --resume: the state is of tinyprobe/workflows/users.yaml, not tinyprobe/workflows/orders.yaml

$ tinyprobe run users.yaml --resume state.json -e tinyprobe/env.staging.yaml
Error: --resume: the state was made with -e tinyprobe/env.local.yaml: run from the start to use tinyprobe/env.staging.yaml

$ tinyprobe run users.yaml --resume state.json
Error: --resume: the steps up to step 7 changed since the state was made: run from the start

$ tinyprobe run users.yaml --resume state.json --to 5
Error: --to 5: the state already ran steps 1 to 7: --to must come after step 7

$ tinyprobe run users.yaml --resume state.json      (state after the last step)
Error: --resume: the state is after the last step, nothing is left to run

$ tinyprobe run tinyprobe/workflows --resume state.json
Error: --resume applies to one workflow file, not a folder or several files.
```

A state of a newer format (`"state": 2`) gives `--resume: this state needs a newer tinyprobe`.

## Protocol

These are new keys only, so the protocol stays at `1`.

- **`run_started`** gets `"resumed_after"`: the state's `after`, or `null`. Its `steps` still lists every step, so a tool can draw the earlier ones. The steps up to `resumed_after` send no events.
- **`run_finished`** gets `"state"`: the object above, or `null`.
- **The summary** gets `"resumed_after"`, repeated as for `stopped_after`. The counts cover only this run.
- **Secrets.** The state holds the same values that `step_completed` already shows in `saved` and in the recorded requests, so it adds no new exposure to ndjson. `docs/protocol.md` says to keep it in memory and never store it.

## Extension

Nothing changes in this task. A run started with `--resume` from a terminal already looks right in the run view: the earlier steps have no events and are shown as `–`.

## Docs

- **`docs/cli.md`:** `--resume` in the usage block and in "Explore an API", next to `--to` and `--skip`, with the `jq` example.
- **`docs/llm-guide.md`:** one paragraph. `--resume` is for tools that drive tinyprobe step by step; an agent exploring a flow uses `--to`, and the state is never committed or written into a workflow.
- **`docs/protocol.md`:** `resumed_after`, `state` and its format, and the rule on secrets.
- **Decision 0003:** a note dated when this spec is approved, saying that a run may continue from the state of the same workflow and under which conditions. Spec 0014's out-of-scope section points to this spec.
- **The binary's `Usage` text.**

## Out of scope

- **`--save-state <file>`** for terminal users. Writing tokens to a file is what this design avoids; in a terminal, `--to` is enough, and `jq` gets the state when it is really needed.
- **`resume` in the MCP `run` tool.** An agent re-runs with `to`. Putting the state into the model's context adds tokens and secrets for little gain.
- **A step-by-step mode that waits between steps** in a single process (a debugger). That is a different way to build the same thing, with a process that stays open.
- **Continuing after a failed step.** The state of a failed run is `null`. Continuing from the step before the failed one would need a state for every step, not only the last one.
- **The extension** (see the open questions).

## Acceptance criteria

- [ ] `run_finished.state` after a `--to` run that passed; `null` after a whole run and after a failure.
- [ ] `--resume <file>` and `--resume -`:
  - restore saved values, resolved variables and cookies;
  - start at the step after the state's step;
  - respect `--to` and `--skip`;
  - give each error above.
- [ ] The fingerprint:
  - an edit to a later step keeps the state good;
  - an edit to a step that ran, or to an endpoint it calls, makes it refused.
- [ ] A generated workflow variable (`${$uuid}`) has the same value before and after the resume.
- [ ] A cookie set before the state is sent after the resume.
- [ ] Console: the "continuing after" line and the summary.
- [ ] ndjson: `resumed_after` and `state`.
- [ ] Unit and CLI tests for all of the above, including a resume between two steps of a sequence.
- [ ] `scripts/dev.sh e2e`: `users.yaml` against the test server, run with `--to` and then resumed to the end. The two runs together leave the server's data as it was.
- [ ] tinyprobe-app:
  - ▶ Up to here continues, and runs from the start with the reason in every case above;
  - ↺ From the start;
  - the state kept only in memory and passed on stdin;
  - the feature hidden with an older tinyprobe.
- [ ] `scripts/dev.sh check` passes in both repositories, along with the performance guard.

## Open questions

1. **Revisiting decision 0003.** The decision chose to replay every time; this spec continues from a state instead. The conditions are:
   - the state comes from the same workflow;
   - it is kept by the caller and not by tinyprobe;
   - it is checked against the steps that made it.

   *Recommendation: approve the change, with a note in 0003.*
2. **How strict the fingerprint is.** The proposal covers only the steps that ran and what they call, so editing the next step does not force a run from the start. That is the case this feature is for. The stricter alternative covers every loaded file and the environment file's contents: safer, but any edit, even to step 8 itself, starts over. *Recommendation: the steps that ran.*
3. **The option's name.** `--resume <file>` reads as "carry on". `--from-state` is more literal, and `--continue` is close to `continue` in other tools. *Recommendation: `--resume`.*
4. **When `run_finished` has a state.** The proposal gives it only after a `--to` run that passed, which keeps ndjson small and CI logs clean. The alternative gives it after every run. *Recommendation: only after `--to` that passed.*
5. **The extension.** ▶ Run to here could continue in the same way, with the state kept in the extension's memory. Doing it in this task means three releases at once. *Recommendation: CLI and app in this task; the extension in a follow-up task, next to M5-9.*
