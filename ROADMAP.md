# tinyprobe roadmap

The plan for **both repositories**: this one (the `tinyprobe` CLI, its format and the guide for coding agents) and [tinyprobe-vscode](https://github.com/kikutano/tinyprobe-vscode) (the VS Code extension). Every development session starts here.

## Principles

1. **The user's AI does the import.** tinyprobe ships no AI model and no direct importers. Users give their own coding agent (Claude Code, Cursor, Copilot, Codex, ...) their Postman collections, OpenAPI documents or backend code. tinyprobe provides what makes any agent good at it: the guide (`tinyprobe guide`), validation with precise errors, and the `inspect` / `call` / `run` loop that lets the agent check its own work. See [decision 0001](docs/decisions/0001-ai-does-the-import.md).
2. **One engine: the binary.** The extension and every other tool drive `tinyprobe` through its [JSON protocol](docs/protocol.md) and never reimplement parsing or checks.
3. **Plain text, Git and CI first.** Endpoints, workflows and environments are YAML files that live in the user's repository and run unchanged in CI.
4. **Endpoints declare, workflows run.** An API file only describes requests: no checks, no `save`, no state. Every value passed between requests is saved by a step of a workflow, and a single `call` remembers nothing. See [decision 0003](docs/decisions/0003-endpoints-declare-workflows-run.md).
5. **The app is a client of the CLI.** The visual app (M5) lives in its own repository and uses the binary through the YAML files and the JSON protocol, like the extension; the CLI does not know it exists. See [decision 0004](docs/decisions/0004-the-app-is-a-client-of-the-cli.md).

## Where we are

Released: **tinyprobe 0.21.0** (`inspect --source`, the file as written for editors; `matches` and `schema` since 0.20.0; the MCP Registry listing since 0.20.1; the test server as a Docker image and a guided `docs/example` since 0.19.0) and the extension **0.9.0** (go to definition on a `schema` file; sequence files, `for_each` items, History and Environment views since 0.7.0 and 0.8.0). M1, M2, M3 and M4 are done. M5 has its four features in **tinyprobe-app 0.4.0** ([kikutano/tinyprobe-app](https://github.com/kikutano/tinyprobe-app)), the app in the browser installed with one command: environment editor and request history, the diagram of a workflow, the visual editor (spec [0017](docs/specs/0017-app.md), [0018](docs/specs/0018-inspect-source.md)); signed installers (M5-5) are left.

- Format: API files with endpoints, params, query, headers, JSON bodies, forms and file uploads, and auth sets (headers, Basic, OAuth2 client credentials). Workflows import them, also under a name (`billing.login`), and run `call` / `with` / `expect` / `save` steps, with `sleep`, `until` (polling), `for_each` (tables of cases from the workflow, a variable or a CSV/JSON file), sequences shared by several workflows (`use`), cookies kept per run, generated values (`${$uuid}`, `${$now+2h}`...), AND/OR checks, regular expressions (`matches`) and JSON Schema checks (`schema`, also against the schemas of an OpenAPI document). Full validation before running, with `file:line:column` and "did you mean".
- CLI: `run` (one workflow, or a folder as a suite, with a JUnit report; `--to` stops after a step), `call`, `inspect`, `init`, `mcp` (an MCP server for agents), `schema` (JSON Schema of the format), `update`, `guide`; console and ndjson reporters; a GitHub Action (`kikutano/tinyprobe@vX.Y.Z`); Native AOT binaries for win-x64, linux-x64, linux-arm64, linux-musl-x64, linux-musl-arm64 and osx-arm64; a Docker image (`ghcr.io/kikutano/tinyprobe`); install scripts. A CI guard keeps the binary under 10 MiB and its start and runs under time budgets.
- Extension: validation on save, completion and hover, ▶ Call, ▶ Run workflow, Run to here, Test Explorer, run view, environment picker, a sidebar with the request History and the Environment editor, sequence files and go to definition, the items of `for_each` steps, Migrate with AI, Generate Tests with AI, the MCP tools in VS Code's chat. On the VS Code Marketplace, on Open VSX (namespace claim pending) and as a `.vsix` in GitHub Releases.

## How we work

- **Status:** `[ ]` todo · `[~]` in progress · `[x]` done · `[-]` dropped (reason in the task) · `[?]` needs a decision.
- **Areas:** `cli` this repository's code · `guide` `docs/llm-guide.md` and other docs for agents · `ext` tinyprobe-vscode · `app` tinyprobe-app, the visual app (M5) · `repo` project housekeeping.
- **Specs before code** for every task that changes the YAML format, the CLI surface (commands, options, output) or the protocol. Use `/spec`: it writes `docs/specs/NNNN-name.md` from the [template](docs/specs/0000-template.md). Code starts when the spec is *Approved*; then `/add-feature`. Other tasks need no spec.
- **Decisions** that constrain future work go in `docs/decisions/` as short records: context, decision, consequences.
- **Done** means the acceptance criteria are met and `scripts/dev.sh check` passes in every repository touched. The task is marked `[x]` in the same PR, with a link to the PR.
- **New ideas** go to the [Inbox](#inbox). We move them into a milestone when we plan the next one.
- Milestones are in order, and tasks inside one are roughly in order. There are no dates.

---

## M0 — Restart

Clean up the project before inviting people in.

- [x] **M0-1** `repo` Replace the old roadmap: this file, `docs/specs/`, `docs/decisions/`, the `/spec` skill. `Milestone1.md` removed: it planned the pre-0.2 format (the old roadmap is in git history: `git show 0e2981c:ROADMAP.md`). Commit 41144ac.
- [x] **M0-2** `repo` Development tooling in both repositories: `scripts/dev.sh`, `CLAUDE.md`, the `/add-feature`, `/release` and `/spec` skills. Commits 05f6f55, and 45bcb6c in tinyprobe-vscode.
- [x] **M0-3** `repo` Remove `Modelfile` (a personal Ollama configuration) from the repository.
- [x] **M0-4** `repo` README: fix the opening sentence ("Tired of paying for Postman?"), refresh the sample output (it still shows 0.4.0), link this roadmap.
- [x] **M0-5** `repo` Choose a name that is free for a developer tool (GitHub, npm, VS Code Marketplace, Open VSX, trademarks): **tinyprobe**, [decision 0002](docs/decisions/0002-name-tinyprobe.md).
- [x] **M0-6** `repo` `CONTRIBUTING.md` (how to build, test and propose a feature with a spec), issue and PR templates, in both repositories.
- [x] **M0-7** `ext` Publish on the VS Code Marketplace and Open VSX. Open VSX is published by the release workflow (`OVSX_PAT`); the Marketplace gets each `.vsix` by hand on its publisher page, because Azure DevOps retires the tokens it accepted on 2026-12-01 (see the Inbox).
- [x] **M0-8** `repo` Use the name tinyprobe everywhere, in both repositories: binary, install scripts, release assets, namespaces, docs, the extension id, commands and settings, the GitHub repositories. PRs #13, and #1 in tinyprobe-vscode.
- [x] **M0-9** `repo` A logo and an extension icon for tinyprobe: a shell prompt followed by a check mark (`media/` in both repositories). The social preview image `media/social-preview.png` is uploaded by hand in the repository settings.

## M1 — Migrate with your AI

The adoption hook: a Postman user gives their collection to their agent and gets tinyprobe workflows that pass.

- [x] **M1-1** `guide` Migration from Postman: a section of `docs/llm-guide.md` mapping collection v2.1 and environments to tinyprobe. Covers `{{var}}` → `${var}`, environments and globals → env file, collection and folder auth → `auth` sets, folders → workflows, `pm.test` / `pm.expect` → `expect`, `pm.environment.set` from the response → `save`, dynamic variables → generated values, retry loops → `until`. It also tells the agent to merge request variants into one endpoint used with different `with`. Spec: [0002](docs/specs/0002-migrate-from-postman.md). PR #14.
- [x] **M1-2** `guide` Other sources, a short mapping each: OpenAPI 3 / Swagger 2, Insomnia, Bruno, HAR, `curl` commands. Measured on an OpenAPI document of the test server (`docs/example/openapi/`). PR #15.
- [x] **M1-3** `guide` What cannot be translated, and the rule: never invent syntax; skip the request and list it in the report with the reason. Covers pre-request scripts with logic (signatures, computed values), `setNextRequest`, form-data and files, OAuth2 flows, cookies, non-JSON checks, unsupported JSONPath. PR #14.
- [x] **M1-4** `guide` Large collections: inventory first (requests, folders, environments), convert one folder at a time, finish with a coverage report (requests → endpoints, workflows, skipped with reasons). PR #14.
- [x] **M1-5** `repo` Ready-to-paste prompts in the README: migrate a Postman collection, start from an OpenAPI document, scan a backend (exists, review it).
- [x] **M1-6** `guide` Benchmark for the guide: a Postman collection of the test server with variables, scripts and variants (`docs/example/postman/`), a reference conversion, and a procedure to score an agent's conversion: rounds of `inspect` / `run` until it passes, requests covered, invented syntax. Run with at least two agents before and after M1-1…M1-4. *Done when:* the scores are recorded in the spec of M1-1. Run with Claude Code (Sonnet and Haiku); other agents can be added by hand. PR #14.
- [x] **M1-7** `cli` `tinyprobe run <folder>`: runs every workflow of a folder, one summary, exit code `1` if any failed. Agents and CI run a whole migration with one command. Spec: [0003](docs/specs/0003-run-a-folder.md). PR #16.
- [x] **M1-8** `cli` `tinyprobe init`: creates the `tinyprobe/` layout of the guide (`api/`, `workflows/`, `env.example.yaml`), the `.gitignore` entries, and a short `AGENTS.md` section that points agents to `tinyprobe guide`. Spec: [0004](docs/specs/0004-init.md). PR #17.
- [x] **M1-9** `ext` "tinyprobe: Migrate with AI": pick a collection or an OpenAPI file, build the migration prompt, and hand it to the editor's chat, or copy it to the clipboard when there is no chat. tinyprobe-vscode PR #2.

## M2 — Agents everywhere

Make tinyprobe a tool that agents use directly, not only a format they write.

- [x] **M2-1** `cli` `tinyprobe mcp`: an MCP server over stdio with the tools `guide`, `inspect`, `call` and `run`, returning the protocol JSON. Native AOT-safe. Spec: [0005](docs/specs/0005-mcp-server.md) PR #18.
- [x] **M2-2** `guide` Ready-made agent packages: a Claude Code skill, Cursor rules, Copilot instructions and `llms.txt`, each a thin wrapper around `tinyprobe guide`. In `agents/` and `llms.txt`; the Claude Code plugin installs from this repository as a marketplace. PR #19.
- [x] **M2-3** `ext` Completion and hover in tinyprobe files: keys, endpoint names after `call:`, params in `with`, from `inspect --json`. Spec: [0006](docs/specs/0006-completion-and-hover.md) PR #20, tinyprobe-vscode PR #5.
- [x] **M2-4** `ext` "tinyprobe: Generate Tests with AI": runs on the model the user already has (Copilot or another provider), with the guide as instructions and `inspect` / `run` to check the result. Built on the editor's own agent instead of a loop of ours on the Language Model API: the command opens the chat in agent mode with the prompt for the backend's code, and the agent uses the tinyprobe MCP tools that M2-5 registers. tinyprobe-vscode PR #4.
- [x] **M2-5** `ext` Register the tinyprobe MCP server with VS Code (`vscode.lm.registerMcpServerDefinitionProvider`), so Copilot and other chat agents in the editor get the tools without configuration. Needs M2-1. tinyprobe-vscode PR #3.

## M3 — CI

- [x] **M3-1** `cli` JUnit XML report, read by every CI server. Spec: [0007](docs/specs/0007-junit-report.md). PR #21.
- [x] **M3-2** `cli` An official GitHub Action: installs a pinned `tinyprobe` and runs a folder of workflows, with a job summary. In this repository (`action.yml`): `uses: kikutano/tinyprobe@v<version>` installs the binary of the same release. PR #22.
- [x] **M3-3** `cli` A small Docker image: `ghcr.io/kikutano/tinyprobe`, the release's Alpine (musl) binary on `runtime-deps` Alpine, amd64 and arm64, pushed by the Release workflow. Alpine binaries are release assets too (`install.sh` and `tinyprobe update` pick them on Alpine). PR #23.
- [x] **M3-4** `cli` Failing steps as GitHub Actions annotations, at the step's line. Done by the GitHub Action, from the JUnit report (`annotations` input). PR #22.

## M4 — What Postman users miss

- [x] **M4-1** `cli` `multipart/form-data` bodies and file uploads, and `application/x-www-form-urlencoded` forms. Spec: [0008](docs/specs/0008-form-and-multipart-bodies.md). PR #24.
- [x] **M4-2** `cli` Cookies: a cookie jar per run, `expect.cookies`, `save` from a cookie. Spec: [0009](docs/specs/0009-cookies.md). PR #25.
- [x] **M4-3** `cli` Basic auth and OAuth2 client credentials: `basic` and `oauth2` in auth sets. Spec: [0010](docs/specs/0010-basic-and-oauth2.md). PR #26.
- [x] **M4-4** `cli` `for_each`: repeat a step over a list of items (written, a variable, or a CSV or JSON file). Spec: [0011](docs/specs/0011-for-each.md). PR #27.
- [x] **M4-5** `cli` Reusable step sequences (a shared "log in"): sequence files run with `use`. Spec: [0012](docs/specs/0012-reusable-sequences.md). PR #28.
- [x] **M4-6** `cli` Namespaced imports (`call: server.login`): `- billing: ../api/billing.yaml` in `import`. Spec: [0013](docs/specs/0013-namespaced-imports.md). PR #30.
- [x] **M4-7** `ext` Request history: the last calls with their responses, to send again. The History view of the tinyprobe sidebar (last 50 calls, kept in the extension's workspace storage). tinyprobe-vscode PR #7.
- [x] **M4-8** `ext` Environment editor: see and edit the variables of the selected environment. The Environment view of the tinyprobe sidebar: edits only that variable's lines, comments kept, secrets hidden until shown. tinyprobe-vscode PR #7.
- M4-9, the visual request editor, moved to M5 as M5-4.
- [x] **M4-10** `cli` `ext` Run to here: `tinyprobe run <workflow> --to <step>` runs the workflow up to that step and stops; in the extension, a ▶ Run to here on every step shows that step's response. Tries a flow one request at a time without state between single calls ([decision 0003](docs/decisions/0003-endpoints-declare-workflows-run.md)). Spec: [0014](docs/specs/0014-run-to-here.md). PR #31, tinyprobe-vscode PR #6 (the extension is released after tinyprobe 0.18.0).
- [x] **M4-11** `repo` A performance guard in CI: the Native AOT binary must stay under a size budget, and `--version`, `inspect` and a run of many fast requests under a time budget (measured on 2026-10-28: 9.09 MB, 13 ms to start, 0.6 ms per step against the local test server). A PR that goes over fails, so a slower or heavier tinyprobe is a choice and never an accident. `scripts/perf.sh`, in the CI job and as `scripts/dev.sh perf`; budgets 10 MiB, 50 ms, 500 ms for `inspect` of 500 steps, 2 s for a run of them. PR #29.

### Consolidation, before M5

What the features of M4 need to be easy to try and complete to use, and the checks people coming from Postman look for first.

- [x] **M4-12** `repo` `cli` Examples anyone can run from the sources. Today the complete suite (`docs/example`) needs the .NET SDK to start the test server, and `example/` (JSONPlaceholder) cannot show auth, cookies, forms, `for_each`, sequences or named imports. *Done when:* the test server starts without the .NET SDK (a self-contained binary in the release assets, or a Docker image); `docs/example` has a README that walks through the features one workflow at a time, with the commands to run (`run`, a folder with `--junit`, `--to`, `call`, `init`); and `until`, generated values and workflow `variables` are added there, so one suite covers the whole format. Done: the image `ghcr.io/kikutano/tinyprobe-testserver` (`docker/testserver/`, built and run by CI on amd64 and arm64, published by the Release workflow), [docs/example/README.md](docs/example/README.md), and `docs/example/workflows/jobs.yaml` (`until`, generated values, `variables`) with a jobs route on the test server; `any` was missing too and is now in `catalog.yaml`.
- [x] **M4-13** `ext` Sequence files in the extension: no ▶ Run workflow or Test Explorer entry for them, completion of a `use` step's `with` from the sequence's `params`, and "go to definition" on a `use` path. (Was in the Inbox.) Done: sequences are validated and completed, with no Run lens or test; completion of a `use`'s `with` from the params, hover on a `use`, and go to definition on `call`, `use`, `import` and `file`. tinyprobe-vscode PR #8.
- [x] **M4-14** `ext` The items of a `for_each` step in the run view (the `iterations` of `step_completed`), each with its request and response, and in the Test Explorer. (Was in the Inbox.) Done: a row per item in the run view, with its own request and response, and the items as children of their step in the Test Explorer. tinyprobe-vscode PR #9.
- [x] **M4-15** `cli` A `matches` operator: a regular expression on a body value, a header or a cookie, in `expect` and `until`. Spec: [0015](docs/specs/0015-matches.md). PR #33.
- [x] **M4-16** `cli` Check a response against a JSON Schema (a file next to the API file, or a schema of the OpenAPI document the endpoints came from). Spec: [0016](docs/specs/0016-json-schema.md). PR #34, tinyprobe-vscode PR #10.
- [x] **M4-17** `repo` "Where we are" brought up to date with tinyprobe 0.18.0 and the extension 0.7.0.
- [x] **M4-18** `repo` List `tinyprobe mcp` where agents and their users look for MCP servers: the official MCP Registry (registry.modelcontextprotocol.io, a `server.json` published with `mcp-publisher`; the Docker image `ghcr.io/kikutano/tinyprobe` can be its OCI package, with `mcp` as the argument and the label the registry checks), then catalogs such as Smithery and the awesome-mcp lists. Publish from the Release workflow if the registry allows it, so each release updates the listing. It helps people find tinyprobe, and needs nothing from M5, so it comes first (it was M6-2). Done: `server.json`, the label on the image, and the `mcp-registry` job of the Release workflow (GitHub OIDC), from 0.20.1; Smithery and the awesome-mcp lists are submitted by hand.

## M5 — The app: tinyprobe for people who don't use the CLI

A Postman-like app for people who use neither a terminal nor VS Code: download it, click the icon, and a page opens in the browser to call endpoints, run workflows and pick the environment. As light as the binary: no account, no cloud, no runtime to install, the YAML files of the user's folder. It lives in its own repository, **tinyprobe-app**, and is a client of the CLI ([decision 0004](docs/decisions/0004-the-app-is-a-client-of-the-cli.md)): a small launcher serves the page on `127.0.0.1` and runs `tinyprobe` through its contracts (the YAML format, `inspect --json`, the ndjson events, `tinyprobe schema`). What the app creates runs unchanged with `tinyprobe run` and in CI. Everything visual goes here too: forms instead of YAML, and pictures of the flows, in the app and in the extension.

- [x] **M5-1** `app` The first version of tinyprobe-app. A launcher in C# Native AOT (3.5 MB with the page, measured on 2026-10-31) that serves a page of plain HTML, JavaScript and CSS with no framework and no build step, opens it in the browser, and stops when its console window is closed; a second click on the icon opens the running one. It uses the `tinyprobe` of the `PATH` when it is new enough, or downloads its own copy with the checksum verified. The page opens a folder (with recent folders), lists its API files, workflows and sequences, shows the problems of invalid files, sends an endpoint from a form, runs workflows with live results (the extension's run view, shared), picks the environment, and creates a new request from a form that writes the YAML and checks it with `inspect`. Security: `127.0.0.1` only, a session cookie set by the launcher, `Host` and `Origin` checked, files only inside the opened folders. Releases for every platform of the CLI; the installer adds an icon (Start menu and desktop on Windows, a `.desktop` file on Linux, a `.app` on macOS). Spec: [0017](docs/specs/0017-app.md). Released as tinyprobe-app 0.1.0, then 0.1.1 with the install command (tinyprobe-app PR #1); the run view shared with the extension in tinyprobe-vscode PR #11.
- [x] **M5-2** `app` Request history and an environment editor in the app, shared with the extension tasks M4-7 and M4-8 where the code can be the same. Done in tinyprobe-app 0.2.0 (PR #2): the Environment and History views, with the extension's code moved to tinyprobe-app's `web/shared/` and copied back (tinyprobe-vscode PR #12).
- [x] **M5-3** `app` A self-explanatory picture of the flows being run: the steps of a workflow as a diagram (what each step calls, the values it saves and the steps that use them, `until` loops, `for_each` iterations), lit up live as the run goes. Someone who opens a workflow for the first time understands it at a glance. Done in tinyprobe-app 0.3.0 (PR #3): the workflow view draws its steps, sequences, `until` and `for_each`, and a line for each saved value down to the steps that use it, lit up as the run goes; the model and the drawing are in `web/shared/flow.js` for the extension.
- [x] **M5-4** `app` `ext` Visual editor for people who prefer forms to YAML: endpoints, auth sets, workflow steps and their checks; it writes the YAML (it was M4-9). The same forms in the app and in the extension, where the code can be shared. M5-1 starts it with the form for a new request. Spec for the CLI side: [0018](docs/specs/0018-inspect-source.md). Done in the app (tinyprobe-app 0.3.0, PR #4): endpoints, auth sets, new workflows and their steps edited with forms, the rest as YAML one block at a time, on `inspect --source` (PR #35). The same forms in the extension are left for later.
- [ ] **M5-5** `app` Signed installers, so that macOS and Windows open the app without warnings: Apple Developer ID and notarization, a Windows signing certificate (or Azure Trusted Signing), then Homebrew, winget and Scoop. The same signing for the `tinyprobe` binary. Before M6, since people who are not developers stop at those warnings.

## M6 — Tell the world

- [ ] **M6-1** `repo` Rewrite `README.md` around the killer features, after M4 and M5: the migration done by the user's own AI and made reliable by the guide, the validation and the `inspect` / `call` / `run` loop (with the bench numbers); tests without hidden state (API files declare, workflows run); CI first (one binary, Docker image, GitHub Action, JUnit); one engine for the CLI, the extension, MCP and the app; no account and no cloud. Lead with what makes tinyprobe different, then install and quick start; move the reference material to `docs/`.

## M7 — Later: more protocols and authentication

Low priority: after M5 and M6, in the order users ask for them. Each needs a spec, and each must fit the size and speed budgets of `scripts/perf.sh`, or ship apart from the main binary.

- [ ] **M7-1** `cli` OAuth2 beyond client credentials: authorization code with PKCE (a browser login with a redirect to localhost), refresh tokens, and the password grant for old APIs.
- [ ] **M7-2** `cli` Other authentication: Digest, AWS Signature v4, and HMAC signatures of the request (what Postman pre-request scripts compute most often).
- [ ] **M7-3** `cli` Network settings: client certificates (mTLS), an HTTP proxy (and `HTTPS_PROXY`), accepting a self-signed certificate for a local server, HTTP/2.
- [ ] **M7-4** `cli` Bodies that are not JSON: checks on the raw text of any body, XML with a small XPath subset, HTML.
- [ ] **M7-5** `cli` GraphQL: a `graphql` body (query and variables) and checks of `data` and `errors`. GraphQL over POST works today as a JSON body.
- [ ] **M7-6** `cli` gRPC: unary calls described from `.proto` files or server reflection, the response checked as JSON. Needs HTTP/2 and protobuf without reflection: measure the cost in size before deciding whether it lives in the main binary.
- [ ] **M7-7** `cli` WebSocket and Server-Sent Events: connect, send messages, and expect messages within a timeout.
- [ ] **M7-8** `cli` A mock server: `tinyprobe mock <api folder>` answers the endpoints of the API files with example responses, for front-end work and for the examples.
- [?] **M7-9** `cli` Scripts for values that no generated value covers (signatures, computed fields). A script engine conflicts with a light binary and with declarative files ([decision 0003](docs/decisions/0003-endpoints-declare-workflows-run.md)): decide first, and prefer more generated values (`${$hmac(...)}`) if they cover the cases.

## Parked

Not planned now. We reconsider them after M5.

- Load testing: N concurrent copies of a workflow, with throughput and latency percentiles.
- A cloud service: shared workspaces, run history, dashboards.

## Dropped

- [-] Direct importers (`tinyprobe import postman` and similar), and an AI model or provider inside tinyprobe. The user's agent does both better; see [decision 0001](docs/decisions/0001-ai-does-the-import.md).

## Inbox

New ideas, not planned yet.

- `ext` Bring the extension's copy of tinyprobe-app's `web/shared/` up to date (`scripts/dev.sh sync-shared`, VERSION from 0.2.0): the run view shows the request before the response and folds saved values (0.3.1), and has a call mode for a single request, with its HTTP status instead of PASSED (0.4.1), which the extension's ▶ Call should use (`reset(title, env, { call: true })`); the workflow diagram (`flow.js`) can come with it. Found while dogfooding on 2026-11-02.
- `cli` `tinyprobe call` prints a green ✓ before its request line whatever the status (a 401 included): a single call is not a test (decision 0003), so its line should show the status without a verdict, as tinyprobe-app 0.4.1 does. An output change: it needs a short spec. Found while dogfooding on 2026-11-02.
- `cli` Faster loading of large workflows: loading and validating cost about 0.2 ms per step (450 ms for 2000 steps, the same in 0.17.0), likely the arguments of every step being recomputed from the endpoint's templates. Profile before changing it.
- `cli` `for_each` over a sequence (`use` with `for_each`): the steps of the sequence once per item, for a create/read/delete per item. Builds on spec 0012.
- `ext` Publish to the VS Code Marketplace from the release workflow, without a token: Trusted Publishing when Microsoft ships it, or Microsoft Entra ID with a GitHub federated credential. Until then each `.vsix` is uploaded by hand.
