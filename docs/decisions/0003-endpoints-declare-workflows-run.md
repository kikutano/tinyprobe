# 0003 — Endpoints declare, workflows run

- **Status:** Accepted
- **Date:** 2026-10-26

## Context

An API file declares endpoints; a workflow calls them, checks the responses and stores values with `save`. Calling endpoints one at a time (`tinyprobe call`, ▶ Call in the extension) raised a question: should a single call remember values for the next ones, like Postman? Call `login`, and `get_user` is authenticated with the saved token.

Two ways to get there:

1. **State in single calls.** A `save` on the endpoint in the API file, or a session file that `call` writes and reads. Every call can change the values that the next calls use.
2. **State only in workflows.** A value such as `${token}` is always created by a `save` written in a workflow. To try a flow one request at a time, you step through the workflow.

Postman works like option 1: scripts write into the environment, so a request works only if another one ran before it, and nothing in the collection says which. This is what makes collections fragile, and what users coming from Postman struggle with when the tests run in CI.

## Decision

- **An API file is declarative.** It describes requests: method, path, query, headers, auth sets, body, params. It has no `save`, no checks, and nothing that runs or changes state.
- **A workflow is where things run.** Every value that moves from one request to the next is created by a `save` in a step of that workflow, and every workflow starts from zero: it logs in itself.
- **A single call is stateless.** `tinyprobe call` and ▶ Call send one request with the arguments given (`--with`), the environment file and `--var`. They remember nothing for the next call. The extension may prefill the last arguments; that is a convenience in the editor, not state in tinyprobe.
- To try a flow one request at a time, run the workflow up to a step ("Run to here", ROADMAP M4-10). It replays the earlier steps every time, so there is no hidden state.

## Consequences

- A workflow reads the same way it runs: its file shows where each value comes from, and it gives the same result locally, in CI and in any order.
- Agents get one simple rule: API files describe, workflows run. There is no key that behaves differently in `call` and in `run`.
- A token used often in single calls goes in `env.local.yaml` or in the extension's `tinyprobe.variables`, by hand. This is less convenient than Postman's scripts, and we accept it.
- "Run to here" replays steps that create data. Workflows that clean up after themselves (guide, section 9) make this safe on a test environment.
- **Credentials obtained by an auth set.** An `oauth2` auth set (client credentials, [spec 0010](../specs/0010-basic-and-oauth2.md)) sends a token request before the request it describes. This is part of sending that request, like the cookie jar: it is declared in the API file, takes no value from earlier steps, and the token lives for one run only (one workflow, or one `call`). Note added on 2026-10-28.
- **Continuing a run** (note added on 2026-11-10, [spec 0022](../specs/0022-resume-a-run.md)). Replaying every step on each "Run to here" proved slow on long flows in tinyprobe-app, and it creates data again on each click. A run cut by `--to` may therefore be continued from its state with `run --resume`. This does not bring back the hidden state ruled out above: the values still come from the `save`s of the same workflow, the state is given to the caller and kept by it (tinyprobe writes nothing to disk and keeps no session), and a state whose steps changed is refused. A single call still keeps no state.
- **Global variables for single calls** (note added on 2026-11-25, [spec 0023](../specs/0023-global-variables.md)). Dogfooding showed that endpoints are used more and more on their own, and that a workflow only to log in and read a profile is tedious: stepping through workflows was not enough in practice. An endpoint may therefore declare `globals`, values of its 2xx response saved for the next single calls, in a file the caller names with `--globals`. What is saved is written in the API file and the values are in a file the user reads and edits, so nothing is hidden; without `--globals` tinyprobe keeps no state, so CI and workflows start from zero and pass values with `save` as before.
- Proposals that add `save` or checks to API files, or a session shared between calls, are ruled out by this decision. We revisit it only if stepping through workflows proves not enough in practice (it did once: global variables, above).
