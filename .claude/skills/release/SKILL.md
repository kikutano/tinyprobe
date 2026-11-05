---
name: release
description: Prepare a tinyprobe release (CLI and, if it changed, the VS Code extension): pick the version, write the annotated tag notes, verify, and tag. Use when asked to release, publish or tag a version.
---

# Release tinyprobe

Pushing a tag publishes binaries to every user (`tinyprobe update`, install scripts): confirm the version and the notes with the user before pushing anything.

## CLI (this repository)

1. On an up-to-date `main`, with a clean tree: `scripts/dev.sh check`. If `clang` is available, also `scripts/dev.sh aot` and run one workflow with `publish/tinyprobe`.
2. Version: `git describe --tags --abbrev=0` gives the last one. SemVer: before 1.0, a breaking format or protocol change bumps the minor and needs a "Breaking change" section.
3. Notes from `git log <last-tag>..HEAD --oneline` (merged PRs): a title line `tinyprobe X.Y.Z`, then highlights for users with a short YAML example per new feature, then breaking changes with what users must change. Write them in the scratchpad as `notes.md`.
4. Update the version in the examples of `README.md` and `docs/cli.md` (sample output, the action's tag, the Docker image tag, `TINYPROBE_VERSION`): `grep -n '0\.[0-9]*\.[0-9]' README.md docs/cli.md`.
   Set `version` in `agents/claude/.claude-plugin/plugin.json` to the new version and commit it before tagging: Claude Code updates the plugin when that version changes.
5. Show the notes and the tag command to the user. After approval:
   `git tag -a vX.Y.Z --cleanup=whitespace -F notes.md && git push origin vX.Y.Z`
   (`-beta.N` suffix → pre-release). The Release workflow builds win-x64, linux-x64, linux-arm64, linux-musl-x64, linux-musl-arm64, osx-arm64, publishes checksums, then:
   - job "Docker image": pushes `ghcr.io/kikutano/tinyprobe:X.Y.Z` (plus `X.Y` and `latest` for a stable release) and pulls it without credentials. If that pull fails, the package is private: ask the user to set it to Public in the package settings on GitHub, then re-run the job;
   - job "Test server image": pushes `ghcr.io/kikutano/tinyprobe-testserver` with the same tags (the server of `docs/example`) and pulls it without credentials. Same fix when that pull fails: a new package starts private;
   - job "GitHub Action": runs `action.yml` with the new release on Linux, macOS and Windows.
   - job "MCP Registry" (stable releases only): publishes `server.json` with the release's version and image to registry.modelcontextprotocol.io, logged in with GitHub OIDC. Check the listing: `curl -s 'https://registry.modelcontextprotocol.io/v0.1/servers?search=io.github.kikutano/tinyprobe'`.
   Check both passed, since users pin `kikutano/tinyprobe@vX.Y.Z` and the image tag.

## Extension (`../tinyprobe-vscode`)

Only if it changed since its last tag:

1. `scripts/dev.sh check` there, with the released binary if possible (`TINYPROBE_PATH=$(command -v tinyprobe)` after `tinyprobe update`).
2. Bump `version` in `package.json`, add the `CHANGELOG.md` section; if it needs the new CLI, `MINIMUM_VERSION` in `src/protocol.js` and the README "Requirements".
3. Commit, then the annotated tag the same way. Release the CLI first when the extension depends on it: its CI installs the latest CLI release.

4. The Release workflow publishes the GitHub Release and Open VSX. The VS Code Marketplace is by hand: ask the user to upload the release's `tinyprobe-vscode.vsix` with **Update** on https://marketplace.visualstudio.com/manage, then check the version with the Marketplace API.
