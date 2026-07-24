---
name: budgetoid.publish-backlog
description: "Publish an SRS-derived user-story backlog to the Budgetoid GitHub Project (github.com/users/anton-kochev/projects/1). Archives the SRS as a secret gist with a revision-pinned permalink, creates draft items with Status=Backlog, sets Priority by critical path, adds a reference item with the dependency map, and removes local spec files from the repo. Use when asked to publish stories or a backlog to the GitHub project, push an SRS to the board, create project items from user stories, or attach an SRS to stories. Requires an SRS markdown file as input."
user-invocable: true
disable-model-invocation: true
---

# Publish Backlog

> **User-executable skill** — invoke with `/budgetoid.publish-backlog <SRS-file>.md`.
> **Required input: an SRS markdown file** — passed as the argument (e.g.
> `@SRS-feature.md`) or named in the request. If no SRS file is provided,
> stop and ask for one before doing anything; do not publish from
> conversation context alone.

Takes a requirements document and its user stories and publishes them as a
groomed backlog on the Budgetoid GitHub Project, following the repo's rule
that **spec files never live in the repository**: the SRS is archived in a
secret gist, the stories live on the board, and local spec files are deleted
at the end.

## Prerequisites

- `gh` authenticated as `anton-kochev` **with the `project` scope**. Check
  with `gh auth status`; if the scope is missing, ask the user to run
  `! gh auth refresh -s project` (interactive — never run it yourself) and
  wait for confirmation.
- The SRS markdown file exists on disk — it is the file that gets archived
  to the gist in step 1. A gist URL or in-conversation text is not enough.
- User stories already derived from that SRS. If they aren't yet, run
  `/grimoire.srs-to-user-stories` on the same file first and get the user's
  approval of the stories before publishing anything.

## Workflow

### 1. Archive the SRS as a secret gist

```bash
gh gist create <SRS-file>.md --desc "Budgetoid — <initiative name>: SRS"
gh api gists/<gist-id> --jq '.history[0].version'   # revision SHA
```

Build the **revision-pinned permalink** —
`https://gist.github.com/anton-kochev/<gist-id>/<revision-sha>` — and use it
in every story's `Source:` line. Never link an unpinned gist URL from a
story: the pin is what preserves context after later SRS revisions.

If the user revises the SRS later: update the gist (new revision) and
repoint the pinned link **only in still-open stories**; closed stories keep
the revision they were implemented against.

### 2. Build the story manifest

Write a JSON manifest to the scratchpad:

```json
{
  "stories": [
    {
      "title": "Story 1.1: <short title>",
      "priority": "P0",
      "body": "<markdown body>"
    }
  ]
}
```

Conventions:

- **Title**: `Story <epic>.<n>: <short title>` — the number encodes the
  epic, so title sort gives backlog order.
- **Body** template (markdown):

  ```markdown
  **Epic <n>: <Epic Name>**

  **As a** <role>,
  **I want** <goal>,
  **So that** <benefit>.

  ### Acceptance Criteria
  - [ ] <criterion> (<requirement IDs, e.g. FR-041, NFR-001>)

  **Dependencies:** <Story X.Y | None>
  **Source:** [<SRS title>](<revision-pinned gist permalink>)
  ```

- **Priority**: `P0` = on the critical path of the dependency map,
  `P1` = off-path branches, `P2` = optional/deferred. Note in the summary
  that P1 items feeding P0 work (e.g. balances feeding to-allocate) run in
  parallel, not after.

### 3. Publish the items

```bash
python3 .claude/skills/budgetoid.publish-backlog/scripts/publish_stories.py \
  --manifest <scratchpad>/stories.json
```

The script resolves the project and field IDs dynamically, creates each
story as a **draft item**, sets `Status=Backlog` and the given `Priority`,
and **skips titles that already exist** — safe to re-run after a partial
failure.

### 4. Create the reference item

One draft item per initiative, titled `📄 SRS — <initiative name>`, with
**no Status** (it is not work). Body contains:

- link to the gist (current) and to the pinned revision the stories were
  derived from;
- a note that story acceptance criteria cite requirement IDs from it;
- the **Dependency Map** section from the user-stories document (critical
  path first, then side branches) — this is where it lives, since the
  stories file gets deleted.

Create it with `gh project item-create`; to update it later, use the
GraphQL `updateProjectV2DraftIssue` mutation (get the `DI_…` content id via
a GraphQL items query — `gh project item-list` only returns `PVTI_…` item
ids).

### 5. Verify, then clean up

1. Verify every published body is complete (GraphQL query on
   `... on DraftIssue { title body }`; check each ends with the `Source:`
   link).
2. Delete the local SRS and user-stories files — spec files never live in
   the repo. Everything they contained now lives in the gist (SRS), the
   items (stories), and the reference item (dependency map).

### 6. Report

Summarize for the user: project URL, gist URL, item count per priority, and
the critical path.

## Pitfalls (learned the hard way)

- **Never generate items with bash heredocs** — macOS ships bash 3.2, whose
  parser breaks on heredocs inside `$( )` when the content has unbalanced
  apostrophes. Always drive `gh` from Python with list-form `subprocess`
  arguments (the manifest script already does).
- `gh project item-create` needs the `project` OAuth scope; the default
  token has only `repo`.
- Draft items have no edit history — the gist is the versioned record, the
  board is the working copy. Never treat item bodies as the source of truth
  for requirements.
- Draft-item **content** ids (`DI_…`) and **item** ids (`PVTI_…`) are
  different node types: `item-edit` takes `PVTI_…`, the draft-issue update
  mutation takes `DI_…`.

## Project constants (verify before trusting)

Current as of the last run — the script re-resolves them dynamically, so
these are for orientation only: project `1`, owner `anton-kochev`, project
id `PVT_kwHOAH-_8s4AsUGm`; Status options Backlog/Ready/In progress/In
review/Done; Priority options P0/P1/P2; Size options XS–XL (left for the
user to fill during grooming — never set Size or Estimate yourself).
