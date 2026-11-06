# tinyprobe guide for coding agents

You are going to write API tests for a backend with **tinyprobe**, a command-line API testing tool. This guide is everything you need: the file format, the rules that are easy to get wrong, a procedure to follow and a complete example. Follow it exactly; do not use syntax that is not described here.

> This guide matches the installed version when read with `tinyprobe guide`. Check that tinyprobe is installed with `tinyprobe --version`; if it is not, tell the user to install it: https://github.com/kikutano/tinyprobe#install

## 1. How tinyprobe works

- **API files** declare the backend's endpoints once: method, path, query, headers, auth, body and parameters. No expectations and no `save`: an API file describes requests and never runs or stores anything.
- **Workflow files** `import` API files and run **steps** in order. A step `call`s an endpoint `with` arguments, checks the response with `expect` and stores values with `save` for the next steps. Every value that moves from one request to the next comes from a `save` in the same workflow.
- **Environment files** hold per-environment values (base URL, credentials). Pass one with `-e`.
- `tinyprobe run <workflow.yaml> -e <env.yaml>` validates the whole workflow and its imports, then runs it and stops at the first failing step.
- `tinyprobe run <folder> -e <env.yaml>` runs every workflow of the folder and its subfolders (API and env files are skipped), each from zero, also after a failing one, and ends with a summary. It validates every file before the first request. Exit `0` means every workflow passed.
- `tinyprobe inspect <file.yaml>` validates an API or workflow file without sending anything, and lists its endpoints and steps.
- `tinyprobe call <api.yaml> <endpoint> --with name=value -e <env.yaml>` sends one request and prints the whole response (status, headers, body), whatever the status. Its line starts with `→`, not with a verdict: read the status (`HTTP 401 Unauthorized`), since a call checks nothing; a `✗` means no response arrived. Use it to see the real shape of a response before writing checks. It remembers nothing: a token for a protected endpoint is passed with `--with` or `--var`.

Exit codes: `0` all steps passed, `1` a step failed, `2` invalid workflow or arguments (nothing was sent).

**If you have the tinyprobe MCP tools** (`guide`, `inspect`, `call`, `run`, from `tinyprobe mcp`), use them instead of the shell. They take JSON arguments, so values keep their JSON type (`"with": { "id": 42 }` is a number, `{ "id": "42" }` a string), and return JSON: `inspect` the file you wrote, `call` an endpoint with `file`, `endpoint`, `with` and `env_file`, `run` with `paths` (files or folders) and `env_file`. An invalid file comes back as a tool error with every problem and its `file:line:column`.

## 2. Project layout

Create the files in a `tinyprobe/` folder at the root of the user's repository, unless the user asks otherwise:

```text
tinyprobe/
├── api/
│   └── backend.yaml          # all endpoints of the backend (split by area if it is large)
├── sequences/
│   └── log-in.yaml           # steps shared by several workflows (a login), run with `use:`
├── workflows/
│   ├── smoke.yaml            # one workflow per feature or resource
│   ├── auth.yaml
│   └── orders.yaml
├── fixtures/                 # small files that workflows upload (multipart)
├── data/                     # data files of for_each steps (CSV or JSON), if any
├── env.example.yaml          # committed: every variable, with placeholder values
└── env.local.yaml            # NOT committed: real values for the local backend
```

Add `tinyprobe/env.*.yaml` to `.gitignore`, except `env.example.yaml`.

If `tinyprobe/` does not exist yet, run `tinyprobe init` first (`--base-url http://localhost:8080` sets `baseUrl` in the env files): it creates these folders and env files, the `.gitignore` lines, and a section in `AGENTS.md` for the next agent. It never overwrites a file.

## 3. API file

```yaml
base_url: ${baseUrl}              # prepended to every path of this file

headers:                          # optional: sent by every endpoint of this file
  Accept: application/json

auth:                             # optional: named auth sets, picked per endpoint
  bearer:
    Authorization: "Bearer ${token}"
  apikey:
    X-API-Key: ${apiKey}
  admin:
    basic:                        # Basic auth: tinyprobe computes the base64
      username: ${adminUser}
      password: ${adminPassword}
  service:
    oauth2:                       # OAuth2 client credentials: tinyprobe gets and reuses the token
      token_url: ${authUrl}/oauth/token
      client_id: ${clientId}
      client_secret: ${clientSecret}
      scope: [orders:read]        # optional

endpoints:
  get_order:                      # the name a step calls: snake_case, unique
    path: /api/orders/${id}       # path parameters are placeholders
    params:
      id:                         # declare path placeholders as required params
    auth: bearer

  list_orders:
    path: /api/orders
    query: [status, page, pageSize]   # each is sent only when the step passes it
    auth: bearer

  create_order:
    method: POST                  # default GET
    path: /api/orders
    auth: [bearer, apikey]        # one auth set or a list
    params:                       # optional: declared arguments
      quantity: 1                 #   with a default
      productId:                  #   no value: required
    body:                         # a YAML value: sent as JSON
      productId: ${productId}
      quantity: ${quantity}

  upload_invoice:
    method: POST
    path: /api/orders/${id}/invoice
    auth: bearer
    params:
      id:
      file:                       # the path of the file, passed by the step
    multipart:                    # multipart/form-data: text fields and files
      note: Uploaded by tinyprobe
      invoice:
        file: ${file}             # relative to the workflow that runs

  login_form:
    method: POST
    path: /login
    params: { username:, password: }
    form:                         # application/x-www-form-urlencoded
      username: ${username}
      password: ${password}
```

