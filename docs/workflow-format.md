# Workflow format

tinyprobe separates *what an endpoint is* (method, path, headers, body shape) from *what a test expects* (status, values, timings). Endpoints are declared once and called by name, like functions, from any number of workflows. The complete example lives in [`docs/example/`](example/).

Two kinds of file:

| File | Contains | Reused by |
|---|---|---|
| **API file** | endpoints with their parameters, base URL, auth header sets | any number of workflows |
| **Workflow file** | steps that `call` endpoints `with` arguments, and what they `expect` and `save` | — |

An API file is declarative: it has no checks and no `save`, and `tinyprobe call` of one endpoint remembers nothing for the next call. Everything that runs and every value passed from one request to the next is in a workflow ([decision 0003](decisions/0003-endpoints-declare-workflows-run.md)).

```text
docs/example/
├── api/
│   └── testserver.yaml        # endpoints of the test server
└── workflows/
    ├── auth.yaml
    ├── users.yaml
    ├── catalog.yaml
    └── http.yaml
```

```bash
tinyprobe run docs/example/workflows/users.yaml -e docs/environment.yaml
tinyprobe run docs/example/workflows -e docs/environment.yaml      # every workflow, one summary
```

## API file

```yaml
base_url: ${baseUrl}

auth:                                  # named header sets
  bearer:
    Authorization: "Bearer ${token}"
  apikey:
    X-API-Key: ${apiKey}

endpoints:
  get_user:
    path: /api/users/${id}
    auth: bearer

  login:
    method: POST
    path: /api/login
    params:
      username: admin                  # default value
      password:                        # no value: required
    body:
      username: ${username}
      password: ${password}
```

