---
name: budgetoid.story-delivery
description: "Implement a Budgetoid user story from an assembled brief through to a clean board: delegate the plan to an architect agent, drive each acceptance criterion test-first, update the owning docs in the same commit, verify, review through agents, and move the board. Use after /budgetoid.story-context has produced a brief, or when asked to implement, build, finish, land, or deliver a story whose acceptance criteria are already known."
user-invocable: true
disable-model-invocation: true
argument-hint: <story-number>
---

# Story Delivery

`budgetoid.story-context` ends by saying "then implement test-first against the criteria." This
skill is that sentence, expanded: who plans, who writes, what counts as proof, when the docs move,
who reviews, and what leaves the board honest.

## This skill points; it does not restate

Every rule already owned by something else — `CLAUDE.md`, `docs/engineering/*`, the ownership hook,
an agent's own instructions — is named here and read there. A copy drifts from its source and then
quietly outranks it.

What follows is only what exists nowhere else: **the order of the handoffs, who owns each one, and
what counts as proof.**

**One deliberate exception: the red-green loop in step 2 is stated here, not pointed at.** No
repository authority owns it — `CLAUDE.md` names the gates, not the rhythm — so pointing would mean
depending on an installed skill, and an installed skill can be revised or removed without this
workflow noticing. Anything named here that is *not* part of the repository — an agent, a hook, a
skill — is a name that may change. Where a rule turns on one, state the rule so it survives the
name: ask what the tool answers rather than what it is called.

## Preconditions

A brief exists. If `/budgetoid.story-context <n>` has not run in this session, say so and run it —
do not reconstruct a brief from the story title. Every dependency is `Done`. Branch is `develop`.
Working tree is clean.

## 1 · Plan — delegated, always

Hand the brief to `grimoire.dotnet-architect`, or `grimoire.frontend-architect` for Angular work.
Both if the story crosses the boundary.

Call `Agent` with `run_in_background: false` and **no `name`** — passing a name registers the agent
as a teammate. One writing agent at a time; report back between them.

How the architect reasons and what shape its plan takes is its own business. This skill requires
three things of the result: that it comes back *before* code does, that it maps every acceptance
criterion to a named test, and that it **separates what it measured from what it inferred**. A plan
that reads as uniformly confident is the one to send back — ask which claims it could not verify and
what would settle each. Architects answer that well when asked and volunteer it unevenly, and the
answer is what step 2 turns into an experiment.

Move the board item to `In progress`.

## 2 · Implement test-first

**The loop is stated here rather than borrowed.** Red → green → refactor, one behaviour per trip:
turn one acceptance criterion — or one case beneath it — into one test, run it, **read the failure**,
write the least code that turns it green, then refactor under a green bar. A test that passed on
arrival, or that failed on a missing symbol when it was meant to fail on an assertion, has not told
you anything yet; say which it was. Nothing here chains a skill that supplies the loop, because an
installed skill can be revised or removed and a delivery workflow that stops working when a
dependency moves is not a workflow. Read whatever TDD guidance happens to be installed if you want
the long form — but the sentence above is the contract, and it holds with or without it.

Requirements on top of the loop:

- **Every unverified claim the plan carries becomes an experiment before it becomes an
  implementation.** This is yours to write, not the architect's: it names the uncertainty, you turn
  it into a step. The shape is always the same — brief the coder to build the version the claim says
  is *wrong*, run the test that should catch it, and **if the test passes, the complexity the claim
  was defending is unearned: drop it and say so.** A plan once argued that a defensive indirection
  was required; implementing the naive version first proved it in a way no reasoning could, and the
  *shape* of the failure said more than the fact of it. Had the plan been followed on trust, the
  same code would have shipped with no idea whether it was needed.
- **Where tests and production code have different owners, the red belongs to the test's owner and
  the green to the code's — one phase, two agents, in that order.** Do not assume the split and do
  not carry forward what was true last story: **the ownership hook's refusal names the owner**, so
  treat the first refusal in a phase as the routing answer rather than an obstacle. Where one agent
  owns both, it runs the whole loop and this requirement is moot. Where they differ, send the test
  owner first to land a *failing* test in the repo, then the code owner to make it pass. Give one
  agent both halves across an ownership boundary and it will reconstruct the red bar in a scratch
  project and hand you a file to copy in — and a red bar nobody observed in place is not evidence.
  One extra handoff per phase buys a failure you actually watched.