| Endpoint key | Meaning |
|---|---|
| `method` | HTTP verb, default `GET`. |
| `path` | Required. Absolute (`https://...`) or relative to `base_url`. |
| `query` | List of query parameter names. A name is added to the URL only if the step passes it in `with` (or it is a declared param with a default). |
| `headers` | Extra headers. Later headers override earlier ones: file `headers`, then `auth` sets, then endpoint `headers`. |
| `auth` | Name of an auth set of **the same file**, or a list of names. |
| `body` | A YAML mapping or list: sent as JSON. A string (e.g. a `\|` block): sent as is, `text/plain` unless a `Content-Type` header says otherwise. A single placeholder (`body: ${payload}`): the whole body is an argument. |
| `form` | A form body (`application/x-www-form-urlencoded`), instead of `body`: `field: value`, encoded by tinyprobe (`a&b` is sent as `a%26b`); a list sends the field once per value. tinyprobe sets the `Content-Type`: don't add the header. |
| `multipart` | A `multipart/form-data` body (file uploads), instead of `body`: `field: value` for a text field, `field: { file: path }` for a file (`filename` and `content_type` are optional: by default the file's name and the type of its extension), a list for several values or files under one name. **The path is relative to the workflow that runs** (to the API file for `tinyprobe call`), like `import`, not to the current directory: a file in `tinyprobe/fixtures/` is `../fixtures/avatar.png` from `tinyprobe/workflows/` and from `tinyprobe/api/` alike, so `tinyprobe call … --with file=../fixtures/avatar.png` and the workflow use the same path. Pass the path as an argument, as in `upload_invoice`. tinyprobe sets the `Content-Type` and its boundary: don't add the header. |
| `params` | Declared arguments: `name: default` or `name:` (required). **Declare every path placeholder and every required input as a required param**: a step that forgets it is then reported before running, instead of failing at run time. Placeholders of `auth` sets and `base_url` (such as `${token}`, `${baseUrl}`) are not params: do not declare them. |

**Auth sets.** A set is headers (`Authorization: "Bearer ${token}"`, `X-API-Key: ${apiKey}`), plus at most one of two keys that are not headers:

- `basic: { username, password }`: tinyprobe sends `Authorization: Basic` with the base64. Never compute base64 yourself, and never put it in an env file.
- `oauth2: { token_url, client_id, client_secret, scope, client_auth, form }`, the client credentials grant: tinyprobe requests a token from `token_url` before the first request that needs it, reuses it for the run until it expires, and sends `Authorization: Bearer <token>`. `client_auth` is `basic` (the default: the client in an `Authorization: Basic` header of the token request) or `form` (the client in the form body). `form` adds fields to the token request (`audience`, `resource`). `tinyprobe call` of such an endpoint works with only the env file: no token step, no `--with token=`.
- Next to `basic` or `oauth2`, no `Authorization` header: not in the set, not in another set of the endpoint, not in the endpoint's `headers` (it is an error).
- Secrets (`password`, `client_secret`) are placeholders, with the values only in `env.local.yaml`.

## 4. Workflow file

```yaml
name: Orders                      # shown in the report
description: Create, read and delete an order.
import:
  - ../api/backend.yaml           # relative to this file

variables:                        # optional defaults; the env file overrides them
  pageSize: 10

settings:                         # optional
  timeout: 10                     # seconds per request (default 30)
  max_retries: 0                  # retries on network errors and timeouts only (default 0)

steps:
  - name: Log in                  # optional; default: the call plus its path/query arguments
    call: login
    with:
      email: ${email}
      password: ${password}
    expect:
      status: 200
    save:
      token: $.accessToken        # stored after every check passed; usable as ${token}

  - sleep: 200ms                  # a pause: ms, s or m
```

A workflow may also contain `base_url`, `headers`, `auth` and `endpoints` itself, for small single-file tests.

**Several services.** When the backend is made of several services (microservices, a gateway, a third-party API), write one API file per service, each with its own `base_url` (`${usersUrl}`, `${billingUrl}`). If two files have endpoints with the same name, import them with a name and call `name.endpoint`:

```yaml
import:
  - ../api/shop.yaml                # create_order, list_products...
  - billing: ../api/billing.yaml    # billing.login, billing.create_invoice...
```

Never rename endpoints (`billing_login`) to avoid a clash: the API file should read like the service it describes.

### Steps

| Step key | Meaning |
|---|---|
| `call` | Endpoint name. Required, unless the step is a `use` or a `sleep`. |
| `use` | A step of its own: `use: ../sequences/log-in.yaml` with `with` for its params. The steps of the sequence run here: see section 6e. |
| `with` | Arguments: values for the endpoint's placeholders. May use variables: `id: ${orderId}`. An argument the endpoint does not use (not a param, query name or placeholder of its path, headers, auth sets, base URL or body) is an error, so typos such as `pagesize` are caught. Query values are URL-encoded; `true`, `42` are sent as written. |
| `expect.status` | A code (`201`) or a list (`[200, 204]`). **Without it, any 2xx is accepted and anything else fails the step.** |
| `expect.headers` | Checks on response headers, keyed by header name (case-insensitive). Every operator works (`exists`, `in`, `any`, `ne`, `contains`, numeric ones). Header values are text and are compared as text, so quoting the expected value makes no difference: `X-Count: 3` and `X-Count: "3"` are the same check. |
| `expect.cookies` | Checks on cookies, keyed by cookie name (case-sensitive), in the workflow's cookie jar after the response: see section 6c. |
| `expect.body` | Checks on the JSON body, keyed by JSONPath. |
| `expect.duration` | Response time: `{ lt: 500ms }`. Only `gt`, `gte`, `lt`, `lte`. |
| `for_each` | Repeat the call once per item, each item's keys being arguments: see section 6d. |
| `until` | Repeat the call until conditions pass (polling): see section 6b. |
| `save` | `variable: <JSONPath>`. A path that matches nothing fails the step. `variable: { cookie: name }` saves a cookie's value (section 6c). |
| `sleep` | `sleep: 500ms`. A sleep step has no `call`, `with`, `expect`, `until` or `save`. Durations use `ms`, `s`, `m`, `h`, `d`. |

## 5. Checks

```yaml
expect:
  status: 200
  headers:
    Content-Type: { contains: json }
  body:
    $.id: ${orderId}                          # plain value: equality
    $.status: { in: [pending, paid] }         # one of these values
    $.total: { gt: 0, lt: 1000 }              # several operators: ALL must pass
    $.tags: { contains: urgent }              # array contains this element
    $.customer.email: { contains: "@" }       # string contains this text
    $.number: { matches: '^ORD-[0-9]+$' }     # a regular expression, in single quotes
    $: { schema: ../../openapi.yaml#/components/schemas/Order }   # the whole body against a JSON Schema
    $.customer.id: { schema: { type: integer } }   # the type of a value
    $.discount: { any: [ null, { gte: 0 } ] } # at least ONE alternative must pass
    $.deletedAt: { exists: false }            # the path is absent
    $.createdAt: { exists: true }             # present, any value (use for timestamps, generated ids)
    $.address: { eq: { city: Rome, zip: "00100" } } # a whole object: every field must match, no extra fields
  duration: { lt: 2s }
```

| Operator | Passes when the actual value |
|---|---|
| *(plain value)* | equals it |
| `eq` | equals it (use to compare a whole object, or to combine equality with other operators) |
| `ne` | does not equal it (also passes when the path is missing) |
| `gt`, `gte`, `lt`, `lte` | is a number greater / less than it |
| `exists` | `true`: the path exists (even if `null`); `false`: it does not |
| `contains` | is an array with an element **exactly equal** to it (objects: same fields, same values), or a string containing it as text |
| `in` | equals one of the values of a list |
| `any` | satisfies at least one alternative of a list; an alternative is a value or a mapping of operators |
| `matches` | is text (or a number or boolean, as its text) in which the regular expression finds a match |
| `schema` | is valid against a JSON Schema: a file (`path` or `path#/pointer`, relative to the workflow) or a schema written in place; `$` is the whole body |

Rules:

- **Equality is typed.** `$.id: 1` matches the number `1`, not the string `"1"`. Numbers compare by value (`9.9` equals `9.90`).
- **A mapping is a list of operators only if every key is an operator.** `{ gte: 1, name: x }` is an error; use `eq` to compare an object.
- **Never repeat a JSONPath key** in the same `body` (YAML forbids duplicate keys). Put all conditions in one mapping: `$.total: { gt: 0, lt: 1000 }`.
- **Every check of a step is evaluated and reported**, not only the first failure.
- **Arrays compare in order**: `$.tags: [a, b]` does not match `["b", "a"]`. Use `contains` for membership.
- **A path past the end of an array is missing**: with two items, `$.items[2]: { exists: false }` passes.
- **`contains` on a string compares text**: `$.message: { contains: 404 }` looks for the text `404`.
- **`matches` takes a regular expression in single quotes** (`'^\d+$'`: in single-quoted YAML a backslash is a plain character). It searches the value, like JavaScript: anchor with `^` and `$` to match all of it. Flags go at the start: `(?i)` ignores case, `(?m)` makes `^ $` match at each line. It works on body values, headers and cookies, in `expect` and `until`. `null`, objects and arrays never match.
- **`schema` checks the contract of a value**: types, required fields, no extra fields, formats. Point at the schema where it already is (`../../openapi.yaml#/components/schemas/Order`, a file in `tinyprobe/schemas/`); write one in place only for a type or a one-line rule (`{ type: string }`). It works on body values only, in `expect` and `until`; each broken rule is a failed check at the JSONPath of the value. JSON Schema drafts 4 to 2020-12 and OpenAPI 3.0/3.1 schemas; `if`/`then`/`else`, `propertyNames`, `unevaluated*`, `dependent*` and `$ref` to URLs are errors.
- **Body checks and `save` need a JSON body.** On a response without one (a `204`, or an error with an empty body) they fail with `The response body is not valid JSON`: check only `status` (and headers) there.

### JSONPath subset

Only these forms are supported:

| Form | Example |
|---|---|
| root | `$` |
| property | `$.user.email`, `$.X-Request-Id` |
| array index | `$.items[0].sku`, `$[2]`, `$.matrix[1][0]` |

**Not supported:** wildcards (`[*]`, `.*`), filters (`[?(@.x)]`), recursive descent (`..`), bracket names (`['a b']`), negative indexes, slices, functions such as `length()`. To check an item of a list, address it by index (`$.items[0].sku`). `contains` on an array works for simple values (`$.tags: { contains: urgent }`) or whole, exactly equal objects.

## 6. Variables and types

Placeholders are `${name}`. Endpoints behave like functions:

- **Declared `params` are local**: they come from the step's `with` or from their default, never from other variables.
- **Any other placeholder** is resolved from, in order: the step's `with`, values stored with `save`, the environment file, the workflow's `variables`.
- An undefined variable fails the step before the request is sent.
- A step can override a saved value for itself only: `with: { token: invalid-token }` sends a wrong token in that step, and the next steps use the saved one again. This works on any endpoint, also on one that declares `params`: `token` is not a param, so `with` can set it.

YAML types matter in bodies and checks:

- Plain scalars are typed: `true`, `false`, `null`, `42`, `9.9`. **Quote values that must stay strings**: `zip: "00100"`, `version: "1.0"`, `phone: "0612345678"`.
- A plain scalar made of **one** placeholder keeps the variable's type: `quantity: ${qty}` with `qty: 2` sends `2`; `active: ${flag}` sends `true` or `false`.
- A placeholder inside a longer text, or inside quotes, produces a string: `name: "user-${n}"`, `id: "${id}"`.
- **Inside `{ }` or `[ ]` (flow style) a placeholder must be quoted**: in `{ id: ${id} }` YAML reads `{` as the start of another mapping and the file is invalid. Write `{ id: "${id}" }` (a string), or use block style (one key per line) when the type matters.

## 6b. Polling and generated values

**Asynchronous operations: use `until`.** When the backend finishes the work later (a job, a queue consumer, a webhook), repeat the call until the result is there. Never use a fixed `sleep` for this.

```yaml
- name: Booking is confirmed
  call: get_booking
  with:
    id: ${bookingId}
  until:                            # same status / headers / cookies / body checks as expect
    body:
      $.status: { in: [Confirmed, Rejected] }   # stop waiting on any final state
    every: 1s                       # default 1s
    timeout: 30s                    # default 30s
  expect:                           # evaluated on the last response
    body:
      $.status: Confirmed
```

- While the `until` conditions fail, the step waits `every` and sends the same request again; any status is accepted meanwhile unless `until.status` is set (`until: { status: 200 }` waits for a resource to appear).
- Stop waiting on **every** final state (`Confirmed` or `Rejected`), then check the one you expect in `expect`: the failure then says "Rejected" at once instead of timing out.
- After `timeout` the step fails with `Gave up after N attempts` and the last values.

**Unique values: generated placeholders start with `$`.** `${$uuid}`, `${$timestamp}` (Unix seconds, a number), `${$randomInt}`, `${$randomString}` (8 characters), `${$now}` (UTC, `2026-10-16T09:41:00Z`), `${$now+2h}`, `${$now-30m}`, `${$today}` (`2026-10-16`), `${$today+1d}`.

- **Every use is a new value.** To reuse one (create with an email, then search it), define it in `variables`: a variable is resolved once per run.
- Use them for fields that must be unique on every run (emails, codes, names) and for dates in the future (`pickupAt: ${$now+2h}`).
- For resources that cannot be deleted, prefer a **fixed** name plus "create, accept `409`, find by `search`" when the backend has a search: it keeps the data from growing on every run. Use generated values when there is no way to find an existing resource again.

```yaml
variables:
  noteTitle: "tinyprobe note ${$randomString}"   # one value for the whole run
  remindAt: ${$now+1d}
```

## 6c. Cookies

**Cookies are automatic.** Each workflow run has its own cookie jar, empty at the start: a `Set-Cookie` is stored (redirects included) and sent with every later request whose URL matches. A session kept by a cookie needs nothing in the API file: log in as a step, and the next steps are logged in. Each workflow logs in itself; `tinyprobe call` keeps no cookies between calls.

```yaml
- name: Log in
  call: login                     # the server answers with Set-Cookie: sid=...
  with: { email: "${email}", password: "${password}" }
  expect:
    cookies:
      sid: { exists: true }       # in the jar after this response
  save:
    csrf: { cookie: XSRF-TOKEN }  # a cookie's value, for an X-XSRF-TOKEN header

- name: Create a note
  call: create_note               # its API file sends X-XSRF-TOKEN: ${csrf}, with csrf in params
  with: { csrf: "${csrf}", title: Hello }
  expect: { status: 201 }

- name: Log out
  call: logout
  expect:
    status: 204
    cookies:
      sid: { exists: false }      # removed by the server
```

- `expect.cookies` (and `until.cookies`) take the same operators as headers; values are text. A name refers to the jar **after** the response, for the request's URL: `{ exists: false }` passes after a logout and when the cookie was never set.
- `save: { x: { cookie: name } }` stores the value only (no attributes). A missing cookie fails the step.
- **Never write a cookie's value into a file**, and never copy `Set-Cookie` into a `Cookie` header by hand: the jar sends it. A `Cookie` header in the API file is only for a cookie the server never sets (a fixed preference, a cookie parameter), and is sent before the jar's cookies.
- CSRF: when the API wants a cookie copied into a header (`XSRF-TOKEN` → `X-XSRF-TOKEN`, Django's `csrftoken` → `X-CSRFToken`), declare the header with a param in the API file and `save` the cookie in the workflow.

## 6d. Tables of cases: `for_each`

**One step, many inputs.** When the same call must be checked with several values (validation errors, roles, every item of a list), write one step with `for_each` instead of copying the step.

```yaml
- name: Invalid orders are rejected
  call: create_order
  for_each:                         # one call per item; its keys are arguments, like with
    - { productId: "", error: product_required }
    - { productId: p-1, quantity: 0, error: quantity_positive }
    - { productId: p-1, quantity: -3, error: quantity_positive }
  expect:
    status: 422
    body:
      $.code: ${error}              # item values are variables of the step

- name: Roles
  call: admin_stats
  for_each: { file: ../data/roles.csv }   # role,status: admin,200 / viewer,403
  expect:
    status: ${status}               # only in a for_each step: the status may come from the item

- name: Every listed order can be read
  call: get_order
  for_each: ${orders}               # a list saved earlier: save: { orders: $.items }
```

- An item's keys must be arguments of the endpoint or variables used by the step (in `expect`, `until` or `with`). A key in both an item and `with` is an error: put shared values in `with`, varying ones in the items.
- Every item runs, also after a failing one; then the workflow stops if any failed.
- No `save` in a `for_each` step. Use `for_each` for checks; a case that creates, reads and deletes is a sequence run with `for_each` on its `use` (section 6e).
- Data files go in `tinyprobe/data/`. CSV values are text; JSON keeps types.
- Use `for_each` only when the steps differ by values. Different endpoints, different checks or a `save` mean different steps.

## 6e. Shared steps: sequences and `use`

**Write the login once.** When two workflows or more need the same steps (a login, a shared setup), put them in a sequence file in `tinyprobe/sequences/` and start each workflow with `use`. Every workflow still logs in itself: through the sequence.

```yaml
# file: tinyprobe/sequences/log-in.yaml
name: Log in
import:
  - ../api/backend.yaml           # the endpoints it calls: a sequence imports them itself
params:
  email: ${adminEmail}            # a default may use the env file
  password:                       # required: every use passes it
sequence:                         # not `steps:`
  - name: Log in
    call: login
    with: { email: "${email}", password: "${password}" }
    expect:
      status: 200
    save:
      token: $.accessToken        # visible to the workflow's next steps
```

```yaml
# file: tinyprobe/workflows/sequences-demo.yaml
name: Notes with the shared login
import:
  - ../api/backend.yaml
steps:
  - use: ../sequences/log-in.yaml
    with:
      password: ${adminPassword}
  - name: Notes of the user
    call: list_notes
    expect:
      status: 200
```

- A param is a variable inside the sequence only. Its `save` values are visible after the `use`.
- Check a sequence with `tinyprobe inspect tinyprobe/sequences/log-in.yaml`. `tinyprobe run` does not run it on its own; `tinyprobe run tinyprobe/workflows` skips sequence files anyway.
- Write a sequence only for steps used by two workflows or more. A step used once stays in its workflow.
- The auth workflow that tests the login itself (wrong password, missing token) calls `login` directly: those steps are the test, not a setup.

**A case of several steps, once per item.** When each case creates something, reads it back and deletes it, write those steps in a sequence and give its `use` a `for_each`. Each item's keys are the sequence's params; each item's steps save and read their own values.

```yaml
# file: tinyprobe/sequences/note-lifecycle.yaml
name: Note lifecycle
import:
  - ../api/backend.yaml
params:
  title:
  pinned: false
sequence:
  - name: Create
    call: create_note
    with: { title: "${title}", pinned: "${pinned}" }
    expect: { status: 201 }
    save: { noteId: $.id }        # each item saves its own
  - name: Read it back
    call: get_note
    with: { id: "${noteId}" }
    expect:
      body:
        $.title: ${title}
  - name: Delete
    call: delete_note
    with: { id: "${noteId}" }
    expect: { status: 204 }
```

```yaml
# file: tinyprobe/workflows/note-cases.yaml
name: Notes, one case per item
import:
  - ../api/backend.yaml
steps:
  - use: ../sequences/log-in.yaml
    with: { password: "${adminPassword}" }
  - use: ../sequences/note-lifecycle.yaml
    for_each:                     # the sequence once per item; the keys are its params
      - { title: First }
      - { title: Pinned, pinned: true }
```

- The items are a written list or `{ file: ../data/notes.csv }`. Never a variable (`for_each: ${notes}`) on a `use`: it is an error, since a sequence's steps are laid out before the workflow runs. To repeat one call over a saved list, use `for_each: ${notes}` on a call step.
- The workflow stops at the first failing step: the items after it don't run.

## 7. Environment file

```yaml
# file: tinyprobe/env.example.yaml
baseUrl: http://localhost:8080
email: user@example.com
password: change-me
apiKey: change-me
```

A flat map of variable names to values. Put here everything that changes between environments and **every secret**. Workflows reference them with `${...}`; never write real passwords, tokens or keys in API or workflow files.

## 8. Procedure

Follow these steps in order. If the user gives you a Postman collection or the exports of another API tool, follow section 12 instead.

1. **Find the endpoints.** Prefer an OpenAPI/Swagger document if the project has one (mapping in section 12.5). Otherwise read the route definitions, for example:
   - ASP.NET: `app.MapGet/MapPost(...)`, `[HttpGet]`, `[Route]` on controllers
   - Express / Fastify / NestJS: `app.get(...)`, `router.post(...)`, `@Get()`, `@Post()`
   - FastAPI / Flask / Django: `@app.get`, `@router.post`, `@app.route`, `urls.py`
   - Spring: `@GetMapping`, `@PostMapping`, `@RequestMapping`
   - Rails: `config/routes.rb`; Laravel: `routes/api.php`; Go: `http.HandleFunc`, `r.GET`

   For each endpoint note: method, path and path parameters, query parameters, request body fields and validation rules, whether it needs authentication, status codes and response body shape.
2. **Understand authentication**: how a client obtains a token (login endpoint, the field that contains it), which header carries it, API keys, Basic auth, OAuth2 client credentials (a token URL with a client id and secret). Model each mechanism as an `auth` set: a header, `basic` or `oauth2`.
3. **Find real test data**: seed data, fixtures, migrations, test users, `.env.example`, `docker-compose.yml` (base URL and port). Use values that exist in the backend; never invent ids, emails or expected values. If credentials or the base URL cannot be found, ask the user.
4. **Write the API file** with every endpoint you found. Check it with `tinyprobe inspect tinyprobe/api/<name>.yaml` (exit `0` means valid), then try the main endpoints with `tinyprobe call`, for example `tinyprobe call tinyprobe/api/backend.yaml get_note --with id=1 -e tinyprobe/env.local.yaml`: the checks you write next should match the responses you saw, not the ones you expect. An endpoint that needs a token gets it with `--with token=...` or `--var token=...`, from a `call` of the login endpoint.
5. **Write the workflows**, one per feature or resource:
   - a **smoke** workflow: the main read endpoints answer as expected;
   - **auth**: valid login, wrong password (`401`), invalid token (`401`/`403`, with `with: { token: invalid-token }`), and no token at all. A request without the `Authorization` header needs an endpoint without `auth` (e.g. `me_anonymous`): that is a different request, not a variant, so it is the one case where a second endpoint for the same route is right;
   - asynchronous results (status set by a job, a webhook, a queue): poll them with `until` (section 6b);
   - for each resource: **list and read** (including filters and pagination), **create** with valid data, **validation errors** (`400`/`422`, check the error field), **not found** (`404`), and the **full lifecycle** of a resource the workflow creates itself: create, save its id, read, update, delete, verify it is gone.
6. **Write `env.example.yaml`**, and `env.local.yaml` with the local values if you know them.
7. **Run the workflows**: one with `tinyprobe run tinyprobe/workflows/<name>.yaml -e tinyprobe/env.local.yaml` while you write it (add `--to <n>` to stop after step `n`, as `inspect` numbers them, and `-v` to see its request and response, when you write the checks of a step in the middle of a flow; add `--skip <n>`, repeatable, to leave out a step for that run, such as one that deletes what you want to look at), then all of them with `tinyprobe run tinyprobe/workflows -e tinyprobe/env.local.yaml`. You are done when the whole folder exits `0`, or the remaining failures are backend bugs you report. The backend must be running locally; start it if you can, otherwise ask the user to.
   - Exit `2`: the YAML is invalid. Every problem is listed as `file:line:column: message`, with suggestions. Fix them and run again.
   - Exit `1`: a check failed. The report shows each check with its actual value. Decide whether **your test** is wrong (fix it) or **the backend** does not do what its code or documentation says (keep the test and report it to the user). Never weaken a check only to make it pass.
8. **Report to the user**: the files you created, what each workflow covers, the endpoints you did not cover and why, and any failure that looks like a backend bug.

When you write a CI job for the tests: on GitHub Actions use `uses: kikutano/tinyprobe@v<version>` with `path`, `env-file` and `vars` (credentials from `${{ secrets.NAME }}`); on GitLab CI and other CIs that run jobs in a container use the image `ghcr.io/kikutano/tinyprobe:<version>` (on GitLab with `entrypoint: [""]`), or install a pinned version (`TINYPROBE_VERSION`); run the folder with `--junit reports/tinyprobe.xml` and publish the report as the CI's test report.

Safety: run workflows only against a **local or test** environment, never against production unless the user explicitly asks. Workflows that create data must delete it, so they can run again.

## 9. Writing good workflows

- **Independent workflows.** Each workflow logs in itself and creates the data it needs, with a `use` of a shared sequence when several workflows log in the same way. Never rely on another workflow having run first.
- **Leave the data as you found it.** Create a resource, `save` its id, work on it, delete it. Do not modify or delete seed data that other tests may read.
- **Tolerate leftovers.** A failing step stops the workflow, so the delete steps after it do not run and a failed run can leave its data behind (there is no teardown step). Identify your data by the id you saved, not by counts: check `$.total: { gte: 1 }` rather than `$.total: 1` after creating something, and never assert the exact size of a list your workflow adds to.
- **Stable checks.** For values that change on every run (timestamps, generated ids, tokens) check their format with `matches` (`{ matches: '^ORD-[0-9]+$' }`), or `{ exists: true }`, or a type-like condition (`{ gt: 0 }`), not an exact value.
- **Check what matters**: the status code and the fields the feature is about. A few precise checks are better than copying the whole response.
- **Name the steps** so the report reads like a specification: `Wrong password is rejected`, `Deleted order is gone`.
- **Negative cases pass `with` overrides** instead of duplicating endpoints: the same `login` endpoint with a wrong password; the same `get_order` with `token: invalid-token`.

## 10. Common mistakes

| Mistake | Correct |
|---|---|
| `expected:`, `asserts:`, `extract:`, `functions:` (old syntax) | `expect:`, `expect.body`, `save:`, `endpoints:` |
| `status_code: 200` | `status: 200` |
| `$.total: ">= 3"` | `$.total: { gte: 3 }` |
| `$.total: { ">=": 3 }` | `$.total: { gte: 3 }` |
| `$.a: 1` and `$.a: { lt: 5 }` as two keys | `$.a: { eq: 1, lt: 5 }` |
| `$.items[*].id`, `$.items[?(@.id==1)]` | `$.items[0].id: 1` (by index); `contains` matches whole elements only |
| `zip: 00100` (becomes the number 100) | `zip: "00100"` |
| `with: { id: ${orderId} }` (invalid YAML) | `with: { id: "${orderId}" }`, or block style: `with:` then `id: ${orderId}` on its own line |
| `sleep: 2` | `sleep: 2s` |
| `sleep: 5s` and hoping an async job finished | `until: { body: { $.status: Done }, every: 1s, timeout: 30s }` |
| `${uuid}`, `{{$guid}}`, `Date.now()` | `${$uuid}`, `${$timestamp}`: generated values start with `$` |
| `${$uuid}` written twice for the same value | define it once in `variables`: every use of `${$uuid}` is a new value |
| `duration: 500` or `{ lt: 500 }` | `duration: { lt: 500ms }` |
| an endpoint per variant (`login_ok`, `login_wrong`) | one `login` endpoint, different `with` |
| a token in the API file | `Authorization: "Bearer ${token}"` and `save: { token: ... }` |
| `Authorization: "Basic ${basicAuth}"` with the base64 in an env file | `basic: { username: ${user}, password: ${password} }` in the auth set |
| a token step against `/oauth/token` at the start of every workflow, for client credentials | an `oauth2` auth set; keep a `form` endpoint for the token URL only to test its errors |
| an `Authorization` header next to `basic` or `oauth2` | remove it: the scheme writes the header |
| `auth: bearer` where `bearer` is defined in another file | define the auth set in the same API file as the endpoint |
| `path: /api/notes/${id}` without `params` | `params: { id: }`: a forgotten `with.id` is then caught before running |
| `$.total: 1` after creating an item | `$.total: { gte: 1 }`: a previous failed run may have left data |
| body checks on a `204` or an empty `401` | only `status`: there is no JSON body to check |
| `matches: "^\d+$"`, `matches: /^\d+$/i` | `matches: '^\d+$'`, `matches: '(?i)^\d+$'`: single quotes, no `/` delimiters, flags inline |
| `matches: 'ORD-[0-9]+'` to check a whole value | `matches: '^ORD-[0-9]+$'`: without `^ $` the pattern can match anywhere |
| a response schema copied from the OpenAPI document into the step | `$: { schema: ../../openapi.yaml#/components/schemas/Note }`: one contract, in one place |
| `schema: ../../openapi.yaml#/paths/~1notes~1{id}/get/...` | `#/components/schemas/Note`; a pointer into `paths` breaks when a path changes |
| `schema` on a header or a cookie | `matches`: headers and cookies are text |
| endpoints renamed (`users_login`, `billing_login`) because two services both have `login` | keep the names; import the files with a name: `- billing: ../api/billing.yaml`, then `call: billing.login` |
| `import: [api/backend.yaml]` from `tinyprobe/workflows/` | `import: [../api/backend.yaml]`: paths are relative to the workflow |
| one endpoint per Postman request (`login_ok`, `login_wrong_password`) | one endpoint; each variant is a step with its own `with` (section 12) |
| `{{token}}` left in a converted file | `${token}` |
| a step commented out or deleted to leave it out of a run | `tinyprobe run … --skip <n>`: the file stays whole, and the next run runs it again |
| the real values of a Postman environment in `env.example.yaml` | placeholders there; real values only in `env.local.yaml` |
| a workflow that relies on a token saved by another workflow | each workflow logs in itself: `use: ../sequences/log-in.yaml` as its first step |
| the same login steps copied in every workflow | one sequence in `tinyprobe/sequences/`, and `use` in each workflow |
| `steps:` in a sequence file | `sequence:`: with `steps:` it is a workflow, and `run` runs it alone |
| a sequence that calls endpoints without `import` | `import` the API file in the sequence too, so it can be checked on its own |
| a Postman request or check skipped without a word | a line in `MIGRATION.md` that says what and why |
| a multipart body built by hand in a string `body`, or `body: "a=${a}&b=${b}"` | `multipart:` or `form:`: tinyprobe encodes the values and writes the boundary |
| a `Content-Type` header on an endpoint with `form` or `multipart` | no header: tinyprobe sets it, and reports the header as an error |
| `save: { sid: $.sid }` after a cookie login, then `Cookie: "sid=${sid}"` in the API file | nothing: the cookie jar sends the session cookie; `expect.cookies` to check it, `save: { x: { cookie: name } }` only to copy a cookie into a header |
| `expect.headers: { Set-Cookie: sid=abc }` | `expect.cookies: { sid: { exists: true } }`: `Set-Cookie` holds every cookie and its attributes |
| the same step copied for each test case, differing in one value | one step with `for_each`, one item per case |
| `save` in a `for_each` step | `save` in a step without `for_each`; `for_each` steps only check. For create/read/delete per case, a sequence with `for_each` on its `use` |
| `for_each: ${list}` on a `use` step | a written list or `{ file: path }` on a `use`; a variable only on a call step |
| `status: ${code}` in a step without `for_each` | a literal status, or a `for_each` whose items give the status |
| `file: tinyprobe/fixtures/a.png` in a workflow of `tinyprobe/workflows/`, or `--with file=tinyprobe/fixtures/a.png` in a `tinyprobe call` | `../fixtures/a.png`: paths are relative to the workflow, or to the API file for `call` |

## 11. Complete example

A backend with JWT login and a notes resource.

```yaml
# file: tinyprobe/api/backend.yaml
base_url: ${baseUrl}

headers:
  Accept: application/json

auth:
  bearer:
    Authorization: "Bearer ${token}"

endpoints:
  login:
    method: POST
    path: /api/auth/login
    params:
      email:
      password:
    body:
      email: ${email}
      password: ${password}

  me:
    path: /api/me
    auth: bearer

  me_anonymous:                   # the same route without auth: a request with no token
    path: /api/me

  list_notes:
    path: /api/notes
    query: [search, page]
    auth: bearer

  get_note:
    path: /api/notes/${id}
    params:
      id:
    auth: bearer

  create_note:
    method: POST
    path: /api/notes
    auth: bearer
    params:
      title:
      pinned: false
    body:
      title: ${title}
      pinned: ${pinned}

  update_note:
    method: PATCH
    path: /api/notes/${id}
    params:
      id:
      changes:
    auth: bearer
    body: ${changes}

  delete_note:
    method: DELETE
    path: /api/notes/${id}
    params:
      id:
    auth: bearer
```

```yaml
# file: tinyprobe/workflows/auth.yaml
name: Authentication
import:
  - ../api/backend.yaml

steps:
  - name: User logs in
    call: login
    with:
      email: ${email}
      password: ${password}
    expect:
      status: 200
      body:
        $.accessToken: { exists: true }
        $.tokenType: { in: [Bearer, bearer] }
    save:
      token: $.accessToken

  - name: Current user is returned
    call: me
    expect:
      status: 200
      body:
        $.email: ${email}

  - name: Wrong password is rejected
    call: login
    with:
      email: ${email}
      password: wrong-password
    expect:
      status: 401

  - name: Invalid token is rejected
    call: me
    with:
      token: invalid-token
    expect:
      status: [401, 403]

  - name: Missing token is rejected
    call: me_anonymous
    expect:
      status: 401
```

```yaml
# file: tinyprobe/workflows/notes.yaml
name: Notes
description: Lifecycle of a note created by the workflow itself.
import:
  - ../api/backend.yaml

variables:
  noteTitle: "tinyprobe note ${$randomString}"   # unique per run, the same in every step

steps:
  - name: Log in
    call: login
    with:
      email: ${email}
      password: ${password}
    save:
      token: $.accessToken

  - name: Create a note
    call: create_note
    with:
      title: ${noteTitle}
    expect:
      status: 201
      body:
        $.id: { exists: true }
        $.title: ${noteTitle}
        $.pinned: false
      duration: { lt: 1s }
    save:
      noteId: $.id

  - name: Title is required
    call: create_note
    with:
      title: ""
    expect:
      status: [400, 422]

  - name: Read the note
    call: get_note
    with:
      id: ${noteId}
    expect:
      body:
        $.id: ${noteId}
        $.title: ${noteTitle}

  - name: Search finds the note
    call: list_notes
    with:
      search: "${noteTitle}"
    expect:
      body:
        $.total: { gte: 1 }
        $.items[0].title: ${noteTitle}

  - name: Pin the note
    call: update_note
    with:
      id: ${noteId}
      changes:
        pinned: true
    expect:
      body:
        $.pinned: true

  - name: Delete the note
    call: delete_note
    with:
      id: ${noteId}
    expect:
      status: [200, 204]

  - name: Deleted note is gone
    call: get_note
    with:
      id: ${noteId}
    expect:
      status: 404
```

```yaml
# file: tinyprobe/env.local.yaml
baseUrl: http://localhost:8080
email: user@example.com
password: change-me
```

Run:

```bash
tinyprobe run tinyprobe/workflows/notes.yaml -e tinyprobe/env.local.yaml   # one workflow
tinyprobe run tinyprobe/workflows -e tinyprobe/env.local.yaml              # all of them, with a summary
```

A failing check looks like this; fix the test or report the backend behavior:

```text
  ✗ Read the note  GET  200  12.4ms  /api/notes/42
      ✓ $.id == 42
      ✗ $.title == "tinyprobe note k3x9q2ab"  actual "Untitled"
```

## 12. Migrating from Postman and other tools

Follow this section when the user gives you exports of another API tool (a Postman collection, its environments) instead of, or as well as, a backend to read. 12.1 to 12.4 are written for Postman: the procedure, the mapping, what cannot be translated and the coverage report. The procedure, the rules of 12.3 and the report apply to every source; 12.5 to 12.9 say what differs for OpenAPI, Insomnia, Bruno, HAR files and `curl` commands. For another tool, use the closest rows.

### 12.1 Procedure

1. **Inventory first.** Read the export files before writing anything. A Postman collection export is v2.1 when `info.schema` ends in `v2.1.0/collection.json`; for v2.0 or v1, ask the user to export it again as v2.1. List:
   - every folder and request, with its method and URL;
   - the `auth` of the collection, of each folder and of each request;
   - the collection `variable`s, and the keys of each environment file;
   - the scripts (`event` with `listen: prerequest` or `test`) at every level.

   Write this list to `tinyprobe/MIGRATION.md`, one row per request (section 12.4). It becomes the coverage report.
2. **Environments.** Each environment export (`values[]`) becomes `tinyprobe/env.<name>.yaml`, a flat map of its enabled keys. Values of type `secret`, and keys that look like credentials (`password`, `token`, `apiKey`, `secret`, `clientSecret`), go only into `env.local.yaml`, which is in `.gitignore`; `env.example.yaml` gets every key with a placeholder value. Keys that scripts fill at run time (a token, a created id) don't belong in env files: they become `save`.
3. **API file.** Group endpoints by resource, not by folder. Several collections (one per service) become several API files; when their names clash, import them with a name (section 4). Requests that differ only in their values (a wrong password, a missing field, another token) are **one endpoint**: declare its inputs as `params`, and each Postman request becomes a step with its own `with`.
4. **Workflows.** One workflow per folder that tests a flow, with the steps in the folder's order. Each workflow logs in itself (section 9), even when Postman relied on an earlier folder to set the token: the login request becomes a sequence in `tinyprobe/sequences/` (section 6e), and each workflow starts with its `use`.
5. **One folder at a time.** For each folder: write its endpoints and its workflow, `tinyprobe inspect` them, `tinyprobe run` the workflow until it passes, and update `MIGRATION.md`. Then move to the next folder. Never convert everything first and validate at the end.
6. **Coverage report.** Run the whole folder once more (`tinyprobe run tinyprobe/workflows -e tinyprobe/env.local.yaml`), finish `MIGRATION.md` (section 12.4) and tell the user the totals: requests converted, requests skipped, checks dropped, failures that look like backend bugs.

### 12.2 Postman to tinyprobe

| Postman | tinyprobe |
|---|---|
| `{{name}}` in the URL, headers or body | `${name}` |
| `{{baseUrl}}/users/:id` and `url.variable` | `base_url: ${baseUrl}`, `path: /users/${id}`, `params: { id: }` |
| `url.query` | `query: [name, ...]`; the values go in the step's `with` |
| `disabled: true` on a header, query parameter or form field | leave it out |
| raw JSON body | a YAML mapping; `"qty": {{qty}}` without quotes becomes `qty: ${qty}`, which keeps the type |
| `urlencoded` body | `form:`, one field per enabled `urlencoded[]` entry (`key: value`); no `Content-Type` header |
| `formdata` body | `multipart:`, one field per enabled `formdata[]` entry: `type: text` is `key: value`, `type: file` is `key: { file: "${key}" }` with the file in `tinyprobe/fixtures/` and its path in the step's `with` (see 12.3 when the file is not in the repository); no `Content-Type` header |
| collection, folder or request `auth`: `bearer`, `apikey` | `auth` sets in the API file; a request with `inherit` (or no `auth`) uses its parent's set, `noauth` uses none |
| `basic` auth (`auth.basic[]`: `username`, `password`) | an auth set `basic: { username: ${username}, password: ${password} }`, with the values in the env files (the password only in `env.local.yaml`) |
| `oauth2` auth with `grant_type` `client_credentials` (`auth.oauth2[]`: `accessTokenUrl`, `clientId`, `clientSecret`, `scope`, `client_authentication`) | an auth set `oauth2: { token_url, client_id, client_secret, scope }`; `client_authentication: header` is `client_auth: basic` (the default), `body` is `client_auth: form`; the secret only in `env.local.yaml`. Drop `tokenName`, `addTokenTo: header` and the other UI settings |
| collection `variable`s | workflow `variables` for non-secret defaults, otherwise env files |
| `pm.environment.set("t", pm.response.json().token)` (also `collectionVariables`, `globals`, `pm.variables`) | `save: { t: $.token }` |
| `pm.response.to.have.status(201)`, `pm.response.code === 201` | `expect.status: 201` |
| `pm.response.to.be.success` | no `status` (any 2xx) |
| `pm.response.to.be.ok` | `status: 200` |
| `pm.expect(json.a).to.eql(1)`, `.to.equal(1)` | `$.a: 1` (`eq` for a whole object) |
| `.to.be.above(n)`, `.below(n)`, `.at.least(n)`, `.at.most(n)` | `gt`, `lt`, `gte`, `lte` |
| `.to.include(x)` on an array or a string | `contains` |
| `.to.be.oneOf([a, b])` | `in: [a, b]` |
| `.to.match(/^ORD-\d+$/i)` | `matches: '(?i)^ORD-\d+$'` (no `/` delimiters, flags inline, single quotes) |
| `.to.be.a("string")`, `.to.be.an("array")`, `.to.be.a("number")` | `{ schema: { type: string } }`, `array`, `number` (`integer` for whole numbers) |
| `pm.response.to.have.jsonSchema(schema)`, `tv4.validate(json, schema)`, `ajv.validate(schema, json)` | the schema object of the script, as JSON, in `tinyprobe/schemas/<name>.json`, and `$: { schema: ../schemas/<name>.json }`; when it is the schema of an OpenAPI document of the project, a pointer to the document instead |
| `.to.have.property("id")`, `.to.exist` | `{ exists: true }` |
| `.to.be.true`, `.to.be.null` | `true`, `null` |
| `.to.have.lengthOf(2)` on an array | `$.items[1]: { exists: true }` and `$.items[2]: { exists: false }` |
| `pm.response.to.have.header("X-Id")` | `headers: { X-Id: { exists: true } }` |
| `pm.response.headers.get("X-Id")` compared to a value | `headers: { X-Id: value }` |
| `pm.response.responseTime` below `500` | `duration: { lt: 500ms }` |
| `pm.cookies.has("sid")`, `pm.expect(pm.cookies.get("sid")).to.exist` | `cookies: { sid: { exists: true } }` |
| `pm.cookies.get("theme")` compared to a value | `cookies: { theme: dark }` |
| `pm.environment.set("csrf", pm.cookies.get("XSRF-TOKEN"))` | `save: { csrf: { cookie: XSRF-TOKEN } }` |
| a session cookie that Postman's cookie jar kept between requests | nothing: each workflow has a cookie jar; the login request must be a step of the workflow |
| a `Cookie` header copied from the browser (`sid=…`) | drop it, and log in as a step; a cookie the server never sets becomes a header with a placeholder |
| `{{$guid}}`, `{{$timestamp}}`, `{{$randomInt}}`, `{{$isoTimestamp}}` | `${$uuid}`, `${$timestamp}`, `${$randomInt}`, `${$now}` |
| other `{{$random...}}` (`$randomFirstName`, `$randomEmail`) | text built on a generated value: `"name-${$randomString}"`, `"user-${$randomString}@example.com"` |
| a value computed in a pre-request script (`Date.now()`, a unique email) | a workflow variable built on a generated value, so it is the same in every step |
| a retry loop (`setTimeout` and `setNextRequest` on the same request) | `until` |
| saved example responses (`response[]`) | not tests: read them to learn the shape of a response, then check it against the real backend with `tinyprobe call` |
| `pm.test("Name", ...)` | the step's `name` |

An example. This collection excerpt:

```json
{
  "info": { "name": "Shop", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
  "auth": { "type": "bearer", "bearer": [{ "key": "token", "value": "{{token}}" }] },
  "item": [
    {
      "name": "Login",
      "request": {
        "method": "POST", "url": "{{baseUrl}}/api/login", "auth": { "type": "noauth" },
        "body": { "mode": "raw", "raw": "{ \"email\": \"{{email}}\", \"password\": \"{{password}}\" }" }
      },
      "event": [{ "listen": "test", "script": { "exec": [
        "pm.test(\"Status is 200\", () => pm.response.to.have.status(200));",
        "pm.environment.set(\"token\", pm.response.json().accessToken);"
      ] } }]
    },
    {
      "name": "Get order",
      "request": { "method": "GET", "url": "{{baseUrl}}/api/orders/7" },
      "event": [{ "listen": "test", "script": { "exec": [
        "const json = pm.response.json();",
        "pm.test(\"Paid or shipped\", () => pm.expect(json.status).to.be.oneOf([\"paid\", \"shipped\"]));",
        "pm.test(\"Two items\", () => pm.expect(json.items).to.have.lengthOf(2));"
      ] } }]
    },
    {
      "name": "Create order",
      "request": {
        "method": "POST", "url": "{{baseUrl}}/api/orders",
        "body": { "mode": "raw", "raw": "{ \"productId\": {{productId}}, \"quantity\": 1 }" }
      },
      "event": [{ "listen": "test", "script": { "exec": [
        "pm.test(\"Created\", () => pm.response.to.have.status(201));"
      ] } }]
    },
    {
      "name": "Create order - quantity zero",
      "request": {
        "method": "POST", "url": "{{baseUrl}}/api/orders",
        "body": { "mode": "raw", "raw": "{ \"productId\": {{productId}}, \"quantity\": 0 }" }
      },
      "event": [{ "listen": "test", "script": { "exec": [
        "pm.test(\"Rejected\", () => pm.response.to.have.status(422));"
      ] } }]
    }
  ]
}
```

becomes one API file, where the two "Create order" requests are one endpoint:

```yaml
# file: tinyprobe/api/shop.yaml
base_url: ${baseUrl}

auth:
  bearer:
    Authorization: "Bearer ${token}"

endpoints:
  login:
    method: POST
    path: /api/login
    params:
      email:
      password:
    body:
      email: ${email}
      password: ${password}

  get_order:
    path: /api/orders/${id}
    params:
      id:
    auth: bearer

  create_order:
    method: POST
    path: /api/orders
    auth: bearer
    params:
      productId:
      quantity: 1
    body:
      productId: ${productId}
      quantity: ${quantity}
```

and one workflow, with the Postman test names as step names:

```yaml
# file: tinyprobe/workflows/orders.yaml
name: Orders
import:
  - ../api/shop.yaml

steps:
  - name: Login
    call: login
    with:
      email: ${email}
      password: ${password}
    expect:
      status: 200
    save:
      token: $.accessToken

  - name: Get order
    call: get_order
    with:
      id: 7
    expect:
      body:
        $.status: { in: [paid, shipped] }
        $.items[1]: { exists: true }
        $.items[2]: { exists: false }

  - name: Create order
    call: create_order
    with:
      productId: ${productId}
    expect:
      status: 201

  - name: Create order - quantity zero
    call: create_order
    with:
      productId: ${productId}
      quantity: 0
    expect:
      status: 422
```

`email` and `productId` go into `env.example.yaml` and `env.local.yaml`; `password` only into `env.local.yaml`. The collection never deletes the order it creates, so `MIGRATION.md` says that this workflow leaves an order behind on every run (section 9).

### 12.3 What cannot be translated

**Never invent syntax.** When something has no tinyprobe equivalent:

1. if the request still makes sense without it, translate the rest and drop only what has no equivalent;
2. otherwise, skip the request.

Either way, write a line in `MIGRATION.md`: the request, what was not translated, and why. A skipped request that is not in the report looks, to the user, like a request that was migrated.

| In Postman | What to do |
|---|---|
| a pre-request script with logic: a signature (HMAC), a hash, a value in a format tinyprobe cannot generate | skip the request, or translate it without that value if the backend accepts it; report it |
| `pm.sendRequest` in a script | a plain request becomes a step before; otherwise report it |
| `postman.setNextRequest` branching (not a retry loop) | translate the main path; report the branches |
| data files (`newman run -d data.csv`), `pm.iterationData.get("email")`, `{{email}}` from a data file | copy the file to `tinyprobe/data/`; the requests of the run become steps with `for_each: { file: ../data/data.csv }`, and the data variables become arguments (`${email}`). A request that does not use the data stays a plain step. Note in `MIGRATION.md` that each step repeats on its own, while Newman repeated the whole collection for each row |
| a `formdata` file whose `src` is not in the repository (a path on someone's machine) | ask the user for the file; otherwise create a small file in `tinyprobe/fixtures/` with the same extension (a few lines of text are enough for most servers) and say so in `MIGRATION.md` |
| the OAuth 2.0 auth helper with another grant type (`authorization_code`, `password`, `implicit`, PKCE) | report it, and ask the user for a token in `env.local.yaml`, used by a bearer set (client credentials are translated: 12.2) |
| a JSON Schema with `if`/`then`/`else`, `dependentSchemas`, `propertyNames`, `unevaluatedProperties` or a `$ref` to a URL | keep the schema without those keywords (or with the remote schema saved as a file), and report what is not checked |
| a regex with a feature .NET reads differently (rare: `\d` also matches non-Latin digits, `/u` and `/y` flags) | use `[0-9]` for digits; drop the `u` and `y` flags |
| a loop over a response array (`json.forEach`) | check the first items by index; report that the others are not checked |
| XML or HTML responses | check `status` and headers only; report the body checks |
| JSONPath beyond the subset (`[*]`, filters, `..`) | index-based checks, or report it |

### 12.4 The coverage report

`tinyprobe/MIGRATION.md` stays in the repository: it tells the team what the tests cover and what they don't yet. For example:

```markdown
# Migration from Postman

Source: Shop.postman_collection.json (4 requests), environments: Local, Staging.

| Folder | Request | Became | Notes |
|---|---|---|---|
| Auth | Login | `login`, step "Login" in workflows/orders.yaml | |
| Orders | Get order | `get_order`, step "Get order" | |
| Orders | Create order | `create_order`, step "Create order" | leaves an order behind on every run |
| Orders | Create order - quantity zero | `create_order`, step "Create order - quantity zero" | |

Converted: 4 of 4 requests. Skipped: 0. Checks dropped: 0.
```

### 12.5 OpenAPI and Swagger

An OpenAPI document describes the endpoints but contains no tests. Use it as step 1 of section 8, in place of reading the routes, and follow section 8 from there: the workflows check what the document promises against the real backend. A document is OpenAPI 3 when it starts with `openapi: 3.`, Swagger 2 when it starts with `swagger: "2.0"`.

| OpenAPI | tinyprobe |
|---|---|
| `servers[0].url` (3), `schemes` + `host` + `basePath` (2) | `baseUrl` in the env files, path included (`http://localhost:8080/v1`); `base_url: ${baseUrl}` in the API file |
| an operation, `paths./notes/{id}.get` | an endpoint, named after `operationId` in snake_case (`getNote` → `get_note`), or after the method and the path when there is none |
| `{id}` in the path | `${id}`, declared in `params` |
| `in: query` parameters | `query: [name, ...]` |
| `in: header` parameters | a header with a placeholder (`X-Tenant: ${tenant}`), and the param declared |
| `in: cookie` parameters | a `Cookie: "name=${name}"` header (several joined with `; `), and the params declared; a session cookie set by a login operation needs nothing (section 6c) |
| `requestBody` with `application/json` (3), the `in: body` parameter (2) | a YAML `body` with the schema's `required` properties, each from a param; optional properties only where a workflow needs them |
| `requestBody` with `application/x-www-form-urlencoded` | `form`, as `urlencoded` in 12.2 |
| `requestBody` with `multipart/form-data`, the `in: formData` parameters (2) | `multipart`, as `formdata` in 12.2: a property with `format: binary` (or `type: file` in 2) is a file field, `{ file: "${name}" }`, with a small file in `tinyprobe/fixtures/`; the others are text fields |
| `securitySchemes` (3), `securityDefinitions` (2): `http` with `scheme: bearer`, `apiKey` with `in: header` | `auth` sets |
| `apiKey` with `in: query` | a query parameter of each endpoint that needs it, passed as `api_key: ${apiKey}` in the steps' `with` |
| `http` with `scheme: basic` (3), `type: basic` (2) | as `basic` auth in 12.2 |
| `oauth2` with `clientCredentials` (3) or `flow: application` (2) | an `oauth2` auth set: `tokenUrl` is `token_url`, the scopes the operations need are `scope`; `client_id` and `client_secret` are placeholders |
| other `oauth2` flows, `openIdConnect` | ask the user for a token in `env.local.yaml` |
| `security` at the top level, or on an operation; `security: []` | the endpoint's `auth`; no `auth` for `[]` |
| the documented success response (`200`, `201`, `204`) | `expect.status` of the main step |
| documented error responses (`400`, `401`, `404`, `422`) | a step for each, as in section 8, step 5 |
| a response schema (`responses.200.content.application/json.schema`) | `$: { schema: ../../openapi.yaml#/components/schemas/Note }` in the main step: a pointer to the document, never a copy of the schema. For a schema written inline in `paths` (no `components` entry), check its `required` properties with `{ exists: true }` and its `enum` with `in: [...]` |
| `example`, `examples`, `default` | the shape of the data, never the expected values: take values from the seed data and check them with `tinyprobe call` |
| `$ref` | resolve it: `#/components/schemas/Note` in the same document; another file, by reading that file |
| `deprecated: true` | convert it, and note it in `MIGRATION.md` |

Write `tinyprobe/MIGRATION.md` (12.4) with a row per operation, the skipped ones included. For a large document, write one API file per `tag`, and convert one tag at a time like a Postman folder. When the backend does not do what the document says (another status, a missing field), keep the check and report the difference: either the document or the backend is wrong, and the user decides which.

This operation:

```yaml
openapi: 3.0.3
servers:
  - url: http://localhost:8080/v1
components:
  securitySchemes:
    bearerAuth: { type: http, scheme: bearer }
security:
  - bearerAuth: []
paths:
  /notes/{id}:
    get:
      operationId: getNote
      parameters:
        - { name: id, in: path, required: true, schema: { type: integer } }
      responses:
        "200":
          description: The note
          content:
            application/json:
              schema:
                type: object
                required: [id, title, status]
                properties:
                  id: { type: integer }
                  title: { type: string }
                  status: { type: string, enum: [draft, published] }
        "404":
          description: No note with this id
```

becomes an endpoint and two steps (in a real workflow, after the login step of section 9):

```yaml
base_url: ${baseUrl}                  # baseUrl: http://localhost:8080/v1

auth:
  bearer:
    Authorization: "Bearer ${token}"

endpoints:
  get_note:
    path: /notes/${id}
    params:
      id:
    auth: bearer

steps:
  - name: Read a note
    call: get_note
    with:
      id: 1                           # a note of the seed data, not of the document's examples
    expect:
      status: 200
      body:
        $.id: 1
        $.title: { exists: true }
        $.status: { in: [draft, published] }

  - name: A note that does not exist
    call: get_note
    with:
      id: 999999
    expect:
      status: 404
```

### 12.6 Insomnia

An Insomnia export is JSON with `"__export_format": 4` and a flat `resources` list, or YAML that starts with `type: collection.insomnia.rest/5.0`, with nested items. Follow the procedure of 12.1; what differs from Postman:

| Insomnia | tinyprobe |
|---|---|
| `_type: request_group`, linked by `parentId` (v4), or nested items (v5) | folders, as in Postman |
| `_type: request`: `method`, `url`, `headers`, `parameters`, `body` | an endpoint, as a Postman request |
| `{{ _.baseUrl }}`, `{{ baseUrl }}` | `${baseUrl}` |
| the base environment and its sub-environments | one env file per sub-environment, with the keys of the base environment merged in. A private sub-environment (`isPrivate: true`) and keys that look like credentials go only into `env.local.yaml` |
| `authentication` of type `bearer`, `apikey`, `basic` | as the Postman auth rows of 12.2 |
| `authentication` of type `oauth2` with `grantType: client_credentials`: `accessTokenUrl`, `clientId`, `clientSecret`, `scope`, `audience`, `credentialsInBody` | an `oauth2` auth set; `credentialsInBody: true` is `client_auth: form`; `audience` goes in `form` |
| `body.mimeType`: `application/json`, `application/x-www-form-urlencoded`, `multipart/form-data` | as `raw` JSON, `urlencoded` and `formdata` (12.2) |
| `{% response 'body', 'req_…', 'b64::JC50b2tlbg==::46b', … %}` | a value taken from another request's response: that request becomes an earlier step, with `save`. After `b64::` is the path in base64: `JC50b2tlbg==` is `$.token` |
| `{% uuid 'v4' %}`, `{% now 'iso-8601' %}`, `{% now 'unix' %}`, `{% faker … %}` | `${$uuid}`, `${$now}`, `${$timestamp}`, and as `$random…` in 12.2 |
| other template tags (`{% now 'millis' %}`, `{% base64 … %}`, `{% hash … %}`, `{% prompt … %}`, `{% file … %}`) | report them (12.3) |
| after-response scripts: `insomnia.test`, `insomnia.expect`, `insomnia.environment.set` | as `pm.test`, `pm.expect`, `pm.environment.set` in 12.2 |
| unit test suites (`unit_test_suite`, `unit_test`): `insomnia.send()`, then `expect(response.status).to.equal(200)` | a workflow per suite, a step per test |

### 12.7 Bruno

A Bruno collection is a folder: `bruno.json`, one `.bru` file per request (subfolders are folders, `meta.seq` is the order), `collection.bru` and `folder.bru` for shared headers, auth and scripts, and `environments/<name>.bru`. A request:

```text
meta {
  name: Get user
  type: http
  seq: 2
}

get {
  url: {{baseUrl}}/api/users/:id
  body: none
  auth: inherit
}

params:path {
  id: 1
}

assert {
  res.status: eq 200
  res.body.firstName: eq Gaetano
  res.body.email: isString
}

vars:post-response {
  userEmail: res.body.email
}
```

| Bruno | tinyprobe |
|---|---|
| the `get`, `post`, ... block: `url` | the method and the path; `{{baseUrl}}` → `${baseUrl}`; `:id` with `params:path` → `${id}`, declared in `params` |
| `params:query`, `headers` | `query`, `headers`. A line that starts with `~` is disabled: leave it out |
| `auth: inherit` | the auth of `folder.bru`, else of `collection.bru` |
| `auth:bearer`, `auth:apikey` (`placement: header`), `auth:basic` | as the Postman auth rows of 12.2 |
| `auth:oauth2` with `grant_type: client_credentials`: `access_token_url`, `client_id`, `client_secret`, `scope` | an `oauth2` auth set |
| `body:json`, `body:form-urlencoded`, `body:multipart-form` | as `raw` JSON, `urlencoded` and `formdata` (12.2) |
| `vars:pre-request` | workflow `variables`, or the step's `with` |
| `vars:post-response { userEmail: res.body.email }`, `bru.setVar("userEmail", res.body.email)` | `save: { userEmail: $.email }` |
| `assert` `res.status: eq 200` | `status: 200` |
| `assert` `res.body.a: eq 1`, and `neq`, `gt`, `gte`, `lt`, `lte`, `in`, `contains` | `$.a: 1`, and `ne`, `gt`, `gte`, `lt`, `lte`, `in`, `contains` |
| `isDefined`, `isUndefined`, `isNull` | `{ exists: true }`, `{ exists: false }`, `null` |
| `res.responseTime: lt 500` | `duration: { lt: 500ms }` |
| `res.headers.x-id: isDefined` | `headers: { X-Id: { exists: true } }` |
| `matches "^ORD-"` | `{ matches: '^ORD-' }` |
| `isString`, `isNumber`, `isBoolean`, `isArray`, `isJson` | `{ schema: { type: string } }`, `number`, `boolean`, `array`, `object` |
| `length` | as `lengthOf` (12.2) |
| `tests { test("...", function() { expect(res.getStatus()).to.equal(200); }); }` | as `pm.test` in 12.2; `res.getStatus()`, `res.getBody()`, `res.getHeader()` are the response |
| `environments/<name>.bru`: `vars { ... }` | `env.<name>.yaml` |
| `vars:secret [ password, apiKey ]` | only the names are in the file. Ask the user for the values, or read them from the collection's `.env` (`{{process.env.NAME}}`), and put them only in `env.local.yaml` |

The request above becomes this step of the workflow (`get_user` with `path: /api/users/${id}`):

```yaml
- name: Get user
  call: get_user
  with:
    id: 1
  expect:
    status: 200
    body:
      $.firstName: Gaetano
      $.email: { exists: true }       # isString has no equivalent: report it
  save:
    userEmail: $.email
```

### 12.8 HAR files

A HAR file (`log.entries[]`) is a recording of the requests of one session, from a browser or a proxy. It shows real requests and responses, not tests.

- **It contains secrets.** Never copy the value of an `Authorization`, `Cookie` or `Set-Cookie` header, or of a credential field in a body, into a file. Tell the user not to commit the HAR.
- **Keep only the API.** Drop requests to other hosts, `OPTIONS` preflights, and documents, scripts, styles, images and fonts (`response.content.mimeType`, `_resourceType`). Keep one of a series of identical polling requests.
- **One endpoint per method and path pattern.** `/api/users/42` and `/api/users/7` are one endpoint, `path: /api/users/${id}`.
- **Form and multipart bodies.** `postData.params` with `postData.mimeType` `application/x-www-form-urlencoded` is a `form`, with `multipart/form-data` a `multipart`. A HAR does not keep the content of uploaded files: create a small file of the same type in `tinyprobe/fixtures/` and say so in `MIGRATION.md`.
- **Headers.** Keep `Content-Type`, `Accept` and the API's own headers. Drop the browser's (`User-Agent`, `Accept-Encoding`, `Origin`, `Referer`, `sec-*`, `Cookie`) and the HTTP/2 pseudo-headers (`:authority`, `:path`). `Authorization` becomes an `auth` set with a placeholder.
- **The order is the flow.** Entries are in time order: a value that a response returned and a later request sent (an id in a URL, a token) becomes a `save`.
- **Responses** (`response.content.text`, base64 when `encoding: base64`) show the shape for the checks. Don't copy volatile values (ids, dates) as expected values; check with `tinyprobe call`.
- **A session kept by cookies** (a `Set-Cookie` on the login, `Cookie` on the next requests) needs nothing: the login becomes a step, and the cookie jar sends the cookie (section 6c). Never copy the `Cookie` values. A cookie copied into a header by the page's script (`X-XSRF-TOKEN` equal to the `XSRF-TOKEN` cookie) becomes `save: { csrf: { cookie: XSRF-TOKEN } }` and a header with a placeholder.

### 12.9 curl commands

From a README, a script, or "Copy as cURL" in a browser.

| curl | tinyprobe |
|---|---|
| the URL | `base_url` and `path`; the query string → `query`, with the values in the steps' `with` |
| `-X POST`, `--request POST` | `method`; without `-X`, `GET`, or `POST` when there is `-d` |
| `-H "Name: value"` | `headers`; `Authorization: Bearer …` → an `auth` set, and the token only in `env.local.yaml` |
| `--json '{…}'`, or `-d` / `--data` / `--data-raw` with JSON and `Content-Type: application/json` | a YAML `body` |
| `-d "a=1&b=2"` without a `Content-Type`, `--data-urlencode "a=x y"` | `form: { a: 1, b: 2 }` |
| `-G` with `-d` | `query` |
| `-u user:password` | an auth set `basic: { username: ${username}, password: ${password} }` |
| `-F "name=value"` | `multipart: { name: value }` |
| `-F "file=@path"` (`;type=…`, `;filename=…`) | `multipart: { file: { file: "${file}" } }` (`content_type`, `filename`), the file in `tinyprobe/fixtures/` and its path in the step's `with` |
| `-c file`, `-b file` (a cookie jar between commands) | nothing: the login command becomes a step, and each workflow keeps its cookies |
| `-b "name=value"` | a `Cookie: "name=${name}"` header with a param; for a session cookie set by a login, log in as a step instead |
| `-k`, `--insecure` | report it: tinyprobe always checks certificates |
| `-s`, `-v`, `-i`, `-L`, `--compressed` | leave them out |
| `$VAR`, `${VAR}` | keys of the env files |
| `TOKEN=$(curl … \| jq -r .token)`, then `-H "Authorization: Bearer $TOKEN"` | a login step with `save: { token: $.token }`, then the steps that use it |

## 13. Checklist before you finish

- [ ] `tinyprobe run tinyprobe/workflows -e tinyprobe/env.local.yaml` exits `0` against the local backend, or the remaining failures are reported to the user as possible backend bugs.
- [ ] No real secret in API or workflow files; secrets are in `env.local.yaml`, which is in `.gitignore`.
- [ ] Every endpoint name used by a step exists in an imported API file.
- [ ] Workflows that create data delete it, and can run twice in a row.
- [ ] Values that must stay strings are quoted.
- [ ] Path placeholders and required inputs are declared as `params`.
- [ ] No exact count on a list the workflow adds to (`{ gte: 1 }`, not `1`), and no body checks on responses without a body.
- [ ] No unsupported JSONPath (`*`, `?()`, `..`) and no repeated keys in `expect.body`.
- [ ] Asynchronous results are awaited with `until`, never with a fixed `sleep`.
- [ ] After a migration: `MIGRATION.md` lists every request of the collection, with what it became or why it was skipped, and every dropped check.
- [ ] After a migration: no `{{...}}` placeholder is left, and no Postman `secret` value is in a committed file.