| Key | Description |
|---|---|
| `base_url` | Prepended to every `path` of the file. Optional if paths are absolute URLs. |
| `headers` | Headers sent by every endpoint of the file. |
| `auth.<name>` | A named auth set: headers, plus at most one of `basic` and `oauth2`. See [Auth sets](#auth-sets). |
| `endpoints.<name>` | An endpoint, called by its name. |
| `endpoints.<name>.method` | HTTP verb, default `GET`. |
| `endpoints.<name>.path` | Path, may contain `${…}` placeholders. |
| `endpoints.<name>.query` | List of query parameters. Each one is sent only when the step passes it. |
| `endpoints.<name>.headers` | Extra headers for this endpoint. |
| `endpoints.<name>.auth` | One auth set or a list of them (`[bearer, apikey]`). |
| `endpoints.<name>.body` | A YAML value, sent as JSON (`application/json`); a string, sent as is (`text/plain` unless a `Content-Type` header says otherwise); or a single `${…}` placeholder. |
| `endpoints.<name>.form` | A form body (`application/x-www-form-urlencoded`), instead of `body`. See [Forms and file uploads](#forms-and-file-uploads). |
| `endpoints.<name>.multipart` | A `multipart/form-data` body with text fields and files, instead of `body`. See [Forms and file uploads](#forms-and-file-uploads). |
| `endpoints.<name>.params` | Optional declaration of the arguments: `name: default`, or `name:` for a required one. Declared params are checked before the request is sent. |

### Auth sets

An auth set is a named set of headers that endpoints pick with `auth:`. Two keys of a set are not headers: `basic` and `oauth2`, which write the `Authorization` header for you.

```yaml
# file: api/secure.yaml
base_url: ${baseUrl}

auth:
  apikey:
    X-API-Key: ${apiKey}                   # headers, as many as needed
  admin:
    basic:                                 # Authorization: Basic base64(username:password)
      username: ${username}
      password: ${password}
  service:
    oauth2:                                # client credentials
      token_url: ${authUrl}/oauth/token
      client_id: ${clientId}
      client_secret: ${clientSecret}
      scope: [orders:read, orders:write]   # optional; a list is sent joined with spaces
      client_auth: basic                   # optional: basic (default) or form
      form: { audience: "https://api.example.com" }   # optional: other fields of the token request
    X-Tenant: ${tenant}                    # a set with a scheme can still add headers

endpoints:
  admin_stats:
    path: /admin/stats
    auth: admin
    params: { username: admin, password: }

  list_orders:
    path: /orders
    auth: [service, apikey]
```

**`basic`** sends `Authorization: Basic` with the base64 of `username:password`, encoded as UTF-8. Both keys are required and may be placeholders, so a step can try a wrong password with `with: { password: wrong }`. A username with `:` fails the step.

**`oauth2`** uses the client credentials grant:

- Before the first request that uses the set, tinyprobe sends a `POST` to `token_url` with `grant_type=client_credentials` (and `scope`, and the `form` fields). Then it sends the request with `Authorization: Bearer <access_token>`.
- The client id and secret go in an `Authorization: Basic` header of the token request (`client_auth: basic`, the default) or in its form body (`client_auth: form`).
- **The token is reused for the rest of the run**, by every endpoint whose set has the same token URL, client and scope, until its `expires_in` minus 30 seconds. Each workflow of a suite gets its own token. `tinyprobe call` gets one on every call, so a protected endpoint can be called with only the environment file.
- If the token request fails, the step fails before calling the API, with the token server's answer: `Cannot get an OAuth2 token from https://auth.example.com/oauth/token: status 401, {"error":"invalid_client"}`. A network error of the token request is retried like the API request (`max_retries`).
- To test the authorization server's own errors (a wrong secret), call the token URL as a normal `form` endpoint.

Rules for both:

- An endpoint can use at most one set with `basic` or `oauth2`, plus header sets.
- An `Authorization` header in the same set, in another set of the endpoint, or in the endpoint's own `headers` is an error. One in the file's `headers` is not sent to that endpoint.
- Their placeholders are endpoint arguments, like the ones in headers.
- With `-v`, the log shows when a token is requested or reused. The recorded request shows the `Authorization` header as it was sent.

### Forms and file uploads

```yaml
# file: api/forms.yaml
base_url: ${baseUrl}
endpoints:
  token:
    method: POST
    path: /oauth/token
    params: { client_id:, client_secret: }
    form:                                  # application/x-www-form-urlencoded
      grant_type: client_credentials
      client_id: ${client_id}
      client_secret: ${client_secret}
      scope: [read, write]                 # a list: scope=read&scope=write

  upload_avatar:
    method: POST
    path: /api/users/${id}/avatar
    params: { id:, avatar: }
    multipart:                             # multipart/form-data
      description: Profile picture         # a text field
      avatar:
        file: ${avatar}                    # a file: its path
        filename: me.png                   # optional, default: the file's name
        content_type: image/png            # optional, default: from the extension
      tags: [profile, public]              # a list: the field once per value
```

```yaml
# file: workflows/avatar.yaml
name: Avatar
import: [../api/forms.yaml]
steps:
  - call: upload_avatar
    with:
      id: 1
      avatar: ../fixtures/avatar.png       # relative to this workflow
    expect:
      status: 201
```

- An endpoint has at most one of `body`, `form` and `multipart`.
- `form` encodes names and values, after resolving placeholders: a password `a&b` is sent as `a%26b`. Numbers and booleans are sent as text, `null` as an empty value.
- In `multipart`, a field is a value (a text field), a mapping with `file` (a file), or a list of them (the field once per item). Fields are sent in the order written.
- **File paths are relative to the file tinyprobe was given**: the workflow for `run` (each workflow of a folder on its own), the API file for `call`, like `import`. Absolute paths are used as they are. The file is read when the step runs, on every attempt; a missing file fails the step.
- `content_type` defaults to the type of the extension (`png`, `jpg`, `jpeg`, `gif`, `webp`, `svg`, `pdf`, `json`, `txt`, `csv`, `xml`, `html`, `zip`), otherwise `application/octet-stream`. Text fields have no `Content-Type`.
- tinyprobe sets the request's `Content-Type` (with the multipart boundary). A `Content-Type` from the file's `headers` or an auth set is not sent; one in the endpoint's own `headers` is an error.
- With `-v` and in `--reporter ndjson`, a multipart body is shown with each file's content replaced by one line, `(file ../fixtures/avatar.png, 79 B)`.
- Under `tinyprobe mcp`, files must be inside the server's working directory.

## Workflow file

```yaml
name: Users
import:
  - ../api/testserver.yaml             # paths are relative to this file

variables:                             # defaults; the env file overrides them
  pageSize: 2

settings:
  timeout: 10                          # seconds per request
  max_retries: 0                       # retries on network errors / timeouts only

steps:
  - name: Create a user
    call: create_user
    with:
      firstName: Test
      email: test.user@example.com
    expect:
      status: 201
      body:
        $.email: test.user@example.com
        $.id: { exists: true }
    save:
      userId: $.id

  - sleep: 200ms
```

| Key | Description |
|---|---|
| `name`, `description` | Shown in the report. |
| `import` | API files whose endpoints the steps can call: a path, or `name: path` to call them as `name.endpoint` (see [Named imports](#named-imports)). |
| `endpoints`, `auth`, `base_url` | Same as in an API file: a workflow can define its own endpoints, handy for small single-file tests. |
| `variables` | Default variables. Each is resolved once per run, the first time it is used, and may use the environment, other variables and generated values (see below). |
| `settings` | `timeout` (seconds per request, default 30) and `max_retries` (retries on network errors and timeouts, default 0; HTTP error status codes are never retried). |
| `steps` | Run in order; the workflow stops at the first failing step. |

### Named imports

A backend made of several services has an API file per service, and they often share endpoint names (`login`, `health`). Two endpoints with the same name are an error, so give the imports a name:

```yaml
name: Checkout
import:
  - ../api/testserver.yaml                 # endpoints by their names, as always
  - other: ../api/forms.yaml               # endpoints called other.token, other.upload_avatar
steps:
  - call: login
    with: { password: password }
  - call: other.token
    with: { client_id: tinyprobe, client_secret: s3cret }
```

- An item of `import` is a path, or one `name: path` pair. The name is lowercase letters, digits and `_`, and starts with a letter.
- Every endpoint of a named import is called `name.endpoint`, in `call`, in the step's default name and in the reports. The file keeps its own `base_url`, `headers` and `auth` sets.
- The same file under two names gives two sets of names; under one name, or twice without one, it is loaded once. A name used for two different files is an error.
- Sequences name their imports the same way.

### Steps

| Key | Description |
|---|---|
| `call` | Name of the endpoint. |
| `use` | A step of its own: run the steps of a sequence file here, see [Reusable sequences](#reusable-sequences). |
| `name` | Shown in the report. Without it the step is shown as the call plus its path and query arguments, e.g. `status code=200`; body and header arguments are left out, since they may be secrets. |
| `with` | Arguments for the endpoint's placeholders. They may reference variables (`id: ${userId}`). |
| `expect.status` | Expected status code, or a list of accepted ones (`[200, 204]`). Without it any 2xx status is accepted and anything else fails the step. |
| `expect.headers` | Checks on response headers (case-insensitive names). |
| `expect.cookies` | Checks on cookies (case-sensitive names) in the workflow's cookie jar after the response, see [Cookies](#cookies). |
| `expect.body` | Checks on the JSON body, keyed by JSONPath. |
| `expect.duration` | Check on the response time, e.g. `{ lt: 500ms }`. |
| `for_each` | Repeat the call once per item of a list, see [Repeating a step](#repeating-a-step-for_each). |
| `until` | Repeat the call until conditions pass (polling), see below. |
| `save` | `variable: <JSONPath>` pairs stored for the following steps, after every check passed. A path that matches nothing fails the step. `variable: { cookie: name }` saves the value of a cookie instead, see [Cookies](#cookies). |
| `sleep` | A step of its own: waits, e.g. `sleep: 500ms` or `sleep: 2s`. Durations use `ms`, `s`, `m`, `h` or `d`. |

## Variables

Endpoints behave like functions:

- **Declared `params` are local.** They come from the step's `with` or from their default, never from global variables. A required param that the step does not pass is an error.
- **Any other placeholder** (for example `${token}` in an auth set) is resolved, from the highest priority, from: the step's `with`, values stored with `save` by earlier steps, the environment file (`-e`), the workflow's `variables`.

So a step can override a saved value for itself only, without changing it for the next steps:

```yaml
- name: Invalid bearer token is rejected
  call: bearer_check
  with:
    token: invalid-token
  expect:
    status: 401
```

An undefined variable fails the step before the request is sent.

**Types in bodies.** A value that is a single placeholder keeps the argument's type: with `active: ${active}` and `with: { active: false }` the JSON is `"active": false`, not `"active": "false"`. Placeholders inside a longer text produce strings. In `with`, plain YAML scalars keep their type (`true`, `42`, `null`); quote them to send a string (`zipCode: "70100"`).

## Polling with `until`

For operations that complete asynchronously (a booking confirmed by a background job, a payment settled by a webhook), `until` repeats the call until its conditions pass:

```yaml
- name: Booking is confirmed
  call: get_booking
  with:
    id: ${bookingId}
  until:                              # same status / headers / body checks as expect
    body:
      $.status: { in: [Confirmed, Rejected] }
    every: 1s                         # pause between attempts (default 1s)
    timeout: 30s                      # give up after this time (default 30s)
  expect:                             # evaluated on the last response only
    body:
      $.status: Confirmed
  save:
    paymentId: $.paymentId
```

- `until` takes `status`, `headers`, `cookies` and `body`, written exactly like in `expect`, plus `every` and `timeout`.
- While its conditions do not hold, the step waits `every` and sends the request again, with the same arguments. Any status is accepted while waiting, unless `until.status` is set (`until: { status: 200 }` waits for a resource to appear).
- When they hold, `expect` and `save` are applied to that last response.
- If they still do not hold after `timeout`, the step fails with `Gave up after N attempts in 30s` and the report shows the values of the last attempt.
- A network error is not a reason to keep waiting: after the `settings.max_retries` retries of that attempt, it fails the step.

## Reusable sequences

A sequence is a file of steps that several workflows run with `use`: a login, a shared setup. Every workflow still starts from zero ([decision 0003](decisions/0003-endpoints-declare-workflows-run.md)); now it starts with one `use` instead of a copy of the steps.

```yaml
# file: sequences/log-in.yaml
name: Log in
import:
  - ../api/forms.yaml                      # the API files the sequence calls
params:                                    # passed by `use` with `with`
  client_id: tinyprobe                     # with a default
  client_secret:                           # required
sequence:
  - name: Get a token
    call: token
    with: { client_id: "${client_id}", client_secret: "${client_secret}" }
    save:
      token: $.access_token                # visible to the workflow after the `use`
```

```yaml
# file: workflows/with-login.yaml
name: With a login
import: [../api/forms.yaml]
steps:
  - use: ../sequences/log-in.yaml          # relative to this file, like import
    with:
      client_secret: ${clientSecret}
  - call: upload_avatar                    # can use ${token}
    with: { id: 1, avatar: ../fixtures/avatar.png }
```

```text
  ✓ Log in › Get a token   POST   200    12.10ms  /oauth/token
  ✓ upload_avatar id=1     POST   201    20.43ms  /api/users/1/avatar
```

**The sequence file.**

- It has `sequence:` where a workflow has `steps:`. The steps are written as in a workflow, and may `use` other sequences; a sequence that uses itself is an error.
- `params` declares what a `use` passes: `name: default`, or `name:` for a required one. Inside the sequence a param is a variable (`${client_secret}`), read before the workflow's variables, and the workflow does not see it afterwards.
- `import` loads the API files it calls, so `tinyprobe inspect sequences/log-in.yaml` checks it on its own. An API file imported by both the workflow and the sequence is loaded once.
- `name` and `description` are optional. The name prefixes its steps in the report (`Log in › Get a token`); without it, the file name is used.
- `tinyprobe run <folder>` skips sequence files, and `tinyprobe run` of a sequence file is an error: a sequence runs from a workflow.

**`use` in a workflow.**

- `use: <path>`, with `with` for the params and an optional `name` that replaces the sequence's name in the report. A `use` step has no `call`, `expect`, `until` or `save`; it may have `for_each` (below).
- The sequence's steps run in its place, as if written there:
  - they see the workflow's variables;
  - their `save` values are visible to the next steps;
  - the workflow stops at the first failing step, inside the sequence or not.
- An unknown param in `with`, or a missing required one, is an error with its location.
- The report shows each step of the sequence as a step. The console, the JUnit report and the extension place it at the `use` line of the workflow. Errors in a sequence file point to that file.

**A sequence once per item: `for_each` on `use`.** For a case made of several steps (create, read back, delete), a `use` step takes `for_each`: the sequence runs once per item, and each item's keys are params of the sequence, like `with` ([spec 0020](specs/0020-for-each-over-a-sequence.md)).

```yaml
# file: sequences/user-lifecycle.yaml
name: User lifecycle
import: [../api/users.yaml]
params:
  firstName:
  city:
sequence:
  - name: Create
    call: create_user
    with: { firstName: "${firstName}", city: "${city}" }
    expect: { status: 201 }
    save: { userId: $.id }
  - name: Delete
    call: delete_user
    with: { id: "${userId}" }
    expect: { status: 204 }
```

```yaml
steps:
  - use: ../sequences/user-lifecycle.yaml
    for_each:
      - { firstName: Ada, city: Bari }
      - { firstName: Grace, city: Lecce }
```

```text
  ✓ User lifecycle [1/2] › Create  POST   201  1.08ms  /api/users
  ✓ User lifecycle [1/2] › Delete  DELETE 204   665µs  /api/users/35
  ✓ User lifecycle [2/2] › Create  POST   201   674µs  /api/users
  ✓ User lifecycle [2/2] › Delete  DELETE 204   271µs  /api/users/36
```

- The items are a written list or a data file, `{ file: path }`, as for a call. A variable (`for_each: ${users}`) is an error: the steps of a sequence are laid out when the workflow loads, before a variable has its value.
- Each item's keys are checked like `with`: an unknown param is an error with a suggestion, a required param must be in `with` or in every item, and a key in both `with` and an item is an error.
- All the steps of item 1 run, then those of item 2. The workflow stops at the first failing step, as anywhere else.
- The steps of a sequence may `save`: each item reads its own values. After the last item, its values stay visible to the next steps.
- Steps are named `User lifecycle [2/2] › Create`; `--to` and `inspect` count the steps of every item.

## Repeating a step: `for_each`

`for_each` runs the same call once per item: a table of validation cases, every role against an endpoint, every item of a list the API returned.

```yaml
name: Tables of cases
import:
  - ../api/testserver.yaml

steps:
  - name: Simulated status codes
    call: status
    for_each:                              # one call per item; the item's keys are arguments
      - { code: 200 }
      - { code: 404 }
      - { code: 503 }
    expect:
      status: ${code}                      # in a for_each step, the status can come from the item
      body:
        $.status: ${code}

  - name: Bearer tokens
    call: bearer_check
    for_each: { file: ../data/tokens.csv } # token,status: one item per line
    expect:
      status: ${status}

  - name: List the first users
    call: list_users
    with: { page: 1, pageSize: 3 }
    save:
      users: $.data                        # a list of objects

  - name: Every listed user can be read
    call: get_user
    for_each: ${users}                     # `id` comes from each object
    expect:
      body:
        $.email: ${email}
```

```text
  ✓ Simulated status codes [1/3]   GET    200     4.93ms  /api/status/200
  ✓ Simulated status codes [2/3]   GET    404     6.61ms  /api/status/404
  ✓ Simulated status codes [3/3]   GET    503      916µs  /api/status/503
```

**Items.**

- `for_each` is one of:
  - a list of mappings;
  - a variable holding such a list (`${users}`, from a `save`, `variables` or the environment);
  - a data file, `{ file: path }`.
- Each item's keys are **arguments of the call**, like `with`, and **variables of the step**, so `expect` and `until` can use them (`$.code: ${error}`).
- `with` holds what is the same for every item, and may read the item's values (`email: "${name}@example.com"`). A key in both an item and `with` is an error.
- The keys of a written list or of a data file are checked when the workflow loads, like `with`: a typo is reported with a suggestion, and a required param must be in `with` or in every item.
- The objects of a variable are known only when the step runs. Their keys that the endpoint does not use are ignored, since an API response has more fields than the call needs. A variable that is not a list of objects fails the step: `for_each: 'users' is a string, not a list of objects`.
- **Data files** are relative to the workflow and read when it loads:
  - `.json`: a list of objects, with their types;
  - `.csv`: the first line names the columns, each other line is an item. Values are text and empty lines are skipped.
- An empty list runs nothing and passes.

**Running.**

- Items run in order, each one a whole call: `until` and `max_retries` apply to each one, and generated values are new for each one.
- **Every item runs, also after a failing one**, so a table shows every case that fails. Then the step has failed, and the workflow stops.
- `expect.status` may be a placeholder only in a `for_each` step (`status: ${status}`). Its value is a code, its text (`"201"`, as in a CSV) or a list of codes.
- `save` cannot be used with `for_each`, because every item would overwrite the value.
- The console shows one line per item: the step's name followed by `[2/3]`. A step without a `name` is shown with each item's path and query arguments, as in `get_user id=7 [2/3]`.
- The summary and the JUnit report count each item as a step.

## Cookies

Every workflow run has its own cookie jar, empty at the start. A `Set-Cookie` of any response is stored, redirects included, and every later request of the workflow sends the cookies that match its URL (domain, path, `Secure`, expiry). A cookie that expires, or that the server removes (`Max-Age=0`), is no longer sent. A session kept by cookies needs nothing in the API file:

```yaml
name: Cookie session
import:
  - ../api/testserver.yaml

steps:
  - name: Log in
    call: session_login
    with:
      password: password
    expect:
      cookies:
        sid: { exists: true }              # set by this response, or by an earlier one
    save:
      csrf: { cookie: XSRF-TOKEN }         # the value of a cookie, for a header

  - name: A write with the CSRF token
    call: session_note
    with: { csrf: "${csrf}", text: Hello }  # the endpoint sends X-XSRF-TOKEN: ${csrf}
    expect:
      status: 201

  - name: Log out
    call: session_logout
    expect:
      status: 204
      cookies:
        sid: { exists: false }             # removed by the server
```

- **Each workflow starts with no cookies**, also in a suite (`tinyprobe run tinyprobe/workflows`), so workflows do not depend on each other. `tinyprobe call` keeps no cookies from one call to the next ([decision 0003](decisions/0003-endpoints-declare-workflows-run.md)).
- `expect.cookies` and `until.cookies` check cookies by name, with the same operators as headers; values are text. A name refers to a cookie in the jar **after** the response, among those that would be sent to the request's URL. So `exists: true` passes after a login, and `exists: false` passes after a logout or when the cookie was never set. Names are case-sensitive.
- `save: { name: { cookie: XSRF-TOKEN } }` saves the cookie's value (no attributes), read the same way. A cookie that is not in the jar fails the step: `Cannot save 'csrf': no cookie 'XSRF-TOKEN' after this response`.
- A `Cookie` header written in the API file is sent as written, followed by the jar's cookies: `Cookie: lang=it; sid=abc`.
- The recorded request (`-v`, `--reporter ndjson`, the extension's response view) shows the `Cookie` header as it was sent, the jar's cookies included.
- Cookie attributes (`HttpOnly`, `Secure`, `SameSite`) cannot be checked yet; a header check on the text does it meanwhile: `Set-Cookie: { contains: HttpOnly }`.

## Generated values

Placeholders whose name starts with `$` produce a value instead of reading a variable:

| Placeholder | Value |
|---|---|
| `${$uuid}` | a random UUID, e.g. `"3f2a…"` |
| `${$timestamp}` | Unix time in seconds, a number |
| `${$randomInt}` | a random number from 0 to 999999 |
| `${$randomString}` | 8 random lowercase letters and digits |
| `${$now}` | the current UTC time, `"2026-10-16T09:41:00Z"`; with an offset: `${$now+2h}`, `${$now-30m}` |
| `${$today}` | the current UTC date, `"2026-10-16"`; with an offset: `${$today+1d}` |

**Every use generates a new value.** To use the same value in several places (an email that a step creates and a later step searches), define it in `variables`: a variable is resolved once per run.

```yaml
variables:
  newUserEmail: "smoke-${$uuid}@example.com"
  pickupAt: ${$now+2h}

steps:
  - call: create_user
    with: { email: "${newUserEmail}" }
  - call: list_users
    with: { search: "${newUserEmail}" }
```

A lone placeholder keeps the generated type: `count: ${$randomInt}` sends a number, `id: "${$randomInt}"` a string. The environment file is read as plain values: generated values work in `variables`, endpoints and steps.

## Checks

```yaml
expect:
  body:
    $.email: mario@example.com              # equality
    $.pagination.total: { gte: 3 }
    $.price: { gt: 0, lt: 100 }             # several operators: all must pass (see below)
    $.deletedAt: null
    $.id: { exists: true }
    $.tags: { contains: admin }             # array element or substring
    $.code: { ne: ERR_UNKNOWN }
    $.number: { matches: '^ORD-[0-9]+$' }   # a regular expression (see below)
```

| Operator | Meaning |
|---|---|
| *(plain value)* | equal |
| `eq` | equal, to compare a whole object: `$.user: { eq: { name: Ada } }` |
| `ne` | not equal |
| `gt`, `gte`, `lt`, `lte` | numeric comparison; for `duration` the values are times (`500ms`, `2s`, `1m`) |
| `exists` | the path exists (`true`) or not (`false`) |
| `contains` | the array contains the value, or the string contains the text |
| `in` | equal to one of the values of a list: `{ in: [active, pending] }` |
| `any` | at least one of a list of conditions holds: `{ any: [ { lt: 0 }, { gt: 100 } ] }` |
| `matches` | a regular expression finds a match in the value: `{ matches: '^ORD-[0-9]+$' }` |
| `schema` | the value is valid against a JSON Schema: `{ schema: ../schemas/user.json }` (see below) |

Equality is typed: `$.id: 1` matches the number `1`, `$.id: "1"` matches the string `"1"`; numbers compare by value (`9.9` equals `9.90`). Header values are text, so header checks compare text. A mapping is read as operators only when all its keys are operators; mixing operators and fields is an error. Expected values may contain placeholders (`$.id: ${userId}`). Every check of a step is evaluated and reported, not only the first failure.

### Regular expressions: `matches`

```yaml
expect:
  headers:
    Location: { matches: '^/api/orders/[0-9]+$' }
  cookies:
    sid: { matches: '^[0-9a-f]{32}$' }
  body:
    $.id: { matches: '^ORD-[0-9]{8}-[0-9]{4}$' }
    $.email: { matches: '(?i)@example\.com$' }      # (?i): ignore case
    $.createdAt: { matches: '^\d{4}-\d{2}-\d{2}T', exists: true }
```

- **It searches the value**, like JavaScript's `.match()`: `'@example\.com$'` passes for `ada@example.com`. Anchor with `^` and `$` to match the whole value.
- **Write the pattern in single quotes.** In single-quoted YAML a backslash is a plain character, so `'\d+'` is the pattern `\d+`. In double quotes YAML reads the backslashes first (`"\\d+"`); unquoted, a `#` after a space or a `: ` ends the value.
- **The syntax is .NET's**, the same as JavaScript's for usual patterns: classes, `\d \w \s`, anchors, groups, `a|b`, `{2,4}`, lookahead. Flags go inline at the start: `(?i)` ignores case, `(?m)` makes `^ $` match at each line, `(?s)` makes `.` match newlines; JavaScript's `/.../i` is `(?i)...`. `\d` also matches digits of other scripts; `[0-9]` matches only 0 to 9.
- **The value is matched as text:** a string as it is, a number or a boolean as its JSON text (`42`, `true`). `null`, objects, arrays and a missing value never match.
- **Placeholders are replaced first**, as in any value: `'^${prefix}-[0-9]+$'`. The variable's text goes in as it is, not escaped: a `.` in it matches any character.
- It works on body values, headers and cookies, in `expect` and `until`, with other operators in the same mapping and inside `any`. A pattern without placeholders is checked when the file is loaded: `invalid regular expression '^ORD-[0-9': Unterminated [] set`.
- **Each check has 1 second.** A pattern that takes longer on a value (such as `(a+)+$` on a long text) fails the step with `the regular expression took more than 1s on this value`, instead of hanging the run.

### JSON Schema: `schema`

```yaml
expect:
  body:
    $: { schema: ../openapi/openapi.yaml#/components/schemas/User }   # the whole body
    $.address: { schema: ../schemas/address.json }                      # a part of it
    $.id: { schema: { type: integer, minimum: 1 } }                     # a schema written here
```

```text
  ✗ User by id   GET   200   2.69ms  /api/users/1
      ✗ $.nickname schema required  actual (missing)
      ✗ $.id schema type "string"  actual 1
      ✗ $.address.city schema maxLength 5  actual "Cerignola"
```

- **A file or a mapping.** A path is relative to the file the check is in (a workflow or a sequence), like `import`; the file is JSON (`.json`) or YAML. After `#`, a JSON Pointer picks a schema inside it, so an OpenAPI document works as it is: `openapi.yaml#/components/schemas/User`. A mapping is a schema written in the step, for a type or a short rule.
- **`$` is the whole body**; any other JSONPath checks that value (`$.data` inside an envelope).
- **Read when the workflow is loaded.** A missing file, a pointer that leads nowhere, an unknown keyword or type, an invalid `pattern` is an error before any request, with the place in the schema: `../schemas/user.json#/properties/id: unknown keyword 'minimun' in a schema (did you mean 'minimum'?)`.
- **One failed check per problem**, at the JSONPath of the value that breaks the rule, with the keyword and the value (objects and arrays in short: `{…}`, `[3 items]`). At most 20 problems are listed per check, then `schema: 14 more problems`. A valid value is one passed check.
- **`$ref`** works inside the same file (`#/components/schemas/Address`, `#/$defs/address`) and to another file, relative to the file that has the `$ref` (`./address.json`, `common.yaml#/Money`). A schema can refer to itself (a tree). A `$ref` to a URL is an error: tinyprobe reads no schema from the network. A schema written in the step has no local `$ref`: put it in a file.
- It works on body values in `expect` and `until`, with other operators and inside `any`; not on headers or cookies (text: use `matches`).

One validator for JSON Schema drafts 4 to 2020-12 and the schema objects of OpenAPI 3.0 and 3.1:

| Kind | Keywords |
|---|---|
| any value | `type` (a name or a list; `integer` is a number without a fraction), `enum`, `const`, `nullable` (OpenAPI 3.0) |
| combining | `allOf`, `anyOf`, `oneOf`, `not`, `$ref`; `true` and `false` as schemas |
| objects | `properties`, `required`, `additionalProperties`, `patternProperties`, `minProperties`, `maxProperties` |
| arrays | `items` (a schema, or a list with `additionalItems`), `prefixItems`, `minItems`, `maxItems`, `uniqueItems`, `contains` |
| numbers | `minimum`, `maximum`, `exclusiveMinimum`, `exclusiveMaximum` (a number, or `true` as in draft 4 and OpenAPI 3.0), `multipleOf` |
| strings | `minLength`, `maxLength` (in characters), `pattern` (searches, like `matches`), `format` |

- `format` is checked for `date-time`, `date`, `time`, `email`, `uuid`, `uri`, `ipv4` and `ipv6`; other formats are not checked.
- Annotations are read and ignored: `title`, `description`, `default`, `examples`, `example`, `readOnly`, `writeOnly`, `deprecated`, `$schema`, `$id`, `$comment`, `discriminator`, `xml`, `externalDocs`, `contentMediaType`, `contentEncoding`, `$defs`, `definitions`, and every key that starts with `x-`.
- Any other keyword is an error, so a typo never makes a schema pass everything. Not supported yet: `if`/`then`/`else`, `dependentRequired`, `dependentSchemas`, `dependencies`, `propertyNames`, `unevaluatedProperties`, `unevaluatedItems`, `minContains`, `maxContains`, `$anchor`, `$dynamicRef`.
- In YAML, `type: [string, null]` (a real null) means the type `"null"`.

### Several conditions on one value

Put several operators in the same mapping: **all of them must pass**. Each one is reported on its own line, so a failure shows exactly which condition did not hold.

```yaml
expect:
  body:
    $.count: { gt: 0, lt: 10 }                               # a range: 0 < count < 10
    $.email: { eq: ada@example.com, contains: "@example.com" } # equality plus another condition
    $.id: { exists: true, gt: 0 }
  headers:
    Content-Type: { contains: json, ne: application/xml }
  duration: { gte: 10ms, lt: 500ms }
```

```text
  ✗ Get user  GET  200  12.4ms  /users/1
      ✓ $.count > 0
      ✗ $.count < 10  actual 12
```

To combine equality with other conditions write it as `eq`: a plain value is a single condition. A JSONPath cannot be repeated as a key (`$.email: …` twice), since YAML does not allow duplicate keys; tinyprobe reports it before running.

### Alternatives (OR)

The operators of a mapping are combined with AND. For OR there are three tools:

```yaml
expect:
  status: [200, 204]                                  # any of these status codes
  body:
    $.status: { in: [active, pending] }               # one of these values
    $.count: { any: [ { lt: 0 }, { gt: 100 } ] }      # one of these conditions
    $.deletedAt: { any: [ null, { exists: false } ] } # null, or not there at all
```

- **`in`** takes a list of values and passes when the actual value equals one of them; equality is typed, as for plain values.
- **`any`** takes a list of alternatives and passes when at least one holds. An alternative is a value (equality) or a mapping of operators, which must all hold: `{ any: [ 0, { gte: 10, lte: 20 } ] }` means "0, or between 10 and 20".
- **`status`** accepts a list of codes.

`in` and `any` work on headers too, and combine with the other operators of the same mapping: `{ in: [admin, owner], ne: guest }`. In the report each one is a single line:

```text
  ✗ get_user id=3  GET  200  352.78ms  /users/3
      ✗ $.id < 2 or (> 5 and < 10)  actual 3
      ✗ $.username in ["Bret","Antonette"]  actual "Samantha"
```

## Editor support

The [tinyprobe extension](https://github.com/kikutano/tinyprobe-vscode) for VS Code completes keys, endpoint names and params, and shows descriptions on hover. In other editors with the YAML language server (VS Code with the YAML extension, JetBrains IDEs, Neovim, Zed), the JSON Schema of the format (`tinyprobe schema`) gives completion and descriptions of the keys; point a file to it with a first line:

```yaml
# yaml-language-server: $schema=https://raw.githubusercontent.com/kikutano/tinyprobe/main/docs/schema/tinyprobe.schema.json
name: Users
steps:
  - sleep: 1s
```

## Validation before running

The whole workflow, imports included, is checked before the first request. Every problem is reported at once, as `file:line:column: message`:

- `call` to an endpoint that does not exist, and unknown keys (both with a "did you mean" suggestion);
- a required `param` missing from `with`, an argument in `with` that the endpoint does not use (a typo such as `pagesize`), or an `auth` set that does not exist;
- an invalid JSONPath, status code, operator or duration, an `until` without a condition, or a `timeout` shorter than `every`;
- an unknown generated value, such as `${$uid}` (with a "did you mean");
- an imported file that does not exist, or two endpoints with the same name.

```text
  ✗ The workflow is not valid (2 problems)

  workflows/users.yaml:12:5: unknown endpoint 'logn' (did you mean 'login'?)
  workflows/users.yaml:16:5: unknown key 'expcet' in a step (did you mean 'expect'?)
```

## Not supported yet

Every feature planned for the format so far is in. See the [roadmap](../ROADMAP.md) for what comes next.
