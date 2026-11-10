# The tinyprobe command line

The reference of the `tinyprobe` binary: installing it, its commands and options, running it in CI, exploring an API, and the JSON it prints for other tools. The YAML format is in [workflow-format.md](workflow-format.md).

## Install

**Linux / macOS**

```bash
curl -fsSL https://raw.githubusercontent.com/kikutano/tinyprobe/main/install.sh | sh
```

**Windows (PowerShell)**

```powershell
irm https://raw.githubusercontent.com/kikutano/tinyprobe/main/install.ps1 | iex
```

Or download the archive for your platform from [Releases](https://github.com/kikutano/tinyprobe/releases) (`win-x64`, `linux-x64`, `linux-arm64`, `linux-musl-x64` and `linux-musl-arm64` for Alpine, `osx-arm64`) and put `tinyprobe` on your `PATH`. `TINYPROBE_VERSION=0.25.0` before the install script pins a version.

**Docker**

```bash
docker run --rm -v "$PWD:/work" ghcr.io/kikutano/tinyprobe:0.25.0 run tinyprobe/workflows -e tinyprobe/env.local.yaml
```

The image is the release's Alpine binary on Microsoft's .NET `runtime-deps` Alpine base, for `linux/amd64` and `linux/arm64`; tags are the version (`0.25.0`), the minor (`0.25`) and `latest`. The current folder is mounted as `/work`. Inside the container `localhost` is the container itself: on Linux add `--network host` to test an API running on your machine, on macOS and Windows use `host.docker.internal` as the host. Add `--user "$(id -u):$(id -g)"` so that a report written with `--junit` belongs to you.

### Update

```bash
tinyprobe update
```

It downloads the latest release for your platform, verifies its SHA-256 checksum and replaces the binary in place. `tinyprobe update --check` only tells you whether a newer version exists. Re-running the install script works too.

## Commands

```text
  tinyprobe run <workflow.yaml | folder>... [options]
  tinyprobe call <file.yaml> <endpoint> [--with name=value]... [options]
  tinyprobe inspect <file.yaml> [--json [--source]]
  tinyprobe init [folder] [--base-url <url>]
  tinyprobe update [--check] [--force]
  tinyprobe guide       Print the guide for coding agents (LLMs) that write tinyprobe files.
  tinyprobe mcp         MCP server on stdin/stdout: the tools guide, inspect, call and run, for agents.
  tinyprobe schema      Print the JSON Schema of tinyprobe files, for editors.
  tinyprobe --help
  tinyprobe --version

'run' runs workflows. A folder is read recursively: every YAML file with a top-level
'steps' key is a workflow, other files are skipped. With several workflows, each one
starts from zero, a failing one does not stop the others, and a summary comes last.
'call' sends one request to an endpoint of an API or workflow file and prints the response.
'inspect' validates a file and lists its endpoints, variables and steps.
'init' creates the tinyprobe/ folder, the .gitignore lines and an AGENTS.md section for
coding agents, in the current folder or the one given. It never overwrites a file.

Options for 'run' and 'call':
  -e, --env <file>        Environment file (YAML) with initial variables, e.g. baseUrl, apiKey.
  --var <name=value>      Set a variable, over the environment file. Repeatable.
  --with <name=value>     ('call' only) An argument of the endpoint, like a step's 'with'. Repeatable.
  --reporter <name>       console (default) or ndjson: one JSON event per line, for tools.
  --junit <file>          ('run' only) Also write a JUnit XML report, for CI servers.
  --to <step>             ('run' only, one workflow) Run up to this step, included, and stop:
                          its number, as 'inspect' lists the steps, or its name.
  --skip <step>           ('run' only, one workflow) Leave this step out of this run, the file
                          unchanged: its number or its name, as for --to. Repeatable.
  --resume <file | ->     ('run' only, one workflow) Continue a run cut by --to after its last
                          step, from the "state" of its run_finished event (- reads stdin).
  -v, --verbose           Print variables, requests and responses while running.
  --no-color              Disable colors (also honored: the NO_COLOR environment variable).

Values of --var and --with are typed like YAML: 42 is a number, true a boolean,
'"42"' a string, '[a, b]' a list.

Options for 'inspect':
  --json                  Print JSON, for tools.
  --source                With --json, also the file as written and where its blocks are, for editors.
  --no-color              Disable colors.

Options for 'init':
  --base-url <url>        baseUrl of the env files (default: http://localhost:8080).

Options for 'update':
  --check                 Only tell whether a newer version is available.
  --force                 Reinstall the latest version, also over a development build.

  -h, --help              Show this help.
  --version               Show the version.

Exit codes ('run', 'call', 'inspect'):
  0  all steps passed ('call': a response was received; 'inspect': the file is valid)
  1  at least one step failed (several workflows: at least one workflow failed)
  2  invalid arguments, file not found, invalid file, or a folder without workflows
```

A workflow stops at its first failing step. Given a folder or several files, `tinyprobe run` runs every workflow, also after a failing one, and ends with one summary of all of them; exit code `0` means every workflow passed:

```bash
tinyprobe run tinyprobe/workflows -e tinyprobe/env.local.yaml
```

A folder is read recursively, and only workflows run: API files and env files in it are skipped. Each workflow starts from zero, so values saved in one are not seen by the next. When the output is not a terminal (CI logs, pipes) the spinner is turned off and each step is printed on its own line.

## Run in CI

Run the whole folder, and write a JUnit report that your CI server shows in its test tab: every step is a test, with its file and line, and a failed one shows the request line and every check with its actual value. The report has no headers and no bodies, since CI servers keep it and requests carry secrets.

**GitHub Actions**, with the action of this repository:

```yaml
- uses: actions/checkout@v4
- uses: kikutano/tinyprobe@v0.25.0      # installs tinyprobe 0.25.0
  with:
    path: tinyprobe/workflows
    env-file: tinyprobe/env.ci.yaml
    vars: |
      token=${{ secrets.API_TOKEN }}
```

It installs the binary of the same release (checksum verified), runs the folder, writes `tinyprobe-report.xml`, puts a summary on the job page, and marks each failing step as an error at its line in the workflow file, in the run and in pull requests. Inputs: `path` (files or folders), `env-file`, `vars` (one `name=value` per line; the log shows the names only), `version`, `junit`, `summary`, `annotations`, and `tinyprobe-path` to use a binary already on the runner. It runs on Linux, macOS and Windows runners. Upload the report with `actions/upload-artifact` to keep it.

**GitLab CI**, with the Docker image:

```yaml
api-tests:
  image:
    name: ghcr.io/kikutano/tinyprobe:0.25.0
    entrypoint: [""]     # GitLab runs the script in a shell, not through tinyprobe
  script:
    - tinyprobe run tinyprobe/workflows -e tinyprobe/env.ci.yaml --junit reports/tinyprobe.xml
  artifacts:
    when: always
    reports:
      junit: reports/tinyprobe.xml
```

The same image works on any CI that runs jobs in a container (Bitbucket Pipelines, CircleCI, Woodpecker, Jenkins with Docker). In a job that needs its own image, install the binary instead: `curl -fsSL https://raw.githubusercontent.com/kikutano/tinyprobe/main/install.sh | TINYPROBE_VERSION=0.25.0 sh`, then run `~/.local/bin/tinyprobe`. The script picks the Alpine build on Alpine.

Pin the version (the action's tag, the image tag, or `TINYPROBE_VERSION`), so a new release does not change your pipeline without you choosing it. Keep credentials in the CI's secrets and write `env.ci.yaml` from them, or pass them with `--var`.

## Explore an API

`inspect` lists what a file defines, and `call` sends a single request and prints the whole response, without writing a workflow:

```bash
tinyprobe inspect docs/example/api/testserver.yaml
tinyprobe call docs/example/api/testserver.yaml status --with code=404 -e docs/environment.yaml
```

```text
  → status code=404  GET    404    64.41ms  /api/status/404

  HTTP 404 Not Found
  ...
```

`call` accepts any status, so a `404` is shown, not reported as a failure: a single call is not a test, so its line starts with `→` and the status, with no ✓ or ✗. A ✗ means that no response arrived (exit code 1). Arguments are validated like a step's `with`: a missing required param or a misspelled name is an error. A call remembers nothing for the next one ([decision 0003](decisions/0003-endpoints-declare-workflows-run.md)).

To look at one step of a flow, the one that needs the token and the ids of the steps before it, run the workflow up to it with `--to`: its number, as `inspect` lists the steps, or its name. The steps before it run again; the steps after it don't run and aren't counted.

```bash
tinyprobe run docs/example/workflows/users.yaml -e docs/environment.yaml --to 3 -v
tinyprobe run docs/example/workflows/users.yaml -e docs/environment.yaml --to "Only active users"
```

In VS Code, **Run to here** above each step does the same and shows that step's response.

To leave a step out for a while, without changing the file, skip it with `--skip`: the step that deletes what you want to look at, a slow `sleep`, or a step against a service that is down today. It takes a number or a name, as `--to` does, and can be repeated. Skipped steps are printed as `– … skipped (--skip)` and are not counted: the run passes when the others pass. A later step that needs a value only a skipped step saves fails, and says which step was skipped.

```bash
tinyprobe run docs/example/workflows/users.yaml -e docs/environment.yaml --skip 2 --skip "Search by name"
```

The next run without `--skip` runs every step, so a step left out never stays out by mistake in CI. tinyprobe-app turns steps off with ⊘ Skip on the diagram and passes them with `--skip`.

A run cut by `--to` can be continued instead of replayed, for tools that go through a flow one step at a time ([spec 0022](specs/0022-resume-a-run.md)). When every step up to the target passed, the `run_finished` event of `--reporter ndjson` has a `state`: the values saved, the workflow `variables` already resolved and the cookies. `--resume` takes it back and starts at the next step, up to `--to` or to the end:

```bash
tinyprobe run docs/example/workflows/users.yaml -e docs/environment.yaml --to "Create a user" --reporter ndjson > run.ndjson
tail -n 1 run.ndjson | jq .state > state.json
tinyprobe run docs/example/workflows/users.yaml -e docs/environment.yaml --resume state.json --to "Patch the user"
```

The state holds tokens: keep it in memory and pass it with `--resume -` (standard input) rather than writing it to a file, and never commit it. tinyprobe refuses a state of another workflow or environment file, and one whose steps changed since it was made (the steps that ran, and the endpoints they call; an edit to a later step is fine). tinyprobe-app continues ▶ Up to here this way.

## Use tinyprobe from other tools

`inspect --json` and `run --reporter ndjson` (also `call`) print JSON for programs: editors, CI dashboards, a local UI. The format is versioned and documented in [protocol.md](protocol.md); the VS Code extension and tinyprobe-app are built on it.

```bash
tinyprobe run docs/example/workflows/users.yaml -e docs/environment.yaml --reporter ndjson
```

`tinyprobe mcp` gives the same commands to coding agents as MCP tools: see [protocol.md](protocol.md#mcp).
