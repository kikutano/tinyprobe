# 0021 — Skip steps in a run

- **Status:** Draft
- **Roadmap:** M5-8
- **Repositories:** tinyprobe, tinyprobe-app (tinyprobe-vscode later, see the open questions)
- **PRs:** —

## Problem

While working on a flow, people want to leave a step out for a while without deleting it, for example:

- the step that deletes the user, to look at the data it leaves;
- a slow `sleep`, or a step that waits on a job;
- a step against a service that is down today.

Today the only way is to delete the step or comment it out in the YAML, and then remember to put it back. A step left out this way can end up in a commit, and CI then passes without running it. Postman's runner solves this by unchecking a request for one run, and the file stays as it is.

This is useful from the command line and for agents as well as in the app: an agent can explore a flow and leave out the step that would delete what it wants to look at. That makes it a general option of `run`, as [decision 0004](../decisions/0004-the-app-is-a-client-of-the-cli.md) requires, and not an app feature in the CLI.

## Proposal

### CLI

```bash
tinyprobe run tinyprobe/workflows/users.yaml --skip 11 -e tinyprobe/env.local.yaml
tinyprobe run tinyprobe/workflows/users.yaml --skip "Delete the user" --skip 12 -e tinyprobe/env.local.yaml
```

```text
  ✓ Log in › Admin logs in   POST   200    35.10ms  /api/login
  ✓ First page of users      GET    200    12.40ms  /api/users
  ...
  ✓ Patch the user           PATCH  200    14.02ms  /api/users/7
  – Delete the user          skipped (--skip)
  – Deleted user is gone     skipped (--skip)

  steps...............: 10/10 passed  2 skipped

   PASSED   10 steps
```

Rules:

- **`--skip <step>` leaves that step out of the run.** The option is repeatable.
- **How a step is named:** as with `--to` ([0014](0014-run-to-here.md)), the step's number as `tinyprobe inspect` lists it, or its exact name. The errors are the same, with `--skip` in place of `--to`.
- **The file does not change.** Nothing is saved: the next run without `--skip` runs every step.
- **Which steps can be skipped:**
  - any step, including a `sleep`, an `until` step and a `for_each` step (all of its items);
  - one step of a sequence, by its number or its prefixed name (`Log in › Admin logs in`);
  - a whole `use` step, by giving the numbers of all its steps.
- **Counting:**
  - skipped steps count neither as passed nor as failed;
  - the exit code covers the steps that ran: `0` when they all passed, `1` when one failed;
  - the summary shows them as `N skipped`.
- **A value that only a skipped step saves.** A later step that uses that value fails, with a message that says why:

  ```text
  ✗ Patch the user   variable 'userId' is not defined: step 8 (Create a user), which saves it, was skipped
  ```

  The rule is applied at run time, like the existing `Variable 'x' is not defined.` error. If the environment or `--var` gives the value, the later step uses it and does not fail.
- **With `--to`:**
  - a skipped step before the `--to` target is left out;
  - a skipped step after the target is ignored;
  - skipping the target itself is an error.
- **One workflow file.** As with `--to`, `--skip` with a folder or several files is a usage error: the same number means different steps in different files.
- **The same step given twice** (by number and by name, for example) counts as one.

### MCP

The `run` tool gets an optional `skip` argument: a list of step numbers or names. It applies to one path only, like `to`.

### tinyprobe-app

- **The switch:**
  - each step of the diagram has **⊘ Skip** in its panel, and so does each row of the list;
  - a skipped step is drawn dimmed, marked "skipped in runs", and its button becomes **Run it again**;
  - next to ▶ Run, "2 steps skipped · Run all" turns every switch back on.
- **Which runs skip them:** ▶ Run and ▶ Up to here pass `--skip` for each switch that is off.
- **Where the switches are kept:**
  - in the browser, per folder and per workflow, as the last runs are, keyed by the step's number and name;
  - when the file changes and the step at that number no longer has that name, its switch is dropped, so the app never skips a step the user did not choose.
- **Version:** the app needs the tinyprobe release that ships `--skip`, and shows the switch only from that version on, as it did for `--source`.

## Validation

As for `--to`, all before any request, exit `2`:

```text
$ tinyprobe run users.yaml --skip 14
Error: --skip 14: the workflow has 12 steps

$ tinyprobe run users.yaml --skip "Delete the usr"
Error: --skip "Delete the usr": no step has this name (did you mean "Delete the user"?)

$ tinyprobe run users.yaml --skip "Log in"
Error: --skip "Log in": 2 steps have this name (1, 5): use the number

$ tinyprobe run users.yaml --to 11 --skip 11
Error: --skip 11: it is the step of --to, which always runs

$ tinyprobe run users.yaml --skip 1 --skip 2 ... (every step)
Error: --skip: every step of the workflow is skipped, nothing would run

$ tinyprobe run tinyprobe/workflows --skip 3
Error: --skip applies to one workflow file, not a folder
```

