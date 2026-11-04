# 0019 — A call shows its status, not a verdict

- **Status:** Implemented
- **Roadmap:** Inbox (`cli`): `tinyprobe call` prints a green ✓ whatever the status
- **Repositories:** tinyprobe
- **PRs:** [#36](https://github.com/kikutano/tinyprobe/pull/36)

## Problem

`tinyprobe call` sends one request and prints it as a step of a run, with the same mark:

```text
$ tinyprobe call api/testserver.yaml login --with password=wrong -e env.yaml
  ✓ login  POST   401     8.14ms  /api/login

  HTTP 401 Unauthorized
  ...
```

The green ✓ says "passed", but nothing was checked: an API file has no checks ([decision 0003](../decisions/0003-endpoints-declare-workflows-run.md)), so any response gets it, a 401 or a 500 included. Someone who calls an endpoint to see whether it works reads the ✓ before the status and thinks it worked. Found while dogfooding on 2026-11-02. tinyprobe-app (0.4.1) and the extension (0.10.0) already show a call with its status and no verdict.

## Proposal

The line of a call has no ✓ or ✗ when a response arrives. A neutral arrow takes the place of the mark. The status keeps the colors it has today: 2xx green, 3xx cyan, 4xx yellow, 5xx red.

```text
$ tinyprobe call api/testserver.yaml login --with password=wrong -e env.yaml
  → login  POST   401     8.14ms  /api/login

  HTTP 401 Unauthorized
  ...
```

```text
$ tinyprobe call api/testserver.yaml login --with password=password -e env.yaml
  → login  POST   200     6.02ms  /api/login

  HTTP 200 OK
  ...
```

When no response arrives (connection refused, timeout, a TLS error), the call itself failed: the line keeps the red ✗ and the reason under it, as today.

```text
  ✗ login  POST   ---              /api/login
      ✗ Connection refused (localhost:5122)
```

Rules:

- The arrow is `→`, or `>` when the console has no Unicode (as ✓ becomes `v` today). It is grey, and the endpoint's name is in the normal color.
- Only `tinyprobe call` changes. `tinyprobe run` keeps ✓ and ✗: a step of a workflow has checks, and passed or failed is its result.
- Exit codes do not change: 0 when a response arrived, whatever its status; 1 when none did; 2 for usage errors.
- The ndjson reporter (`call --reporter ndjson`) does not change. Its `step_completed` keeps `success`, which is true when a response arrived; the clients decide how to show it.

## Validation

No new error.

## Protocol

No change.

## Extension

No change: since 0.10.0 the extension shows a call with its status and no PASSED, from the ndjson events.

## Docs

- `docs/llm-guide.md`: say that the arrow line of `call` is not a verdict: an agent reads the status (`HTTP 401 Unauthorized`), not a mark. The sentence about `call` already says "whatever the status".
- `README.md`: the `call` example (`status --with code=404`) gets its output with the arrow.
- `docs/workflow-format.md` and `docs/protocol.md`: no change.

## Out of scope

- A check on a single call (`--expect-status 200`, so that `call` can fail on a status in a script). It would be the first check outside a workflow; if it is needed, it gets its own spec.
- The output of `run` and of a suite.

## Acceptance criteria

- [x] `tinyprobe call` of an endpoint answering 200, 401 and 500 prints `→` and the colored status, with no ✓ or ✗, and exits 0.
- [x] A call with no response prints ✗, the reason, and exits 1, as today.
- [x] Without Unicode the arrow is `>`.
- [x] `tinyprobe run` prints ✓ and ✗ as before.
- [x] The README example and the guide are updated.
- [x] `scripts/dev.sh check` passes.

## Decisions

Approved on 2026-11-04:

- **The mark** is `→` (`>` without Unicode). A grey `•` and no mark at all were the other choices.
- **No response** keeps the red ✗ and the reason: nothing came back, and the exit code is 1.
