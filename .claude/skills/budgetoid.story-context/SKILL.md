---
name: budgetoid.story-context
description: "Retrieve everything an AI coding agent needs to implement a Budgetoid user story from the GitHub Project (github.com/users/anton-kochev/projects/1): the story body, the full text of every requirement it cites from the SRS gist, its dependencies and their board status, and the repo docs that ground it. Use when asked to implement, start, or work on a story (e.g. 'implement Story 4.1', 'take the next backlog item'), or before coding against acceptance criteria that cite FR-/NFR-/IFR-/CON-/ASM- requirement IDs."
user-invocable: true
---

# Story Context

Assembles an **implementation brief** for one user story from the Budgetoid
GitHub Project. A story body alone is not enough to implement from: its
acceptance criteria cite requirement IDs whose full text lives in the SRS
gist, its dependencies may not be done, and the repo has canonical docs the
SRS builds on. Gather all of it *before* writing code.

## Prerequisites

`gh` authenticated as `anton-kochev` with the `project` scope. If
`gh auth status` shows no project scope, ask the user to run
`! gh auth refresh -s project` and wait.

## Workflow

### 1. Locate the story on the board

```bash
gh project item-list 1 --owner anton-kochev --format json --limit 200
```

Match by story number in the title (`Story 4.1: …`). Capture the item's
`Status` and `Priority` field values. Stories are draft items; fetch the
full body via GraphQL (item-list may truncate):

```bash
gh api graphql -f query='query { user(login: "anton-kochev") {
  projectV2(number: 1) { items(first: 50) { nodes {
    content { ... on DraftIssue { id title body } } } } } } }'
```

### 2. Parse the story body

Extract: the **epic** (first line), the **role/goal/benefit** triple, every
**acceptance criterion** with its cited requirement IDs (`FR-`, `NFR-`,
`IFR-`, `CON-`, `ASM-`), the **Dependencies** line, and the **Source** line's
revision-pinned gist permalink
(`https://gist.github.com/anton-kochev/<gist-id>/<revision-sha>`).

### 3. Fetch the SRS from the gist

Use the pinned revision — it is the exact baseline the story was derived
from (open stories are re-pinned when the SRS is revised, so the pin is
current by convention):

```bash
gh api gists/<gist-id>/<revision-sha> --jq '.files[].content'
```

Also check `gh api gists/<gist-id> --jq '.history[0].version'`; if the
latest revision differs from the pin, tell the user the story may need
re-pointing before implementing against it.

### 4. Extract the requirement context

From the SRS, pull the **full text** of:

- every requirement ID the acceptance criteria cite;
- every **CON-** constraint and **ASM-** assumption — they apply globally
  (stack, precision, idempotency, UTC, append-only rules), not just when
  cited;
- the **definitions table** entries for domain terms the story uses
  (available, carryover, to allocate, horizon, …);
- the **endpoint table** (IFR-003) rows for any endpoint the story touches,
  including error codes;
- the **worked example** appendix if the story involves envelope arithmetic
  — it is normative and doubles as test expectations;
- the **acceptance criteria** section (§7.2) if the story is a verification
  story.

### 5. Check dependencies

From the story's `Dependencies:` line, look up each prerequisite story's
`Status` on the board. Also read the `📄 SRS — …` reference item — it holds
the **dependency map** with the critical path. If a prerequisite is not
`Done`, stop and tell the user which stories block this one instead of
implementing on top of missing structure.

### 6. Ground in the repo

The SRS specifies *what*; the repo specifies *how things are done here*:

- `CLAUDE.md` — build/test commands, conventions, workflow (strict TDD:
  the acceptance criteria are the test list).
- `docs/business-logic/` — current-state rules the story modifies; start
  from `_overview.md`, read the files for the affected domain area, and
  remember the same-commit doc-update rule.
- `docs/product/` — the design rationale the SRS traces to (problem.md,
  multi-budget.md, multi-currency.md) when a decision needs its "why".
- The SRS **traceability matrix** row for each requirement names its source
  doc — follow it when the requirement's intent is unclear.

### 7. Produce the implementation brief

Summarize before coding: story + epic + priority; the acceptance criteria
with each cited requirement's full text inlined; global constraints that
apply; endpoint contract and error codes; dependency status; the list of
business-logic docs to update in the same commit. Then implement test-first
against the criteria.

### 8. Keep the board honest

When implementation starts, move the story's `Status` to `In progress`;
when it lands, to `In review` or `Done` (ask the user which). Use
`gh project item-edit --id <PVTI_…> --project-id <project-id> --field-id
<status-field-id> --single-select-option-id <option-id>` — field and option
ids come from `gh project field-list 1 --owner anton-kochev --format json`.

## Pitfalls

- Draft-item **content** ids (`DI_…`) and **item** ids (`PVTI_…`) differ:
  GraphQL draft mutations take `DI_…`, `gh project item-edit` takes `PVTI_…`.
- Never implement from the story body's paraphrase when it disagrees with
  the SRS text — the SRS revision the `Source:` line pins is the contract;
  surface the discrepancy to the user.
- Do not set `Size`/`Estimate` fields — those are the user's grooming calls.
- Never commit SRS or story files into the repo while gathering context —
  specs live in the gist and on the board only.
