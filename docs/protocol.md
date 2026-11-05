# tinyprobe JSON protocol

Programs (a local UI, an editor plugin, a CI dashboard) drive tinyprobe through the `tinyprobe` binary and read JSON from its standard output. They never parse YAML or reimplement checks: the binary is the only engine.

| Command | Output |
|---|---|
| `tinyprobe inspect <file.yaml> --json` | one JSON document: what the file defines, or its problems |
| `tinyprobe run <workflow.yaml> --reporter ndjson` | one JSON event per line, as the run goes |
| `tinyprobe run <folder or files>... --reporter ndjson` | the same events for each workflow, between `suite_started` and `suite_finished` |
| `tinyprobe call <file.yaml> <endpoint> --reporter ndjson` | the same events, for a single request |
| `tinyprobe run … --junit <file>` | a JUnit XML report for CI servers, besides the output above ([JUnit report](#junit-report)) |
| `tinyprobe schema` | the JSON Schema of tinyprobe files ([Schema](#schema)) |
| `tinyprobe mcp` | an MCP server on standard input and output, with the tools below ([MCP](#mcp)) |

## Rules

- **Versioning.** Every document and the first event of a run carry `"protocol": 1` and `"version"`, the tinyprobe version. The protocol number changes only when a key is removed or changes meaning. New keys can appear in any release, so ignore keys you don't know.
- **Keys** are `snake_case`. **Durations** are milliseconds (`*_ms`), as numbers.
- **YAML values** (variables, bodies, `with`, param defaults) are shown as JSON with their placeholders **unresolved**: `"${email}"`, `"smoke-${$uuid}@example.com"`. Only a run resolves them.
- **Locations** are `{ "file", "line", "column" }`, 1-based. `file` is relative to the current directory when the file is inside it, otherwise absolute. Problems that concern a whole file have `line` 0. Problems with arguments given on the command line have the file `command line`.
- **Exit codes** are the same as for people: `0` success, `1` a step failed (in a suite: a workflow failed), `2` invalid arguments or invalid file. Errors in the arguments themselves (unknown option, missing file) are plain text on standard error, with exit code `2` and no JSON.
- **Encoding.** The output is UTF-8. Non-ASCII characters are written as `\uXXXX` escapes, so the bytes are plain ASCII.

## `inspect --json`

Takes a workflow or an API file (a file without `steps`). A workflow is validated completely, imports included.

```json
{
  "protocol": 1,
  "version": "0.9.0",
  "valid": true,
  "kind": "workflow",
  "file": "/home/me/project/tinyprobe/workflows/users.yaml",
  "name": "Users",
  "description": "",
  "settings": { "timeout_ms": 30000, "max_retries": 0 },
  "variables": [
    { "name": "email", "value": "smoke-${$uuid}@example.com", "location": { "file": "tinyprobe/workflows/users.yaml", "line": 5, "column": 10 } }
  ],
  "imports": [ { "name": null, "file": "/home/me/project/tinyprobe/api/backend.yaml" } ],
  "endpoints": [
    {
      "name": "login",
      "import_name": null,
      "method": "POST",
      "base_url": "${baseUrl}",
      "path": "/login",
      "query": [],
      "headers": [ { "name": "X-Client", "value": "tinyprobe" } ],
      "body": { "email": "${email}", "password": "${password}" },
      "form": null,
      "multipart": null,
      "basic_auth": null,
      "oauth2": null,
      "params": [
        { "name": "email", "required": true, "default": null },
        { "name": "password", "required": false, "default": "secret" }
      ],
      "arguments": ["baseUrl", "email", "password"],
      "location": { "file": "tinyprobe/api/backend.yaml", "line": 12, "column": 3 }
    }
  ],
  "steps": [
    {
      "index": 0,
      "type": "call",
      "name": "Login",
      "call": "login",
      "with": { "email": "${email}" },
      "expect": {
        "status": [200],
        "status_placeholder": null,
        "checks": [ { "subject": "$.token", "condition": "exists" }, { "subject": "duration", "condition": "< 500ms" } ]
      },
      "for_each": null,
      "until": null,
      "save": { "token": "$.token" },
      "location": { "file": "tinyprobe/workflows/users.yaml", "line": 9, "column": 5 },
      "source_location": null,
      "sequence": null
    },
    { "index": 1, "type": "sleep", "name": "sleep 1s", "duration_ms": 1000, "location": { "file": "tinyprobe/workflows/users.yaml", "line": 17, "column": 5 } }
  ]
}
```

- `kind` is `workflow`, `api` or `sequence`. An API file has an empty `steps` and `variables`. A sequence file (`inspect` of a file with `sequence:`) also has `params`, as an endpoint's, and its `steps`.
- In a workflow, the steps of a sequence are listed where their `use` is, with consecutive indexes, as they run. Every step has:
  - `source_location`: where a sequence's step is written, in the sequence file, or `null`;
  - `sequence`: `{ "name", "file" }` of the `use` it comes from, or `null`. When that `use` has `for_each`, it also has `item` (1-based) and `items` (the count): `{ "name": "User lifecycle", "file": "/…/user-lifecycle.yaml", "item": 2, "items": 3 }`. The run events have no `sequence`: there the step's `name` carries `[2/3]`.

  For those steps `location` is the `use` step's, the line a tool shows the result on.
- `name` is the name a step calls. For an endpoint of a named import (`- billing: ../api/billing.yaml`) it is `billing.login`, and `import_name` is `billing`; otherwise `import_name` is `null`.
- `imports` (top level) lists the imported API files in load order, sequences' imports included: `{ "name": "billing" | null, "file": "/…/api/billing.yaml" }`.
- `headers` are the effective headers of the endpoint: file headers, then `auth` sets, then the endpoint's own, where a later one with the same name replaces an earlier one.
- `body`, `form` and `multipart`: at most one is not `null`. `form` and `multipart` are the fields in order, one entry per field sent (a YAML list gives one entry per item): `{ "name", "value" }` for a text field, `{ "name", "file", "filename", "content_type" }` for a file, with `filename` and `content_type` `null` when they are not written. Values are the literal YAML, placeholders included, as in `body`:
  ```json
  "multipart": [
    { "name": "description", "value": "Profile picture" },
    { "name": "avatar", "file": "${avatar}", "filename": null, "content_type": null }
  ]
  ```
- `basic_auth` and `oauth2`: the `basic` or `oauth2` key of the endpoint's auth set, `null` when it has none. Values are the literal YAML, placeholders included; `scope` is `null` when not written, and a list of scopes is shown joined with spaces. `headers` does not list the `Authorization` header they write.
  ```json
  "basic_auth": { "username": "${username}", "password": "${password}" },
  "oauth2": { "token_url": "${authUrl}/oauth/token", "client_id": "${clientId}", "client_secret": "${clientSecret}", "scope": "orders:read orders:write", "client_auth": "basic", "form": {} }
  ```
- `arguments` are the names a call of the endpoint can pass with `--with` (or a step's `with`): its params, its query names and every placeholder of its base URL, path, headers, body, form and multipart fields, `basic_auth` and `oauth2`. Generated values (`${$uuid}`) are not listed. A UI can build the form of an endpoint from `params` (required ones first) and `arguments`.
- `until`, when present, has the same `status` and `checks` as `expect`, plus `every_ms` and `timeout_ms`.
- `for_each` is `null`, or `{ "items", "variable", "file" }`:
  - `items` holds the literal items (a data file's are read and listed, with its path in `file`), or is `null` for a variable;
  - `variable` is the name of a `for_each: ${name}`.
  - Example: `{ "items": [ { "code": 200 }, { "code": 404 } ], "variable": null, "file": null }`.
- `status_placeholder` is the `${…}` of a `for_each` step's `expect.status` (`status` is then empty), otherwise `null`.
- `checks[].subject` is a JSONPath, `duration`, `header <name>` or `cookie <name>`.
- `checks[].condition` is the text the report shows, such as `>= 3`, `in ["a","b"]`, `< 0 or > 100`, `matches "^ORD-[0-9]+$"`, `schema ../openapi.yaml#/components/schemas/User`. A failed `schema` check is one assertion per problem: `subject` is the JSONPath of the value, `condition` the rule (`schema type "string"`, `schema required`), `actual` the value (objects and arrays in short: `{…}`, `[3 items]`). Strings in `condition` and `actual` keep characters such as `+`, `<` and `'` as they are (since 0.20.0; before, they were escaped as `\u002B`).
- `save` maps each variable to where its value is read: a JSONPath (`"$.token"`) or `cookie <name>` (`"cookie XSRF-TOKEN"`).

An invalid file gives exit code `2` and:

```json
{
  "protocol": 1,
  "version": "0.9.0",
  "valid": false,
  "file": "/home/me/project/tinyprobe/workflows/users.yaml",
  "errors": [
    {
      "file": "tinyprobe/workflows/users.yaml",
      "line": 14,
      "column": 11,
      "message": "unknown endpoint 'get_usr' (did you mean 'get_user'?)",
      "text": "tinyprobe/workflows/users.yaml:14:11: unknown endpoint 'get_usr' (did you mean 'get_user'?)"
    }
  ]
}
```

When the file is a workflow whose imports all loaded, the invalid JSON also has `endpoints`, with the same shape as in a valid inspection: the endpoints of the imports and of the workflow itself. Editors use them to complete endpoint names and params while the workflow is still being written. When an import is missing or invalid, `endpoints` is left out.

## `--reporter ndjson`

One JSON object per line on standard output, written and flushed as soon as it happens. Every event has an `event` key. With `-v` the request log goes to standard error, so standard output stays valid NDJSON.

| Event | When |
|---|---|
| `run_started` | the workflow is valid and is about to run |
| `step_started` | a step starts, again before each new attempt of an `until` step, and for each item of a `for_each` step |
| `step_completed` | a call step ended, passed or failed (not sent for `sleep` steps); a `for_each` step sends one, after its last item |
| `run_finished` | the run ended; the last event |
| `invalid` | the workflow is invalid: the only event, exit code `2` |

```json
{"event":"run_started","protocol":1,"version":"0.9.0","workflow":"Users","file":"/home/me/project/tinyprobe/workflows/users.yaml","to":null,"env_file":"/home/me/project/tinyprobe/env.local.yaml","steps":[{"index":0,"type":"call","name":"Login"},{"index":1,"type":"sleep","name":"sleep 1s"}]}
{"event":"step_started","index":0,"type":"call","name":"Login","attempt":1,"iteration":null,"iterations":null}
{"event":"step_completed","index":0,"name":"Login","success":true,"failure":null,"method":"POST","url":"http://localhost:8080/login","status":200,"expected_status":[200],"duration_ms":12.4,"attempts":1,"bytes_sent":48,"bytes_received":120,"assertions":[{"subject":"$.token","condition":"exists","actual":"\"eyJ...\"","passed":true}],"saved":{"token":"eyJ..."},"request":{"method":"POST","url":"http://localhost:8080/login","headers":[{"name":"Content-Type","value":"application/json; charset=utf-8"}],"body":"{\"email\":\"ada@example.com\",\"password\":\"...\"}"},"response":{"status":200,"reason":"OK","headers":[{"name":"Content-Type","value":"application/json"}],"body":"{\"token\":\"eyJ...\"}","body_truncated":false},"iterations":null}
{"event":"step_started","index":1,"type":"sleep","name":"sleep 1s","attempt":1,"iteration":null,"iterations":null}
{"event":"run_finished","success":true,"summary":{"total_steps":1,"passed_steps":1,"failed_steps":0,"not_run_steps":0,"stopped_after":null,"checks_passed":2,"checks_failed":0,"http_requests":1,"bytes_sent":48,"bytes_received":120,"duration_ms":1015.2,"http_req_duration_ms":{"avg":12.4,"min":12.4,"med":12.4,"max":12.4,"p90":12.4,"p95":12.4}}}
```

- **`run --to`.** `run_started` has `to`, the index of the last step that runs, and lists only the steps up to it; `summary.stopped_after` repeats it. Both are `null` for a whole run. The steps after it are not counted anywhere, so `success` is `true` when every step up to it passed.
- `index` is the position of the step in the workflow, sleeps included, and matches `steps[].index` of `inspect` and `run_started`.
- `step_completed` of a failed step has `success: false` and either `failure` (a status, network or variable problem) or failed `assertions`. Steps after it are not run: they are counted in `not_run_steps`.
- `status` is `null` when no response was received, for example on a network error. Then `response` is `null` too, while `request` shows what was sent.
- `request` and `response` belong to the last attempt. `headers` keep their order and include content headers. Bodies are text; a response body over 1 MiB is cut and has `body_truncated: true`. A `multipart/form-data` request body is the body as sent with each file's content replaced by one line, `(file ../fixtures/avatar.png, 79 B)`, with `\n` line endings. Headers set by the HTTP client itself (such as `Host`) are not listed. The `Cookie` header is listed as sent: a `Cookie` header of the API file followed by the cookies of the workflow's cookie jar.
- `saved` holds the values stored by `save` in this step, with their JSON type.
- **`for_each` steps.**
  - `step_started` has `iteration` (1-based) and `iterations` for each item; both are `null` for other steps.
  - `step_completed` is sent once, after the last item. Its `iterations` lists every item, each with `iteration` and the same fields as a step (`name`, `success`, `failure`, `status`, `assertions`, `request`, `response`...).
  - The event's own fields are those of the first failing item, or of the last one when all passed, so a tool that does not read `iterations` still shows the right result.
  - `iterations` is `null` for other steps, and `[]` for a `for_each` with no items.
  - The summary counts each item as a step. The MCP `run` tool lists steps the same way.
- `assertions[].actual` is the actual value as JSON text, or `(missing)`.
- `http_req_duration_ms` is `null` when no request got a response.

**Secrets.** Requests are shown as sent, `Authorization` headers and passwords included. The output is meant for a program on your machine: do not store it or publish it as it is.

## Suites: a folder or several files

`tinyprobe run` with a folder, or with more than one file, runs a suite. The events of each workflow are the ones above, unchanged, and two events wrap them:

| Event | When |
|---|---|
| `suite_started` | every workflow is valid; the first one is about to run |
| `suite_finished` | the last workflow ended; the last event of the suite |

```json
{"event":"suite_started","protocol":1,"version":"0.12.0","files":["/home/me/project/tinyprobe/workflows/auth.yaml","/home/me/project/tinyprobe/workflows/users.yaml"]}
{"event":"run_started", ...}
{"event":"run_finished", ...}
{"event":"run_started", ...}
{"event":"run_finished", ...}
{"event":"suite_finished","success":false,"workflows":[{"file":"/home/me/project/tinyprobe/workflows/auth.yaml","success":true},{"file":"/home/me/project/tinyprobe/workflows/users.yaml","success":false}],"summary":{"total_workflows":2,"passed_workflows":1,"failed_workflows":1,"total_steps":19,"passed_steps":10,"failed_steps":1,"not_run_steps":8,"duration_ms":1430.2}}
```

- `files` are full paths, in the order the workflows run: files of a folder in ordinal order of their path, files given on the command line in the order given.
- A failing workflow does not stop the suite: the next one starts with its own `run_started`.
- If any workflow is invalid, nothing runs and the only event is `invalid`, with the problems of every file; its `file` is the first path of the command line.
- With a single workflow file there are no suite events: the output is that of a single run.

## Variables from the command line

`--var name=value` sets a variable over the environment file, for `run` and `call`. It can be repeated. The value is typed like a YAML value: `--var id=42` is a number, `--var 'id="42"'` a string, `--var 'tags=[a, b]'` a list, and `--var name=` an empty string. Placeholders are not resolved, as in an environment file.

A UI that lets people change the variables of a workflow passes them with `--var` and never rewrites the YAML files.

## The file as written

`tinyprobe inspect <file> --json --source` adds two keys to the document, for editors that change a file with forms (since 0.21.0): `source`, the file as written, and `blocks`, where its parts are. For this file:

```yaml
base_url: ${baseUrl}
auth:
  bearer:
    Authorization: "Bearer ${token}"

endpoints:
  login:
    method: POST
    path: /login
    body: { email: "${email}", password: "${password}" }
  get_user:
    path: /users/${id}
    auth: bearer
    headers:
      X-Client: app
```

```json
"source": {
  "base_url": "${baseUrl}",
  "auth": {
    "bearer": { "Authorization": "Bearer ${token}" }
  },
  "endpoints": {
    "login": { "method": "POST", "path": "/login", "body": { "email": "${email}", "password": "${password}" } },
    "get_user": { "path": "/users/${id}", "auth": "bearer", "headers": { "X-Client": "app" } }
  }
},
"blocks": [
  { "kind": "key", "name": "base_url", "start": { "line": 1, "column": 1 }, "end": { "line": 1, "column": 21 } },
  { "kind": "key", "name": "auth", "start": { "line": 2, "column": 1 }, "end": { "line": 4, "column": 37 } },
  { "kind": "auth", "name": "bearer", "start": { "line": 3, "column": 3 }, "end": { "line": 4, "column": 37 } },
  { "kind": "key", "name": "endpoints", "start": { "line": 6, "column": 1 }, "end": { "line": 15, "column": 20 } },
  { "kind": "endpoint", "name": "login", "start": { "line": 7, "column": 3 }, "end": { "line": 10, "column": 57 } },
  { "kind": "endpoint", "name": "get_user", "start": { "line": 11, "column": 3 }, "end": { "line": 15, "column": 20 } }
]
```

- **`source`** is the inspected file as written, as JSON: mappings become objects with their keys in order, lists become arrays, and scalars are typed as YAML reads them (`42` a number, `true` a boolean, `~` and an empty value `null`, `"42"` a string). Placeholders are not resolved. Comments are not in it, nor the imported files: inspect them for theirs.
- **`blocks`** are where things are written, in the order of the file:
  - `key`: every top-level key (`name`, `base_url`, `headers`, `auth`, `endpoints`, `import`, `variables`, `steps`, `sequence`, `params`...), with its `name`;
  - `auth`: each auth set, with its `name`;
  - `endpoint`: each endpoint written in the file, with its `name`;
  - `step`: each item of `steps` (or of `sequence` in a sequence file), `use` and `sleep` steps included, with its `index` in that list from 0. It is the index in `source.steps`, not the `index` of `steps`, which counts the steps of sequences where their `use` is.
- **`start`** is where a block begins: the key of an entry (`login:`), or the `-` of a list item. **`end`** is just after the last character of its value: the comments and blank lines that follow belong to what comes next. Both are 1-based `{ line, column }` in the inspected file. An editor replaces a block by replacing the text from `start` to `end`.
- An invalid file also has `source` and `blocks` when its YAML can be read, so an editor can help fix it; a file whose YAML cannot be read has neither.
- Without `--source` the document has neither key. The MCP tool `inspect` does not add them.

## `call`

`tinyprobe call <file.yaml> <endpoint> [--with name=value]...` runs a workflow of one step that calls the endpoint and accepts **any** status. `<file.yaml>` is an API file or a workflow; with a workflow, its variables and settings apply. `--with` values are typed like `--var`, and they can use variables: `--with 'id=${userId}'`. They are validated like a step's `with`: an unknown endpoint, a missing required param or an unknown argument is an `invalid` event with the file `command line`.

## MCP

`tinyprobe mcp` is an [MCP](https://modelcontextprotocol.io) server: JSON-RPC 2.0, one message per line, on standard input and output. It speaks the protocol versions `2025-06-18`, `2025-03-26` and `2024-11-05`, and offers tools only. It keeps nothing between calls: each one loads the files again. Standard output carries only MCP messages.

| Tool | Arguments | Result |
|---|---|---|
| `guide` | none | the guide for agents, as text |
| `inspect` | `file` | the JSON of `inspect --json` |
| `call` | `file`, `endpoint`, `with`, `env_file`, `vars` | `endpoint`, then the keys of a `step_completed` event: `success`, `status`, `assertions`, `request`, `response`... |
| `run` | `paths`, `env_file`, `vars`, `to` | `success`, and `workflows`: for each, `file`, `workflow`, `success`, `summary` (as in `run_finished`) and `steps` |

- `with` and `vars` are JSON objects; their values keep their JSON type. `paths` is a list of workflow files and folders (one string is accepted too), read like `tinyprobe run`.
- `to` (optional) is `--to`: a step number (as `inspect` numbers the steps, 1-based) or a step name. It needs one path, a workflow file; the run stops after that step, and `summary.stopped_after` is its index.
- The result is the JSON as the text content of the tool result.
- In `run`, passing steps have no `request` and `response`; failed steps have both.
- `call` accepts any status, like `tinyprobe call`: a `500` is a result, not an error.
- **Tool errors** (`isError: true`): an invalid file gives the JSON of an invalid `inspect` (problems of the arguments have the file `command line`); a wrong argument gives one sentence, such as `File not found: tinyprobe/api/backend.yml`. A failing step is not a tool error: the result has `success: false`.
- **JSON-RPC errors**: `-32700` for a message that is not JSON, `-32600` for an invalid request, `-32601` for an unknown method, `-32602` for an unknown tool.
- `guide` and `inspect` have `readOnlyHint: true`; `call` and `run` have `openWorldHint: true`, because they send requests to the backend.

## Schema

`tinyprobe schema` prints a [JSON Schema](https://json-schema.org) (draft 2020-12) of API files and workflows: the keys of every section, with a description each, their types, and the check operators. It is built from the keys the loader accepts, and the same text is in the repository as [`docs/schema/tinyprobe.schema.json`](schema/tinyprobe.schema.json) and in every release as an asset.

- One schema for both kinds of file: a workflow has every key of an API file, plus its own.
- `$defs` has `endpoint`, `step`, `expect`, `until`, `settings`, `condition` (the operators, `matches` and `schema` included), `duration_condition` and `check` (conditions, or a value to compare).
- The schema helps people type; it is not the validator. Imports, endpoint names, params, placeholders and JSONPath are checked only by `tinyprobe inspect` and `run`.

## JUnit report

`tinyprobe run … --junit <file>` also writes a JUnit XML report, for CI servers, when the run ends; the console output or the ndjson events don't change. It is not available for `call`.

- `testsuites` has the totals: `tests`, `failures`, `errors`, `skipped`, `time` (seconds).
- One `testsuite` per workflow: `name` (the workflow's name), `file`, the counts, `time`, `timestamp` (UTC), and the property `tinyprobe.version`.
- One `testcase` per call step (sleeps are not tests): `name` (the step's name), `classname` (the workflow's name), `file`, `line` of the step, `time`.
- A step whose response did not pass has a `failure`, with `type` `status`, `check` or `step` (for example a value that could not be saved). A step without a response (network error, timeout, undefined variable) has an `error` with `type` `request`. Their `message` is the first reason; their text is the request line, the status and every check with its actual value.
- Steps after a failing one have `skipped` with the message `Not run: an earlier step failed`.
- When files are invalid (exit `2`), the report has one `testsuite` per invalid file with a single `testcase` named `load`, whose `error` (`type` `invalid`) lists every problem.
- No headers and no bodies are written.
- `file` and `line` on `testcase`, and `skipped` on `testsuites`, are extensions that GitLab and the GitHub test-report actions read; apart from them, the report matches the JUnit schema of the Jenkins xUnit plugin.