## Protocol

These are new keys only, so the protocol stays at `1`.

- **`run_started`** gets `"skipped"`: the indexes (0-based, as `steps[].index`) of the skipped steps, in order, or `[]`. The skipped steps stay in its `steps` list, so a tool can still draw them.
- **Step events:** a skipped step sends no `step_started` and no `step_completed`.
- **`run_finished`:** the summary gets `"skipped_steps"`, a count. `total_steps` counts only the steps that were meant to run, so `passed_steps + failed_steps + not_run_steps = total_steps` stays true.
- **A `for_each` step that is skipped** counts as one skipped step, since its items were never read.
- **JUnit:** each skipped step is a `<testcase>` with `<skipped message="Skipped with --skip"/>`, counted in `skipped` like the steps that did not run.
- **The MCP `run` tool** returns `skipped_steps` in its `summary`, and each skipped step in `steps` has `"skipped": true`.

## Extension

Nothing changes in this task. The run view already marks steps without events as `–`, so a run started with `--skip` from a terminal already looks right in the extension. See the open questions for adding the switch to the extension.

## Docs

- **`docs/cli.md`:** `--skip` in the usage block and in "Explore an API", next to `--to`.
- **`docs/llm-guide.md`:** when exploring, leave a destructive or slow step out with `--skip` instead of editing the workflow. Common mistake: committing a step that was commented out instead of skipped.
- **`docs/protocol.md`:** `skipped`, `skipped_steps`, the JUnit `skipped`, and the MCP `skip` argument.
- **`README.md`:** one line in the CLI section.
- **The binary's `Usage` text.**

## Out of scope

- **A `skip:` key in the YAML** (option B, set aside on 2026-11-06). It would be visible to everyone and in CI, but a step left off by mistake would pass silently.
- **Skipping single items of a `for_each`.**
- **Running only some steps (`--only`).** `--to` and `--skip` cover the cases seen so far.
- **Skipping on a condition** (`if:`). That is a format feature with its own problems.

## Acceptance criteria

- [ ] `--skip` by number and by name, repeatable, with every error above, and combined with `--to`.
- [ ] Skipped steps:
  - send no step events;
  - are listed in `run_started.skipped` and counted in `skipped_steps`;
  - are excluded from the exit code;
  - appear in JUnit with `<skipped>`.
- [ ] The failure of a later step that uses a value only a skipped step saves names that step. A value given by the environment is used.
- [ ] Console: the `–` line for each skipped step and `N skipped` in the summary.
- [ ] MCP `run` with `skip`.
- [ ] Unit and CLI tests:
  - number, name, duplicate name, out of range, a folder;
  - `--to` combined;
  - a sleep, a `for_each` step and a step of a sequence skipped;
  - the missing saved value, with and without a value in the environment.
- [ ] `docs/example`: a CLI check in `scripts/dev.sh e2e` that skips the delete steps of `users.yaml` against the test server.
- [ ] tinyprobe-app: the switch in the diagram and the list, the switches passed to ▶ Run and ▶ Up to here, switches dropped when their step changes, and the switch hidden with an older tinyprobe.
- [ ] `scripts/dev.sh check` passes in both repositories, along with the performance guard.

## Open questions

1. **How a step is named.** The proposal takes a number or the exact name, as `--to` does, so both options read the same way. The app passes numbers. *Recommendation: both, as for `--to`.*
2. **When the missing value is detected.** The proposal reports it at run time, when the later step needs the value. Checking before the run would mean guessing which variables each step uses, and the environment may provide them anyway. *Recommendation: at run time, with the message that names the skipped step.*
3. **Skipping every step.** The proposal makes it an error. The alternative is a run of 0 steps that passes, which looks like a success while testing nothing. *Recommendation: an error.*
4. **The extension.** Its diagram is the app's shared code, so the switch could follow cheaply. Doing it in this task means three releases at once. *Recommendation: CLI and app in this task; the extension in a follow-up task, M5-9.*
5. **Where the app keeps the switches.** The proposal keeps them in the browser, per workflow, until they are turned back on, and they survive closing the app. The alternative keeps them only until the page is closed: safer against forgetting, but they have to be set again every time. *Recommendation: kept in the browser, with "2 steps skipped · Run all" always visible next to ▶ Run so they are not forgotten.*
