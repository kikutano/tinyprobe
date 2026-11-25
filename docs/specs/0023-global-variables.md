# 0023 — Global variables for single calls

- **Status:** Implemented
- **Roadmap:** Inbox, "A token for single calls without a workflow" (dogfooding on 2026-11-06 and 2026-11-23)
- **Repositories:** tinyprobe, tinyprobe-app (tinyprobe-vscode later, see the open questions)
- **PRs:** see the roadmap

## Problem

Endpoints are used more and more on their own, from dogfooding on 2026-11-06 and 2026-11-23. To read one's own profile (`GET /me`), the token of a login is needed, and a single call remembers nothing ([decision 0003](../decisions/0003-endpoints-declare-workflows-run.md)). Today the choices are:

1. a workflow `login` → `me`, written only to read a profile, and one more for each small combination of calls;
2. copying the token by hand into `env.local.yaml` or into the field of the call, again each time it expires.

Both are tedious for what people do all day: log in once, then call what needs the token.

## Proposal

An endpoint declares in its API file the values of its response it **saves as global variables**. A caller that wants them names a file with `--globals`: the global variables are read from it as variables, and those the endpoint saves are written to it after a 2xx response.

```yaml
endpoints:
  login:
    method: POST
    path: /api/login
    params: { username: admin, password: }
    body: { username: "${username}", password: "${password}" }
    globals:
      token: $.access_token        # saved after each 2xx response
  me:
    path: /api/me
    auth: bearer                   # Authorization: "Bearer ${token}"
```

```bash
tinyprobe call api.yaml login --with password=secret --globals globals.local.json
tinyprobe call api.yaml me --globals globals.local.json
```

### Format

- `endpoints.<name>.globals`: a mapping of variable names to a JSONPath of the response body, or `{ cookie: name }`, validated like `save` (an invalid JSONPath is an error at its line and column).
- Nothing on the endpoints that use them: a `${token}` is resolved from the global variables like any variable. The lookup order becomes: the step's `with`, saved values, `--var`, **global variables**, the environment file, the workflow's `variables`.

### CLI

- `--globals <file>` on `call` and `run`. Without it, `globals` keys are ignored and nothing changes: CI, existing workflows and tools behave as before.
- A value is saved only after a **2xx** response, so a failed login does not replace a good token. A value missing from a 2xx response is reported and the call does not fail.
- The file is JSON, so tools read it without a YAML parser and people can still read and edit it. Each variable has the endpoint that saved it and when (UTC):

  ```json
  { "token": { "value": "eyJ…", "from": "login", "at": "2026-11-25T10:12:00Z" } }
  ```

  A value written by hand without the object (`"token": "abc"`) is read too. Variables saved again replace theirs in place; new ones go last. The file is replaced at once, never half written.
- A missing global variable says what to call: `Variable 'token' is not defined: call 'login' first, it saves it as a global variable.`
- The console shows `✓ global token = …` under the response, and `! global …` for one not found.
- `run --globals` reads the file and saves the globals of the endpoints the workflow calls, for the steps after them too. It is for trying things by hand; workflows meant for CI still log in and `save`.
- `tinyprobe init` adds `globals.local.json` to `.gitignore`: the file holds tokens.

### Protocol (additive)

- `inspect --json`: an endpoint with `globals` has `"globals": { "token": "$.access_token" }`.
- `step_completed`: `"globals"`, the values saved by the step, and `"globals_missing"`, the messages of those not found. Both only when there are some.

### tinyprobe-app

- Every single call passes `--globals globals.local.json`, in the folder it opened, when the tinyprobe in use supports it (0.26.0 or newer).
- On an endpoint, a box shows what it **saves** (`token: from $.access_token of the response`, with the value saved now) and what it **uses** (`token: saved from login endpoint · 10:12`, or not saved yet with **▶ Call login**). A variable with a value has a green check, one without a grey empty circle, and a line just saved flashes green.
- Under a 2xx JSON response: "Save a value as a global variable…" picks a value and a name, writes `globals` into the endpoint (checked by tinyprobe) and saves the value at once. The endpoint form has the same rows.
- A "Global variables" view lists them with their value, the endpoint that saved them, when and which endpoints use them; they can be edited, deleted, cleared or set by hand.
- A workflow runs with the global variables only when its "uses global variables" switch is on (per folder, off by default).
- The fields of a call show in grey the value they get when left empty, from the environment or a global variable (secrets as dots), and the `${…}` of paths and step names are colored.

## Decision 0003

0003 rules out "a session shared between calls". This proposal stays close to what it protects:

- nothing is hidden: what an endpoint saves is written in its API file, and the values are in a file the user names, reads and edits;
- it is opt-in per command: without `--globals` tinyprobe keeps no state, so CI and workflows start from zero as before;
- workflows still pass their values with `save`.

It is still a state shared between calls, which 0003 ruled out "unless stepping through workflows proves not enough in practice": the feedback above is that case. A note is added to 0003.

## Alternatives considered

- **An auth set that logs in through an endpoint** (like `oauth2`, a login before each call): no state at all, but a login before every request, and nothing for values other than tokens. Left for later; both can live together.
- **"Save to the environment" in the app**: writes into `env.local.yaml`, mixing what people write with what calls save, and only in the app.
- **A session kept by tinyprobe** (a hidden file): ruled out, nothing would say where a value comes from.

## Open questions

- The VS Code extension: ▶ Call with `--globals` and the same box, with the shared code.
- Expiry: a token saved yesterday may be expired. The time of each value is shown; a refresh could use the auth-set login above.
