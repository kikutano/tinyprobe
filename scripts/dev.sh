#!/bin/sh
# Development tasks for tinyprobe, the same steps CI runs, on your machine.
#
#   scripts/dev.sh <command>
#
# Commands:
#   build     Build the solution (Debug).
#   test      Run the unit tests.
#   server    Start the test server on http://localhost:5122, in the foreground.
#   e2e       Run every workflow of docs/example and the JSON protocol checks with the
#             development binary, against the test server (started and stopped if needed).
#   example   Run the workflows of example/ against JSONPlaceholder (needs internet).
#   check     build + test + e2e: run it before opening a pull request.
#   install   Publish a development binary to .dev/tinyprobe and link it as tinyprobe-dev
#             in $HOME/.local/bin. The VS Code extension can use it with tinyprobe.path.
#   aot       Publish the Native AOT binary to publish/, like a release (needs clang or gcc, and zlib).
#   perf      aot, then the performance guard of CI (scripts/perf.sh) against the test server.
#
# Environment variables:
#   TINYPROBE_PORT   port of the test server (default: 5122)
set -eu

ROOT=$(cd "$(dirname "$0")/.." && pwd)
PORT="${TINYPROBE_PORT:-5122}"
BASE_URL="http://localhost:$PORT"
DEV_DIR="$ROOT/.dev"
DEV_BIN="$DEV_DIR/tinyprobe/tinyprobe"
SERVER_PROJECT="$ROOT/tests/Tinyprobe.RealServer.Tests"

fail() { echo "dev: $*" >&2; exit 1; }
step() { printf '\n\033[1m==> %s\033[0m\n' "$*"; }

rid() {
  case "$(uname -s)" in
    Linux) os="linux" ;;
    Darwin) os="osx" ;;
    *) fail "unsupported OS '$(uname -s)'." ;;
  esac
  case "$(uname -m)" in
    x86_64|amd64) arch="x64" ;;
    aarch64|arm64) arch="arm64" ;;
    *) fail "unsupported architecture '$(uname -m)'." ;;
  esac
  echo "$os-$arch"
}

build() {
  step "Build"
  dotnet build "$ROOT/Tinyprobe.slnx" -nologo -v quiet
}

unit_tests() {
  step "Unit tests"
  dotnet test "$ROOT/Tinyprobe.slnx" --no-build -nologo -v quiet
}

# A framework-dependent build of the CLI: fast, no native toolchain needed, version 0.0.0-dev.
publish_dev() {
  step "Development binary"
  dotnet publish "$ROOT/src/Tinyprobe.Cli/Tinyprobe.Cli.csproj" -c Release -p:PublishAot=false \
    -o "$DEV_DIR/tinyprobe" -nologo -v quiet
  echo "$DEV_BIN"
}

server_up() { curl -sf -o /dev/null "$BASE_URL/api/public"; }

SERVER_PID=""
start_server() {
  if server_up; then
    echo "Test server already running on $BASE_URL"
    return
  fi
  step "Start test server on $BASE_URL"
  mkdir -p "$DEV_DIR"
  dotnet run --project "$SERVER_PROJECT" --urls "$BASE_URL" > "$DEV_DIR/server.log" 2>&1 &
  SERVER_PID=$!
  trap stop_server EXIT INT TERM
  i=0
  until server_up; do
    i=$((i + 1))
    [ "$i" -le 120 ] || { cat "$DEV_DIR/server.log"; fail "the test server did not start."; }
    sleep 1
  done
}

stop_server() {
  [ -n "$SERVER_PID" ] || return 0
  # dotnet run starts the server as a child process: stop both.
  pkill -P "$SERVER_PID" 2>/dev/null || true
  kill "$SERVER_PID" 2>/dev/null || true
  SERVER_PID=""
}

