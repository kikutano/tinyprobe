# 0020 — `for_each` over a sequence

- **Status:** Implemented
- **Roadmap:** Inbox (`cli`): `for_each` over a sequence
- **Repositories:** tinyprobe
- **PRs:** [#40](https://github.com/kikutano/tinyprobe/pull/40)

## Problem

`for_each` ([spec 0011](0011-for-each.md)) repeats one call per item, and a sequence ([spec 0012](0012-reusable-sequences.md)) bundles several steps behind one `use`. Together they would cover a common test: for each case, create something, read it back, delete it. Today a `use` step cannot have `for_each`, so a workflow writes the `use` once per case:

```yaml
steps:
  - use: ../sequences/user-lifecycle.yaml
    with: { role: admin }
  - use: ../sequences/user-lifecycle.yaml
    with: { role: editor }
  - use: ../sequences/user-lifecycle.yaml
    with: { role: viewer }
```

A table of ten cases is ten copies, and the ids saved by one case have to be kept apart by hand. An agent migrating a Postman collection that loops a folder over a data file has nothing to map it to.

## Proposal

A `use` step takes `for_each`, as a call step does: a list of items or a data file. **The sequence runs once per item, and each item's keys are params of the sequence**, like `with`.

```yaml
# sequences/user-lifecycle.yaml
name: User lifecycle
import:
  - ../api/testserver.yaml
params:
  role:
sequence:
  - name: Create
    call: create_user
    with: { firstName: Test, lastName: "${role}", email: "${role}-${$uuid}@example.com", role: "${role}" }
    expect: { status: 201 }
    save: { userId: $.id }
  - name: Read
    call: get_user
    with: { id: "${userId}" }
    expect:
      body:
        $.role: ${role}
  - name: Delete
    call: delete_user
    with: { id: "${userId}" }
    expect: { status: 204 }
```

```yaml
# workflows/roles.yaml
steps:
  - use: ../sequences/user-lifecycle.yaml
    for_each:
      - { role: admin }
      - { role: editor }
      - { role: viewer }
```

```text
  ✓ User lifecycle [1/3] › Create  POST   201    5.21ms  /api/users
  ✓ User lifecycle [1/3] › Read    GET    200    1.02ms  /api/users/12
  ✓ User lifecycle [1/3] › Delete  DELETE 204    0.98ms  /api/users/12
  ✓ User lifecycle [2/3] › Create  POST   201    1.40ms  /api/users
  ...
```

Rules:

- **Items.** A written list of mappings, or a data file `{ file: path }` (`.csv` or `.json`, relative to the workflow), as in spec 0011. Each item's keys are params of the sequence; `with` holds what is the same for every item, and a key in both is an error, as for a call.
- **Checked when the workflow loads**, like `with`: an unknown param is an error with a suggestion, and a required param must be in `with` or in every item.
- **Running.** The sequence's steps run once per item, in order: all the steps of item 1, then all the steps of item 2. It is the same as writing the `use` once per item. **The workflow stops at the first failing step**, as everywhere else in a workflow: the items after it don't run.
- **Saved values.** A step of the sequence may `save` (a `for_each` call step may not): each item saves its own `userId` and reads it in its next steps. After the last item, the values saved by the last item stay visible to the workflow's next steps.
- **Names.** The steps of each item are named `<sequence name> [2/3] › <step name>` in the console, the JUnit report and the events. A `name` on the `use` step replaces the sequence's name, as today.
- **Nested sequences** work: a sequence used by a sequence with `for_each` repeats with it.
- **Run to here** (`--to 4`) counts the steps of every item, as `inspect` lists them.

## Validation

```text
workflows/roles.yaml:5:44: item 2 of for_each: unknown param 'rol' for sequence 'User lifecycle' (did you mean 'role'?)
workflows/roles.yaml:4:5: missing required param 'role' of sequence 'User lifecycle' in item 3 of for_each (defined at sequences/user-lifecycle.yaml:4:3)
workflows/roles.yaml:5:15: for_each of a use step takes a list of items or { file: path }: the items of a variable are known only when the step runs
```

An unknown param points to its value in the item, and is reported once, at the first item that has it (a CSV column would otherwise be reported on every line). The errors for a key in both `with` and an item, a bad item and an unreadable file are the ones of spec 0011.

## Protocol

New keys only. In `inspect --json`, the `sequence` object of a step that comes from a `use` with `for_each` gets `item` (1-based) and `items` (the count):

```json
"sequence": { "name": "User lifecycle", "file": "/…/sequences/user-lifecycle.yaml", "item": 2, "items": 3 }
```

Without `for_each` the two keys are absent, as today. The run events have no `sequence` object: there the step's `name` carries `[2/3]`.

## Extension

Nothing needed: the extension lists and runs the steps of every item, as `inspect` and the events give them. Its copy of the diagram groups consecutive steps of the same sequence; the items could be shown as separate groups using `item`, in a later change of tinyprobe-app's shared `flow.js`. The app's step form keeps `for_each` of a `use` step in its YAML box, with no change.

## Docs

- `docs/workflow-format.md`: in "Reusable sequences", `for_each` on `use`; remove `for_each` from "A `use` step has no ...".
- `docs/llm-guide.md`: when to use it (one case = several steps: create, read, delete), and the common mistake of a variable (`for_each: ${users}`) on a `use` step.
- `README.md`: the `use` row of the table mentions `for_each`.
- `docs/protocol.md`: `item` and `items` of `sequence`.
- `docs/example`: a workflow of the test server that runs a create/read/delete sequence over a few roles, cleaning up after itself.

## Out of scope

- **A variable as the items** (`for_each: ${users}`) of a `use` step. The steps of a sequence are laid out when the workflow loads, and a variable's items are known only when the step runs; supporting it means repeating a block of steps in the engine, with new events. It can come later on its own spec, if needed.
- Running every item after a failing one, as a `for_each` call does.

## Acceptance criteria

- [x] A `use` step with a list or a data file runs the sequence once per item, with each item's keys as params, and stops at the first failing step.
- [x] Each item's steps can save and read their own values.
- [x] Every error of "Validation", with its location and suggestion.
- [x] `[i/n]` in the step names; `item` and `items` in `inspect --json`.
- [x] `--to` counts the steps of every item.
- [x] A `docs/example` workflow exercises it in CI.
- [x] Docs updated; `scripts/dev.sh check` passes.

## Decisions

Approved on 2026-11-05:

- **A variable as the items** is left out: a `use` step takes a written list or `{ file: path }`, and a variable gets the error above.
- **After a failing step** the workflow stops, as everywhere else.
- **Names** are `User lifecycle [2/3] › Create`.
