<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="media/logo-dark.svg">
    <img src="media/logo-light.svg" alt="tinyprobe logo: a shell prompt followed by a check mark" width="124">
  </picture>
</p>

# tinyprobe

[![CI](https://github.com/kikutano/tinyprobe/actions/workflows/ci.yml/badge.svg)](https://github.com/kikutano/tinyprobe/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/kikutano/tinyprobe?include_prereleases)](https://github.com/kikutano/tinyprobe/releases)
[![License: GPL-3.0](https://img.shields.io/badge/License-GPL--3.0-blue.svg)](LICENSE)
[![VS Code extension](https://img.shields.io/github/v/release/kikutano/tinyprobe-vscode?label=VS%20Code%20extension&color=orange)](https://github.com/kikutano/tinyprobe-vscode)
[![tinyprobe-app](https://img.shields.io/github/v/release/kikutano/tinyprobe-app?label=tinyprobe-app&color=orange)](https://github.com/kikutano/tinyprobe-app)

**[Why tinyprobe](#why-tinyprobe)** · **[Install](#install)** · **[Quick start](#quick-start)** · **[Migrate with your AI](#migrate-with-your-ai)** · [Command line](docs/cli.md) · [Workflow format](docs/workflow-format.md) · [Guide for coding agents](docs/llm-guide.md) · [Roadmap](ROADMAP.md)

**API tests your AI writes and your CI runs.** Give your coding agent a Postman collection, an OpenAPI document or your backend's code: it writes the tests as YAML files in your repository, checks them with tinyprobe until they pass, and tells you what it could not convert. Then the same files run in your terminal, in CI, in VS Code and in a small app, from one native binary. No account, no cloud.

```bash
curl -fsSL https://raw.githubusercontent.com/kikutano/tinyprobe/main/install.sh | sh
```

<p align="center">
  <img src="screen.png" alt="tinyprobe run output in the terminal" width="640">
</p>

## Why tinyprobe

- **Your AI does the migration, and checks its own work.** tinyprobe has no importers: the agent you already use converts the collection, following a guide built into the binary (`tinyprobe guide`). It validates every file with `inspect`, tries requests with `call` and runs the workflows with `run` until they pass, then writes `MIGRATION.md` with what it could not convert. On our [Postman benchmark](docs/specs/0002-migrate-from-postman.md#scores) (20 requests, 4 planted cases with no equivalent), Claude Code with Sonnet and with Haiku covered 18 of 18 convertible requests, reported 4 of 4 untranslatable cases, invented no syntax and committed no secrets.
- **Mistakes before requests.** A file is validated as a whole before anything is sent: every problem with its `file:line:column` and a "did you mean". A failing check shows the actual value. That is what lets an agent, or you, fix a test in one round.
- **No hidden state.** API files only describe endpoints; workflows run them, and every value a step uses is saved by a step you can read above it. Each workflow starts from zero, so it gives the same result on your machine, in CI and in any order ([decision 0003](docs/decisions/0003-endpoints-declare-workflows-run.md)).
- **Made for CI.** One Native AOT binary of about 9 MB that starts in about 20 ms: no runtime, nothing to install on the runner. A GitHub Action that annotates failing steps in pull requests, a Docker image, and JUnit reports for any CI server.
- **One engine, wherever you work.** The same YAML files and the same `tinyprobe` run in the terminal, in the [VS Code extension](#vs-code-extension), in the [app](#the-app) for people who don't use a terminal, and in your coding agent through MCP.
- **Your files, your repository.** Tests are plain YAML next to your code, reviewed in pull requests. Free and open source under the GPL-3.0.

## Install

**Linux / macOS**

```bash
curl -fsSL https://raw.githubusercontent.com/kikutano/tinyprobe/main/install.sh | sh
```

**Windows (PowerShell)**

```powershell
irm https://raw.githubusercontent.com/kikutano/tinyprobe/main/install.ps1 | iex
```

Then `tinyprobe --version`. `tinyprobe update` updates it in place, with the checksum verified. Archives for every platform, the Docker image (`ghcr.io/kikutano/tinyprobe`) and the other options are in [docs/cli.md](docs/cli.md#install).

## Quick start

`hello.yaml`:

```yaml
name: Hello tinyprobe
base_url: https://jsonplaceholder.typicode.com

endpoints:
  create_post:
    method: POST
    path: /posts
    body:
      title: ${title}
      userId: 1
  get_user:
    path: /users/${id}

steps:
  - name: Create a post
    call: create_post
    with:
      title: hello
    expect:
      status: 201
      body:
        $.id: 101
        $.title: hello
    save:
      postId: $.id              # available as ${postId} in the next steps

  - call: get_user
    with:
      id: 1
    expect:
      body:
        $.email: Sincere@april.biz
        $.address.city: { contains: Gwen }
      duration: { lt: 2s }
```

Run it:

```bash
tinyprobe run hello.yaml
```

```text
  ⚡ tinyprobe 0.25.0

  workflow : Hello tinyprobe
  file     : hello.yaml
  steps    : 2

  ✓ Create a post  POST   201   587.33ms  /posts
  ✓ get_user id=1  GET    200    76.89ms  /users/1

  checks..............: 100.00%  ✓ 6  ✗ 0
  steps...............: 2/2 passed
  http_reqs...........: 2  3/s
  http_req_duration...: avg=332.11ms min=76.89ms med=332.11ms max=587.33ms p(90)=536.28ms p(95)=561.8ms
  data_received.......: 559 B
  data_sent...........: 28 B
  workflow_duration...: 666.86ms

   PASSED   2 steps
```

When a step fails, every check of that step is listed with the actual value, and the steps after it are not run:

```text
  ✗ Wrong expectations  GET    200     4.73ms  /api/headers
      ✗ header X-Custom-Header == "something-else"  actual "hello-world"
      ✓ $.message contains "headers"
      ✗ duration < 1ms  actual 4.73ms
```

A mistake in the YAML is reported before any request is sent, with every problem and its location:

```text
  ✗ The workflow is not valid (2 problems)

  tests/users.yaml:12:5: unknown endpoint 'logn' (did you mean 'login'?)
  tests/users.yaml:16:5: unknown key 'expcet' in a step (did you mean 'expect'?)
```

To try more without writing anything, [example/](example/) tests a free public API: clone this repository, then `cd example && tinyprobe run workflows/01-read.yaml -e environment.yaml`.

<a name="generate-tests-with-a-coding-agent"></a>

## Migrate with your AI

Start with `tinyprobe init` at the root of your repository. It creates the `tinyprobe/` folder (`api/`, `workflows/`, `sequences/`, the env files), adds the env files with credentials to `.gitignore`, and adds a section to `AGENTS.md` that tells every coding agent to read `tinyprobe guide` before touching the tests. It never overwrites a file.

Then give your agent (Claude Code, Cursor, Copilot, Codex...) one of these prompts.

**From a Postman collection.** Export the collection (Collection v2.1) and each environment into the repository, for example in `postman/`:

```text
Read https://raw.githubusercontent.com/kikutano/tinyprobe/main/docs/llm-guide.md (or run `tinyprobe guide`)
and follow its section on migrating from Postman: convert the collection and the environments in postman/
to tinyprobe files in tinyprobe/, one folder at a time, and run each workflow until it passes. Put secrets
only in tinyprobe/env.local.yaml and add it to .gitignore. Write tinyprobe/MIGRATION.md with what you
converted, what you skipped and why, and tell me which failures look like backend bugs.
```

Variants of the same request ("Login", "Login - wrong password") become one endpoint called with different arguments, and the values your scripts saved become `save`. What has no equivalent (a script that computes a signature, `pm.sendRequest`) is not guessed: it is listed in `MIGRATION.md`, so you know what the tests do not cover. The same prompt works for Insomnia, Bruno or another tool's exports: name the folder and the tool.

**From an OpenAPI document:**

```text
Read https://raw.githubusercontent.com/kikutano/tinyprobe/main/docs/llm-guide.md (or run `tinyprobe guide`)
and follow it, starting from the OpenAPI document openapi.yaml: write one tinyprobe endpoint per operation
and workflows that check the documented status codes and response fields, then run them against the
local backend until they pass. Take test values from the seed data, not from the examples in the
document. Put credentials only in tinyprobe/env.local.yaml and add it to .gitignore. Tell me where the
backend does not do what the document says.
```

**From the backend code:**

```text
Read https://raw.githubusercontent.com/kikutano/tinyprobe/main/docs/llm-guide.md (or run `tinyprobe guide`)
and follow it: look at this backend's routes, authentication and seed data, write the tinyprobe API
file and workflows in tinyprobe/, then run them with tinyprobe until they pass. Put credentials only in
tinyprobe/env.local.yaml and add it to .gitignore. Tell me which failures look like backend bugs.
```

**Give your agent the tinyprobe tools.** `tinyprobe mcp` is an [MCP](https://modelcontextprotocol.io) server with the tools `guide`, `inspect`, `call` and `run`, with JSON arguments and results:

```bash
claude mcp add tinyprobe -- tinyprobe mcp          # Claude Code (--scope project shares it in .mcp.json)
```

```json
{ "servers": { "tinyprobe": { "type": "stdio", "command": "tinyprobe", "args": ["mcp"] } } }
```

That is `.vscode/mcp.json` for VS Code. Cursor reads `.cursor/mcp.json` with `{ "mcpServers": { "tinyprobe": { "command": "tinyprobe", "args": ["mcp"] } } }`, Codex `~/.codex/config.toml` with `[mcp_servers.tinyprobe]`, `command = "tinyprobe"`, `args = ["mcp"]`. It is listed in the [MCP Registry](https://registry.modelcontextprotocol.io) as `io.github.kikutano/tinyprobe`. `call` and `run` send requests to your backend, so your agent may ask you before using them.

[agents/](agents/) has ready-made instructions that send an agent to `tinyprobe guide`: a Claude Code plugin (`/plugin install tinyprobe --marketplace kikutano/tinyprobe`), a Cursor rule, Copilot instructions, and [`llms.txt`](llms.txt).

## Run in CI

```yaml
- uses: actions/checkout@v4
- uses: kikutano/tinyprobe@v0.25.0      # installs tinyprobe 0.25.0
  with:
    path: tinyprobe/workflows
    env-file: tinyprobe/env.ci.yaml
    vars: |
      token=${{ secrets.API_TOKEN }}
```

The action runs every workflow of the folder, also after a failing one, puts a summary on the job page and marks each failing step at its line in the pull request. On other CI servers use the Docker image or the binary, with `--junit` for the test tab: see [docs/cli.md](docs/cli.md#run-in-ci) for GitLab and the others. Exit codes: `0` passed, `1` a step failed, `2` an invalid file or arguments.

## The format at a glance

Endpoints and tests live in separate files, so the same endpoints serve many workflows:

```yaml
# api/server.yaml: endpoints only, no expectations
base_url: ${baseUrl}
auth:
  bearer:
    Authorization: "Bearer ${token}"
endpoints:
  login:
    method: POST
    path: /api/login
    params:
      username: admin        # default
      password:              # required
    body:
      username: ${username}
      password: ${password}
  get_user:
    path: /api/users/${id}
    auth: bearer
```

```yaml
# workflows/users.yaml: steps that call the endpoints
name: Users
import:
  - ../api/server.yaml
steps:
  - call: login
    with: { password: password }
    save: { token: $.access_token }

  - call: get_user
    with: { id: 1 }
    expect:
      status: 200
      body:
        $.email: gaetano@example.com
        $.orders: { gt: 0, lt: 100 }       # several conditions on one value: all must pass
        $.role: { in: [admin, owner] }     # OR: one of these values
```

| In a step | Description |
|---|---|
| `call` / `with` | The endpoint to call and its arguments. |
| `use` | Run a shared sequence of steps here (a login), with `with` for its params, or once per item with `for_each`. |
| `expect` | `status`, and checks on the `body` (by JSONPath), `headers`, `cookies` and `duration`: equality, comparisons, `contains`, `in`, `any`, `matches` (a regular expression) and `schema` (JSON Schema, also from an OpenAPI document). |
| `save` | Values of the response for the next steps, or a cookie's value. |
| `for_each` | Repeat the call once per item: a list, a variable or a CSV or JSON file. |
| `until` | Polling: repeat the call until conditions pass, every so often, up to a timeout. |
| `sleep` | A pause, e.g. `sleep: 2s`. |

API files also have auth sets (bearer, API key, Basic, OAuth2 client credentials), query strings, forms and file uploads; workflows have variables, generated values (`${$uuid}`, `${$now+2h}`) and named imports for several services. Everything is in [docs/workflow-format.md](docs/workflow-format.md). [docs/example/](docs/example/) uses every feature against a test server you start with one command, and is what CI runs.

## VS Code extension

[**tinyprobe for VS Code**](https://github.com/kikutano/tinyprobe-vscode) runs the same files from the editor: problems underlined as you save, completion and go to definition, **▶ Call** above every endpoint, **▶ Run workflow** and the Test Explorer, **Run to here** on every step, the workflow drawn as a diagram that lights up as it runs, the history of your calls and an environment editor.

<p align="center">
  <a href="https://github.com/kikutano/tinyprobe-vscode">
    <img src="https://raw.githubusercontent.com/kikutano/tinyprobe-vscode/main/media/screen.png" alt="A tinyprobe workflow open in VS Code: a Run workflow link above it, a green check next to each step, and the run view with the steps, the checks of the selected step and its response" width="800">
  </a>
</p>

Install it from the [VS Code Marketplace](https://marketplace.visualstudio.com/items?itemName=kikutano.tinyprobe-vscode), or [Open VSX](https://open-vsx.org/extension/kikutano/tinyprobe-vscode) for Cursor, Windsurf and VSCodium: `code --install-extension kikutano.tinyprobe-vscode`.

## The app

[**tinyprobe-app**](https://github.com/kikutano/tinyprobe-app) is tinyprobe for people who use neither a terminal nor VS Code: one small download for Windows, macOS and Linux that opens in the browser. Send any request from a form, run workflows and watch them as a diagram, and edit endpoints and steps with forms instead of YAML. It works on the same files, so what you do in the app runs unchanged in a terminal and in CI.

<p align="center">
  <a href="https://github.com/kikutano/tinyprobe-app">
    <img src="https://raw.githubusercontent.com/kikutano/tinyprobe-app/main/media/flow-run.gif" alt="tinyprobe-app: a workflow as a diagram that lights up as it runs, the items of a for_each counting up, and each value colored the same where it is saved and where it is used" width="600">
  </a>
</p>

## Learn more

- [docs/cli.md](docs/cli.md): every command and option, CI, exploring an API with `inspect` and `call`, the JSON for other tools.
- [docs/workflow-format.md](docs/workflow-format.md): the complete format.
- [docs/llm-guide.md](docs/llm-guide.md): the guide for coding agents, also printed by `tinyprobe guide`.
- [docs/protocol.md](docs/protocol.md): the JSON of `inspect --json`, the run events and the MCP tools.
- [docs/comparison.md](docs/comparison.md): tinyprobe next to Postman, Bruno and Hurl, and when to choose which.
- [example/](example/) and [docs/example/](docs/example/): a first look against a public API, and every feature against the test server.
- [CONTRIBUTING.md](CONTRIBUTING.md): building from source and how changes are made. [ROADMAP.md](ROADMAP.md): what comes next.

## License

Copyright (C) 2026 Gaetano Luigi Lenoci

tinyprobe is free software: you can use, change and share it under the terms of the [GNU General Public License, version 3](LICENSE) (GPL-3.0-only). A changed version may be shared, even for a fee, only under the same license and together with its source code.

Releases up to 0.21.0 were published under the MIT License, which still applies to them.