e2e() {
  command -v jq > /dev/null || fail "jq is needed for the protocol checks."
  publish_dev
  start_server
  cd "$ROOT"

  step "End-to-end (docs/example/workflows)"
  "$DEV_BIN" run docs/example/workflows -e docs/environment.yaml --var "baseUrl=$BASE_URL/" --junit "$DEV_DIR/junit.xml"
  grep -q '<testsuites name="tinyprobe" tests="70" failures="0" errors="0" skipped="0"' "$DEV_DIR/junit.xml" \
    || fail "unexpected JUnit report: $(head -2 "$DEV_DIR/junit.xml")"

  step "End-to-end (inspect, call, ndjson)"
  "$DEV_BIN" inspect docs/example/api/testserver.yaml
  "$DEV_BIN" inspect docs/example/workflows/users.yaml --json \
    | jq -e '.valid and .kind == "workflow" and (.steps | length) > 0' > /dev/null
  # The file as written, for editors (spec 0018): every endpoint is a block, and --source needs --json.
  "$DEV_BIN" inspect docs/example/api/testserver.yaml --json --source \
    | jq -e '(.source.endpoints | length) == ([.blocks[] | select(.kind == "endpoint")] | length) and (.source.auth | has("bearer"))' > /dev/null
  if "$DEV_BIN" inspect docs/example/api/testserver.yaml --source 2>/dev/null; then fail "--source without --json was accepted"; fi
  "$DEV_BIN" call docs/example/api/testserver.yaml status --with code=404 \
    -e docs/environment.yaml --var "baseUrl=$BASE_URL/"
  "$DEV_BIN" run docs/example/workflows/users.yaml -e docs/environment.yaml \
    --var "baseUrl=$BASE_URL/" --reporter ndjson > "$DEV_DIR/events.ndjson"
  jq -e -s '.[0].event == "run_started" and .[-1].event == "run_finished" and .[-1].success' \
    "$DEV_DIR/events.ndjson" > /dev/null
  "$DEV_BIN" run docs/example -e docs/environment.yaml --var "baseUrl=$BASE_URL/" --reporter ndjson \
    > "$DEV_DIR/suite.ndjson"
  jq -e -s '.[0].event == "suite_started" and (.[0].files | length) == 8
    and .[-1].event == "suite_finished" and .[-1].success' "$DEV_DIR/suite.ndjson" > /dev/null
  # Run to here: the steps up to the third, and a success although the workflow has more.
  "$DEV_BIN" run docs/example/workflows/users.yaml -e docs/environment.yaml --var "baseUrl=$BASE_URL/" --to 3 \
    --reporter ndjson > "$DEV_DIR/to.ndjson"
  jq -e -s '.[0].to == 2 and (.[0].steps | length) == 3 and .[-1].success and .[-1].summary.stopped_after == 2' \
    "$DEV_DIR/to.ndjson" > /dev/null
  echo "Protocol checks passed."

  step "End-to-end (schema)"
  "$DEV_BIN" schema | jq -e '."$defs".step.properties.call and (.properties.steps.items."$ref" == "#/$defs/step")' > /dev/null
  echo "Schema checks passed."

  step "End-to-end (mcp)"
  printf '%s\n' \
    '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}' \
    '{"jsonrpc":"2.0","method":"notifications/initialized"}' \
    '{"jsonrpc":"2.0","id":2,"method":"tools/list"}' \
    "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"run\",\"arguments\":{\"paths\":[\"docs/example/workflows\"],\"env_file\":\"docs/environment.yaml\",\"vars\":{\"baseUrl\":\"$BASE_URL/\"}}}}" \
    | "$DEV_BIN" mcp > "$DEV_DIR/mcp.ndjson"
  jq -e -s 'length == 3 and .[0].result.serverInfo.name == "tinyprobe" and (.[1].result.tools | length) == 4
    and (.[2].result.isError | not) and (.[2].result.content[0].text | fromjson | .success and (.workflows | length) == 8)' \
    "$DEV_DIR/mcp.ndjson" > /dev/null
  echo "MCP checks passed."

  step "End-to-end (init)"
  init_dir=$(mktemp -d)
  "$DEV_BIN" init "$init_dir" --no-color > /dev/null
  "$DEV_BIN" init "$init_dir" --no-color | grep -q '= tinyprobe/env.local.yaml'
  test -d "$init_dir/tinyprobe/workflows" && grep -q 'tinyprobe guide' "$init_dir/AGENTS.md" \
    && grep -qx '!tinyprobe/env.example.yaml' "$init_dir/.gitignore"
  rm -rf "$init_dir"
  echo "init checks passed."
}

example() {
  publish_dev
  cd "$ROOT/example"
  step "Example (JSONPlaceholder)"
  for workflow in workflows/*.yaml; do
    "$DEV_BIN" run "$workflow" -e environment.yaml
  done
}

install_dev() {
  publish_dev
  mkdir -p "$HOME/.local/bin"
  ln -sf "$DEV_BIN" "$HOME/.local/bin/tinyprobe-dev"
  echo "Linked $HOME/.local/bin/tinyprobe-dev -> $DEV_BIN"
}

aot() {
  # Releases link with clang; gcc gives a binary of the same size (measured: 16 bytes apart), enough for local checks.
  linker=""
  if ! command -v clang > /dev/null; then
    command -v gcc > /dev/null || fail "Native AOT needs clang (or gcc) and zlib (Debian/Ubuntu: sudo apt-get install clang zlib1g-dev)."
    linker="-p:CppCompilerAndLinker=gcc"
  fi
  step "Native AOT ($(rid))"
  # shellcheck disable=SC2086 # $linker is empty or one option
  dotnet publish "$ROOT/src/Tinyprobe.Cli/Tinyprobe.Cli.csproj" -c Release -r "$(rid)" -o "$ROOT/publish" -nologo $linker
  ls -lh "$ROOT/publish/tinyprobe"
}

perf() {
  aot
  start_server
  step "Performance guard"
  "$ROOT/scripts/perf.sh" "$ROOT/publish/tinyprobe" "$BASE_URL"
}

case "${1:-}" in
  build) build ;;
  test) build && unit_tests ;;
  server) cd "$ROOT" && exec dotnet run --project "$SERVER_PROJECT" --urls "$BASE_URL" ;;
  e2e) e2e ;;
  example) example ;;
  check) build && unit_tests && e2e && step "All checks passed" ;;
  install) install_dev ;;
  aot) aot ;;
  perf) perf ;;
  ""|-h|--help|help) sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//' ;;
  *) fail "unknown command '$1'. See scripts/dev.sh --help." ;;
esac
