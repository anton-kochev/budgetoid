---
name: budgetoid.publish-backlog
description: "Publish an SRS-derived user-story backlog to the Budgetoid GitHub Project (github.com/users/anton-kochev/projects/1). Archives the SRS in the private anton-kochev/budgetoid-specs repository with a commit-pinned permalink, creates draft items with Status=Backlog, sets Priority by critical path, adds a reference item with the dependency map, and removes local spec files from the repo. Use when asked to publish stories or a backlog to the GitHub project, push an SRS to the board, create project items from user stories, or attach an SRS to stories. Requires an SRS markdown file as input."
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
that **spec files never live in the product repository**: the SRS is archived
in the private `anton-kochev/budgetoid-specs` repository, the stories live on
the board, and local spec files are deleted at the end.

## Prerequisites

- `gh` authenticated as `anton-kochev` **with the `project` scope**. Check
  with `gh auth status`; if the scope is missing, ask the user to run
  `! gh auth refresh -s project` (interactive — never run it yourself) and
  wait for confirmation.
- The SRS markdown file exists on disk — it is the file that gets archived
  in step 1. A URL or in-conversation text is not enough.
- User stories already derived from that SRS. If they aren't yet, run
  `/grimoire.srs-to-user-stories` on the same file first and get the user's
  approval of the stories before publishing anything.

## Workflow

### 1. Archive the SRS in the private specs repository

Specs live in `anton-kochev/budgetoid-specs`, a **private** repository —
not in a gist. A gist marked "secret" is unlisted, not access-controlled:
anyone holding the URL reads it without signing in, and GitHub offers no
private tier for gists. The specs repo gives a real ACL, diffs between
revisions, and issues against the requirements themselves.

Clone it outside the product repo, add the file, commit, push, and take the
commit SHA:

```bash
gh repo clone anton-kochev/budgetoid-specs <scratchpad>/budgetoid-specs
cp <SRS-file>.md <scratchpad>/budgetoid-specs/
# add a row to the specs README table for the new document
git -C <scratchpad>/budgetoid-specs add -A
git -C <scratchpad>/budgetoid-specs commit -m "docs(<scope>): add the <initiative> requirements baseline"
git -C <scratchpad>/budgetoid-specs push origin HEAD
git -C <scratchpad>/budgetoid-specs rev-parse HEAD          # commit SHA
```

Build the **commit-pinned permalink** —
`https://github.com/anton-kochev/budgetoid-specs/blob/<commit-sha>/<SRS-file>.md`
— and use it in every story's `Source:` line. Never link a `blob/main` URL
from a story: the pin is what preserves context after later SRS revisions.

Before deleting anything local (step 5), verify the archived copy is
byte-identical:

```bash
gh api "repos/anton-kochev/budgetoid-specs/contents/<SRS-file>.md?ref=<sha>" --jq .sha
git hash-object <SRS-file>.md      # must match
```

If the user revises the SRS later: commit the new revision and repoint the
pinned link **only in still-open stories**; closed stories keep the revision
they were implemented against.

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
  **Source:** [<SRS title>](<commit-pinned specs-repo permalink>)
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

- link to the document on `main` (current) and to the commit-pinned
  revision the stories were derived from;
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
   the product repo. Everything they contained now lives in `budgetoid-specs`
   (SRS), the items (stories), and the reference item (dependency map).
   Delete only the files this run published — never another initiative's
   unpublished spec.

### 6. Report

Summarize for the user: project URL, specs-repo permalink, item count per
priority, and the critical path.

## Pitfalls (learned the hard way)

- **Never generate items with bash heredocs** — macOS ships bash 3.2, whose
  parser breaks on heredocs inside `$( )` when the content has unbalanced
  apostrophes. Always drive `gh` from Python with list-form `subprocess`
  arguments (the manifest script already does).
- `gh project item-create` needs the `project` OAuth scope; the default
  token has only `repo`.
- Draft items have no edit history — `budgetoid-specs` is the versioned
  record, the board is the working copy. Never treat item bodies as the
  source of truth for requirements.
- **Story numbers are global to the board, not per-initiative.** Epics 1–5
  belong to envelope budgeting, 6–14 to privacy. A new initiative continues
  from the highest epic in use; two `Story 1.1` items make the board
  ambiguous and defeat the title-sort ordering.
- Draft-item **content** ids (`DI_…`) and **item** ids (`PVTI_…`) are
  different node types: `item-edit` takes `PVTI_…`, the draft-issue update
  mutation takes `DI_…`.

## Project constants (verify before trusting)

Current as of the last run — the script re-resolves them dynamically, so
these are for orientation only: project `1`, owner `anton-kochev`, project
id `PVT_kwHOAH-_8s4AsUGm`; Status options Backlog/Ready/In progress/In
review/Done; Priority options P0/P1/P2; Size options XS–XL (left for the
user to fill during grooming — never set Size or Estimate yourself).