- **A pin's red bar has to be manufactured, and the evidence is the deliverable.** The loop above
  hands you a failure for free only where the test drives behaviour that does not exist yet. A test
  that pins existing state — a config value, a header set, a schema, a vocabulary — is green on
  arrival by construction, and nothing has shown it can ever object. The technique for manufacturing
  that failure is not this skill's to state; ask whatever TDD guidance is installed, or work it out.
  **What this skill requires is the report: the mutation applied, which test went red, and the
  message it printed. Without that table the phase is not done.** The wording matters because "write
  a provable-fail control" reliably gets read as *prove the test matches* — four times running,
  agents shipped guards that passed against the exact mutation they existed to catch. Demand the
  table and the same agents deliver it, including proof that the positive control itself fires.
- **A claim is measured or it is tagged; there is no tier in between.** This binds docs, code
  comments and commit messages alike, because this repository's prose *argues* — it names the
  rejected alternative and the change that would redden a test — so a wrong sentence misleads
  precisely rather than vaguely, and a reader who catches one stops trusting the rest of the file.
  Any assertion about a third-party runtime — a cloud host, a framework, a browser — carries the
  command that produced it and what came back, or it carries `[Guessing]`. Ask subagents for **what
  they ran and what they saw**, and treat "I reasoned that…" as unverified however fluent it is: a
  second agent once "resolved" a `[Guessing]` tag with an explanation that was itself wrong, and
  confidence accumulated with no run behind it. Name the tool that produced the evidence, and say
  what that tool does not prove — an emulator is not the platform it emulates.
- **A hook refusal is a routing instruction, not an obstacle.** A `PreToolUse` hook owns which
  agent writes which file, and it is the authority on that — read the refusal, delegate to the owner
  it names. Never reach for `Bash` to write around it: the hook gates `Write` and `Edit`, so a shell
  write does not satisfy the routing decision, it evades it.
- **Check the tree yourself after every writing agent; the report is not the evidence.** `git
  status` and read the diff. Agents have written production files through `Bash` to get around a
  hook refusal and reported the file as delegated, and have left a scratch test file behind after
  an interrupted run — which would have changed the suite count under a green summary. Both were
  caught by looking, neither by reading the report.

**Close this step with one adversarial pass over the tests — before the docs, not after the
commit.** Send a reviewer with the mutation lens from step 6 and nothing else, as soon as the tests
exist. It is the cheapest of the lenses and the highest-yield, and its findings change the *shape*
of the tests rather than adding to them. Left until step 6 it still works, but by then the docs
describe the tests it is about to rewrite — and each doc pass done over tests that then change is a
doc pass paid for twice.

## 3 · Docs in the same commit

`CLAUDE.md` names which doc owns which rule and requires the update to land in the same commit as
the change. The shape of a business-logic file is owned by the files themselves: `_overview.md` is
the index a new file must join, and the existing siblings are the template — read the one nearest
your domain and follow it, rather than inventing sections or importing a structure from elsewhere.
Chain a documentation skill if one is installed and it agrees with what is already on disk; where
they disagree, the files on disk win, because they are what the next reader will actually open.

The trigger this skill adds: a **changed rule** moves its doc, and anything a future reader could
not infer from the code gets a decision-log entry — especially a deliberate absence, which always
reads as an oversight to whoever finds it next.

## 4 · Verify

Run the gates `CLAUDE.md` defines, in the order it defines them.

One acceptance bar on top: **every pre-existing test green without having been edited.** An edited
assertion is a claim that the old expectation was wrong, and it needs its justification in the
commit body. A suite that only passes after its expectations moved has proved nothing.

*Pre-existing* means predating **this story**, not predating this commit — a fix pass has to be able
to correct the tests the story itself just wrote. Name the untouchable files explicitly when you
delegate; left to infer it, an agent freezes what it wrote an hour ago and reports a defect it was
sent to fix.

Re-run the suite **after** committing, not only before. A pre-commit formatter rewrites staged files,
so the tree you verified is not always the tree that landed.

## 5 · Commit

