# tinyprobe, Postman, Bruno and Hurl

An honest comparison, for choosing the right tool. Each of the four is good at something the others are not, and tinyprobe is the youngest of them. Checked on 2026-11-05 against each project's own site ([sources](#sources)); prices and features change, so check them again before you decide.

## At a glance

| | **tinyprobe** | **Postman** | **Bruno** | **Hurl** |
|---|---|---|---|---|
| What it is | A CLI, with a VS Code extension and a small browser app on the same engine | An API platform: desktop and web client, workspaces in its cloud | A desktop API client that keeps collections as files | A command-line tool that runs requests from plain-text files |
| Where tests live | YAML files in your repository: API files describe endpoints, workflows run them | Collections in Postman workspaces, exported as JSON (Native Git workflows also exist) | A folder of files in your repository: YAML (OpenCollection, the default since 3.1) or the Bru format | `.hurl` text files in your repository |
| Account | None | Yes, for collections and environments; a lightweight client sends single requests signed out | None (an email only to buy a license) | None |
| License and price | GPL-3.0, free | Proprietary. Free for 1 user, Solo $9/month, Team $19/user/month (billed annually) | MIT, free. Pro $6 and Ultimate $11 per user/month for Git providers, workspaces and team features | Apache-2.0, free |
| In CI | One native binary (about 9 MB, no runtime), a Docker image, a GitHub Action, JUnit | Postman CLI or Newman, both npm packages (Node.js) | Bruno CLI (`bru run`), an npm package, or its Docker image | One native binary (Rust, libcurl); JUnit, TAP and HTML reports |
| Logic in a test | None: declarative checks, saved values, generated values, `for_each`, `until` | JavaScript before and after each request | JavaScript before and after each request, Chai assertions | None: declarative asserts and captures, retry |
| A request used in many tests | Declared once as an endpoint, called with arguments | A request per item; variants are copies | A request per file; variants are copies | Written in full in each file |
| Coming from Postman | Your coding agent converts the collection, following `tinyprobe guide`, and lists what it could not convert | — | A built-in importer, with a converter for scripts | No importer |
| Protocols | HTTP with JSON, forms and file uploads | HTTP, GraphQL, gRPC, WebSocket and more | HTTP, GraphQL, gRPC, WebSocket | HTTP, with XML and HTML checks (XPath) as well as JSON |
| A graphical client | VS Code extension, browser app | Yes, the main way to use it | Yes, the main way to use it | No |

## When to choose which

**Postman** if your team works in its workspaces: shared collections, mock servers, monitors, documentation and many protocols in one place, with JavaScript for anything the client does not do. It is the most complete of the four. The price is an account and a cloud workspace, and per-user plans once more than one person works on it.

**Bruno** if you want a Postman-like desktop client without an account, with collections as files in Git. It imports Postman collections and their scripts, and keeps JavaScript for what declarative tests cannot say. Choose it when people explore APIs by clicking, and the tests grow out of that.

**Hurl** if you like requests written as plain HTTP, and you test HTML or XML as well as JSON. It is mature, fast and small, with libcurl underneath, and its files read like the requests they send. Every file spells out its requests: there is no shared description of an endpoint.

**tinyprobe** if you want your coding agent to write and maintain the tests, and the tests to be the same in your terminal, in CI and in review. What makes it different:

- **The migration is done by your AI, and checked.** There is no importer to keep up with Postman's format: the agent converts the collection, validates every file with `tinyprobe inspect`, tries requests with `call`, and runs the workflows until they pass. What it could not convert is written down in `MIGRATION.md`. On our [Postman benchmark](specs/0002-migrate-from-postman.md#scores), Claude Code covered every convertible request and reported every untranslatable one.
- **Endpoints are declared once.** A workflow calls `login` or `get_user` with arguments, like functions, so a changed URL is changed in one place.
- **No scripts, no hidden state.** Every value a step uses is saved by a step above it, and each workflow starts from zero ([decision 0003](decisions/0003-endpoints-declare-workflows-run.md)). What a test does is written in the file, for you and for the agent.
- **Errors before requests.** A whole file is validated first, with `file:line:column` and a "did you mean".

What tinyprobe does not do yet: scripts (a signature computed before a request has no equivalent), XML and HTML checks, gRPC, WebSocket and GraphQL-specific checks (GraphQL over POST works as a JSON body), mock servers and team workspaces. Some of these are planned in the [roadmap](../ROADMAP.md) (M7); a cloud service is not.

## Sources

- Postman: [pricing](https://www.postman.com/pricing/), [Newman and the Postman CLI](https://learning.postman.com/docs/collections/using-newman-cli/command-line-integration-with-newman/), [the lightweight API client](https://learning.postman.com/docs/getting-started/basics/using-api-client).
- Bruno: [what Bruno is](https://docs.usebruno.com/introduction/what-is-bruno), [pricing](https://www.usebruno.com/pricing), [the repository](https://github.com/usebruno/bruno) (license, CLI), [YAML as the default format](https://blog.usebruno.com/making-yaml-the-default-in-bruno-v3.1), [importing collections](https://docs.usebruno.com/get-started/import-export-data/import-collections), [tests with Chai](https://docs.usebruno.com/testing/tests/introduction), [the API client](https://www.usebruno.com/product/api-client) (protocols).
- Hurl: [hurl.dev](https://hurl.dev/), [the repository](https://github.com/Orange-OpenSource/hurl) (license, options).

A mistake in this page? Open an issue or a pull request: it should stay fair to every tool.
