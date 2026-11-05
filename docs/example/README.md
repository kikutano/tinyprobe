# tinyprobe examples: the test server

A complete tinyprobe project that uses **every feature of the format**, against a small test server you run on your machine. It is also what CI runs on every change. For a first look with nothing to start, see [example/](../../example/) (it calls a public API, JSONPlaceholder, which has no authentication, cookies or uploads to show).

## 1. Start the test server

With Docker, nothing else to install:

```bash
docker run --rm -p 127.0.0.1:5122:5122 ghcr.io/kikutano/tinyprobe-testserver
```

The server accepts fixed credentials (`admin` / `password`) and fake tokens on purpose. Keep the `127.0.0.1:` in `-p`, so only your machine reaches it and not your network. Its data lives in memory: stop it with Ctrl+C, and the next start begins from the same seed data.

The image `latest` follows the last release. To run the examples of a given release, use its version as the tag (`ghcr.io/kikutano/tinyprobe-testserver:X.Y.Z`): the image is published with each release from 0.19.0.

From the sources instead, with the .NET 10 SDK: `scripts/dev.sh server` (or `dotnet run --project tests/Tinyprobe.RealServer.Tests`).

Check that it answers:

```bash
curl http://localhost:5122/api/public
```

## 2. Run everything

From the root of the repository, with [tinyprobe installed](../../README.md#install):

```bash
tinyprobe run docs/example/workflows -e docs/environment.yaml
```

Every workflow runs and one summary closes the run. The values the workflows need (the base URL, the API key, the OAuth2 client) are in [docs/environment.yaml](../environment.yaml), passed with `-e`.

## 3. Walk through the features

Each workflow is short and commented. Read it, then run it alone with `-v` to see every request and response:

```bash
tinyprobe run docs/example/workflows/users.yaml -e docs/environment.yaml -v
```

| Workflow | What it shows |
|---|---|
| [users.yaml](workflows/users.yaml) | The basics: `call` with `with` arguments, `expect` on status and body, `save` a value for the next steps. **JSON Schema** checks: a user against the schema of the OpenAPI document (`openapi.yaml#/components/schemas/User`), a page against [schemas/user-page.json](schemas/user-page.json), the type of a value with a schema written in place. The whole life of a user the workflow creates itself: create, replace, patch, delete, check it is gone. Query parameters (`active`, `search`), numeric operators (`gt`, `gte`, `lte`), a regular expression on a value (`matches`). |
| [catalog.yaml](workflows/catalog.yaml) | Checks deep in a nested JSON response: operators (`in`, `contains`), several on one value (AND), and `any` (OR: one of a list of conditions). |
| [http.yaml](workflows/http.yaml) | A **named import** (`- probe: ../api/probe.yaml`, called as `probe.health`), a **sequence** shared by several workflows (`use: ../sequences/log-in.yaml`), request and response headers (a UUID header checked with `matches`), a response-time check (`duration`), settings (`timeout`, `max_retries`). |
| [auth.yaml](workflows/auth.yaml) | **Auth sets** of the API file: a bearer token saved from the login, an API key, both together, **Basic** (`basic:`), **OAuth2 client credentials** (`oauth2:`, client in a Basic header or in the form). |
| [session.yaml](workflows/session.yaml) | **Cookies**: a login that sets a session cookie, `expect.cookies` (the session id checked with `matches`), a CSRF token saved from a cookie (`save: { csrf: { cookie: XSRF-TOKEN } }`) and sent as a header, log out, a form login that follows a redirect. |
| [files.yaml](workflows/files.yaml) | A **form** (`application/x-www-form-urlencoded`) and a **multipart upload** of two files from [fixtures/](fixtures/). |
| [tables.yaml](workflows/tables.yaml) | **`for_each`**: one step, one call per item. Items written in the workflow (the expected status comes from the item), read from a CSV file ([data/tokens.csv](data/tokens.csv)), and taken from a list the workflow saved. Then a sequence ([user-lifecycle.yaml](sequences/user-lifecycle.yaml)) runs once per item with `for_each` on its `use`: create a user, read it back, delete it. |
| [jobs.yaml](workflows/jobs.yaml) | **`until`** (polling): start a report and repeat the read until it is done. **Generated values** (`${$uuid}`, `${$now}`, `${$randomInt}`) and workflow **`variables`**, resolved once per run so every step sees the same name. |

The endpoints are declared once, in [api/testserver.yaml](api/testserver.yaml): what to send, which arguments each one takes, which auth set it uses. The API file has no checks and no saved values: what runs, what is checked and what is passed between requests is always in a workflow.

## 4. Try the commands

```bash
# Call one endpoint, with its arguments, and see the whole response
tinyprobe call docs/example/api/testserver.yaml status --with code=404 -e docs/environment.yaml

# Run a workflow up to a step (its number or its name) and stop: the steps after it are not run
tinyprobe run docs/example/workflows/users.yaml -e docs/environment.yaml --to "User by id" -v

# Every workflow, with a JUnit report for a CI server
tinyprobe run docs/example/workflows -e docs/environment.yaml --junit reports/tinyprobe.xml

# What a file declares, as JSON for tools and agents
tinyprobe inspect docs/example/workflows/users.yaml --json
```

Make a mistake on purpose: change `call: get_user` into `call: get_usr` in `users.yaml` and run it again. Nothing is sent; tinyprobe stops with the line and column of the problem, and `did you mean 'get_user'?`.

To start a project of your own with the same layout, run `tinyprobe init` in an empty folder.

## 5. Migrate from another tool

The same test server described by other tools, to try a migration with your coding agent (`tinyprobe guide` is what it reads):

- [postman/](postman/): a Postman collection and environment, with variables, scripts and request variants.
- [openapi/](openapi/): an OpenAPI 3 document.

Each folder's README has the prompt to give your agent. The workflows of this folder are the reference result.