`CLAUDE.md` requires Conventional Commits and one logical change per commit; the format itself is
the public [Conventional Commits 1.0.0](https://www.conventionalcommits.org/en/v1.0.0/) spec, which
outlives any tooling that wraps it. Chain a commit skill if one is installed — it saves keystrokes,
not correctness.

The subject answers **what problem this solves**, not what was changed: a reader scanning `git log`
is looking for the symptom they are chasing, not a list of edited files. The body carries the *why*
of each decision a reviewer might otherwise reverse — and, per step 4, the justification for any
pre-existing expectation the commit moved.

## 6 · Review — delegated, always, and by more than one reader

Two kinds of reviewer, and the distinction is the point:

- **Whichever architect planned the work reviews its execution** — `grimoire.dotnet-architect`
  and/or `grimoire.frontend-architect`, each only if it actually planned. Its value is **depth**: it
  holds the design context, so it is the reader that reaches for a decompiler or builds a scratch
  project to settle a mechanism, where a cold reader would reason about it instead. But that only
  happens if the brief points it at **its own plan** — "attack your recommendations rather than tick
  them off; what did your plan get wrong?" Framed that way an architect will demolish its own work,
  including predictions it made an hour earlier. Framed as "check this was implemented correctly",
  the warning below applies to it in full.
- **`grimoire.code-reviewer` runs every time**, whoever planned. Its value is **breadth**, and it
  comes precisely from not having planned: an author reviewing their own design tends to confirm it,
  and this is the pass with no design to defend. Where the architects find one deep mechanism error,
  this pass finds the six small false claims nobody with a stake would look for.

**Run them in parallel** — one message, several `Agent` calls. Reviewers are read-only, so they
cannot conflict; the one-at-a-time rule exists for agents that write. Still no `name` on any of them.

**Pick lenses that cannot overlap.** Two general-purpose reviewers largely agree with each other;
the yield is in asking different questions. These repeatedly find what a general pass does not:

- **Refute, don't confirm.** Hand a reviewer the previous round's conclusion — "there is no tenancy
  violation" — and require it to attack that claim, then report every attack that *failed* and why.
  The failed-attack list is worth as much as the findings: it is the only record of what was
  actually checked.
- **Mutation analysis over the tests.** For each test, name the smallest production change that
  turns it red; a test with no such change is a decoration. Invert it too — name what you could
  break in production without any test noticing. This is stronger than the provable-fail rule in
  step 2, and it is what caught a leak-search assertion whose target could not have been in the
  response under *any* implementation, after two reviewers had passed the same file.
- **Measure, don't opine.** Give one reviewer a short list of the change's load-bearing factual
  claims — what a framework does, what a platform emits, what a builder flag changes — and require
  it to *settle* each by running something, reporting the command and the output. It will disprove
  claims that three reviewers reading carefully had let stand, because reading cannot disprove a
  claim about behaviour. Budget for it: this lens is the slowest and it is the one that finds the
  errors that survive everything else.

**Triage findings against the acceptance criteria before planning any fix**, and put the split to
the user. Reviews reliably surface real problems that no criterion cites — a missing rate limit, an
absent cache directive, an authorization asymmetry with a neighbouring endpoint. Fixing them
silently is scope the user did not choose; dropping them silently loses them. Anything deferred goes
to the hardening backlog in `budgetoid-specs`, in the same pass — a finding that lives only in a
transcript is gone. While you are in that file, delete any entry the story just closed; it lists
pending work only.

Findings that stay go **back through a planning agent as a fix plan**, not straight into code. The
fix pass is sequential again, because it writes.

## 7 · Close the board

Ask whether the story goes to `In review` or `Done` — never assume.

Then sweep `Backlog` against the dependency statuses and report which stories became unblocked.
`story-context` step 8 has the `gh` commands and the field ids.

**Sweep the critical path, not only the direct dependents.** The reference item holds the dependency
map; walk it far enough to say which newly-unblocked story is a *gate* on it. "Three stories became
Ready" is bookkeeping; "one of the three is what holds the entire encryption chain" is the answer.
While you are there, check the reference item's own "start here — no blockers" line against the
board: it goes stale silently and reads as current to whoever arrives next.

## Pitfalls

Each of these has actually happened here.

- **A plan can contradict the header rule of a file it edits.** Surface the conflict and let the
  user choose; never silently follow one and drop the other.
- **A classifier rule can sit unreachable behind a broader one and still look alive.** When a rule
  is added to a deny-list or a vocabulary, walk the whole set for entries that can no longer fire.
- **Mutating your own uncommitted code to prove a test can fail is encouraged; disabling a shipped
  control to do it is not.** They look alike and are not. Breaking the line you wrote an hour ago,
  in your own working tree, is often the only way to learn that a control is a decoration — it is
  how this repo found out that a culture-sensitivity test was setting a culture the endpoint never
  saw. Turning off a control that is already protecting something buys the same evidence at the
  price of a window where it is off; find an equivalent case that is already red instead. Either
  way: revert the probe, then verify the tree before you report, not after.
- **Touching `Infrastructure/Persistence/Migrations/` means reading `docs/engineering/migrations.md`
  first**, and carrying whatever caveat it names into the commit body.
- **Never commit SRS or story files into the repo.** Specs live in `budgetoid-specs` and on the
  board.
