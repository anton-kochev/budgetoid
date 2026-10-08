// How a write ended, as a word a screen can render — and the one place this
// client reads a refusal out of an API answer.
//
// **`docs/design/components.md`, "A write that does not happen", is the
// authority for every word below.** That chapter's state table is the
// specification; this file is its wire half, and the copy is the screen's. What
// is written here is why the set is the size it is, and why each member is not
// one of its neighbours.
//
// **The routing is over the problem document and never over the status code.**
// A duplicate name is a **400** with an `errors` map on accounts, categories
// and category groups, and a **409** with `conflictKind: duplicate_name` on the
// payee create; a retried create is a **409** with
// `conflictKind: duplicate_identifier`. Keying on the status passes on three
// resources and silently fails on the payee create — the path a person hits
// most, inside the transaction write — which is the defect this file exists to
// close. So the kind is read first, the `errors` map second, and the status is
// consulted for exactly one thing: whether an answer this client could not use
// came from a server that **failed** or from one that **judged**.
//
// **`conflictKind` is a wire contract and its tokens are read, never invented.**
// `docs/business-logic/payees.md` owns the vocabulary and
// `Domain.Common.ConflictKindSpelling` writes it out on the other side. The two
// tokens this client has a branch for are the two constants below, and they are
// the only place either string is spelled in `src/` — every other module asks
// for the **word**, so a third token added on the server reaches this file or
// it reaches nothing.
//
// **The client's words are not the wire's, deliberately.** `duplicate-name` is
// not `duplicate_name`: one is a state this client renders and the other is a
// token a server sends, and spelling them alike is how a later reader comes to
// believe that any new token may be pasted in as a state. The set of states is
// decided by how many *next steps* a person has; the set of tokens is decided
// by the API.
//
// **Seven words, because the chapter's table has seven answers a screen acts
// on differently.** The two the wire tells apart (`duplicate-name`,
// `duplicate-identifier`) carry opposite remedies — adopt the row that exists,
// versus the row you meant is already saved — and were the same response until
// the kind shipped. `unreachable` and `unreadable` are the collapse the chapter
// names outright: a 5xx or a dead connection is the server failing to answer,
// and a minute is a real remedy; a 400 with no usable map or a 409 with no kind
// is the server *judging*, where a minute changes nothing and the sentence
// promises no retry. `invalid` is the only outcome that touches a field.
// `locked` is a write that never left the browser. `recorded` is the only word
// that permits a form to be cleared.
//
// **`invalid` is one outcome even though it renders in two places.** The
// chapter is explicit: a map some of whose keys the form can place puts a
// message under each of those controls *and* a line in the region for every key
// it cannot, "which is one answer to one write rather than two". Which keys a
// form can place is a fact about the form, so the split is the component's and
// the map arrives whole.
//
// **The map is a `Map` before it leaves this file, and that is the chapter's
// rule kept at the source.** A body parsed out of JSON is an ordinary object,
// so `errors['constructor']` hits `Object.prototype` and places an entry under
// a control that does not exist. `Object.entries` is own-enumerable-only and a
// `Map` has no prototype chain to hit, so the hazard is gone before any caller
// can meet it rather than being a rule every caller has to remember.
import { HttpErrorResponse } from '@angular/common/http';

// The two `conflictKind` tokens this client has a branch for. Written out, and
// written out **here only** — see the head of this file.
const DUPLICATE_NAME_KIND = 'duplicate_name';
const DUPLICATE_IDENTIFIER_KIND = 'duplicate_identifier';

/**
 * How one write ended.
 *
 * The states are `docs/design/components.md`, "A write that does not happen".
 * Two are never collapsed because they arrive on the same status, and none is
 * collapsed because a screen does not render it yet.
 */
export type WriteOutcome =
  /** The server has the row. The one word that permits a form to be cleared. */
  | { readonly state: 'recorded' }
  /**
   * The server judged what was typed and keyed its sentences to members.
   *
   * Every entry is renderable: an entry with no message in it is not a
   * message, and a map with no renderable entry left is not this state at all.
   */
  | {
      readonly state: 'invalid';
      readonly errors: ReadonlyMap<string, readonly string[]>;
    }
  /**
   * `conflictKind: duplicate_name` — the payee create, and nowhere else in the
   * product. The remedy is to adopt the row that already exists, which is not
   * a field anybody can correct.
   */
  | { readonly state: 'duplicate-name' }
  /**
   * `conflictKind: duplicate_identifier` — a create carrying an id the budget
   * already holds. The remedy is to reload, never to press again: the same
   * press sends the same id and collects the same answer.
   */
  | { readonly state: 'duplicate-identifier' }
  /**
   * Nothing answered, or the server failed rather than judged. The only state
   * whose remedy is to wait, and it may not claim nothing was written.
   */
  | { readonly state: 'unreachable' }
  /**
   * An answer this client cannot read: a 400 with no usable map, a 409 with no
   * kind or one nobody here knows, any other judgement, or a 2xx whose body
   * this client failed to make sense of. The remedy is a reload, not a retry.
   */
  | { readonly state: 'unreadable' }
  /**
   * The account's keys went away before anything could be sealed or keyed, so
   * no request was made.
   *
   * It is its own word rather than a shade of `unreadable` because nothing was
   * judged and nothing was sent: the next step is to present a factor, which
   * is true of no other state here. The chapter's table gives it no sentence,
   * and correctly — the screen's locked notice is already the account of it,
   * and a second sentence would be the duplicate the region refuses.
   */
  | { readonly state: 'locked' };

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

// The body of a refusal, when there is one to read. A network failure carries a
// `ProgressEvent` and a plain-text 500 carries a string; neither is a problem
// document and both fall through to the status.
function problemDocumentOf(
  error: HttpErrorResponse,
): Record<string, unknown> | null {
  const body: unknown = error.error;

  return isRecord(body) ? body : null;
}

// The kind, as one of this client's two words. `null` for a document naming no
// kind **and** for one naming a kind this client has never heard of — the
// chapter puts both on the unreadable sentence rather than on a guess.
function conflictOf(document: Record<string, unknown>): WriteOutcome | null {
  const kind: unknown = document['conflictKind'];

  if (kind === DUPLICATE_NAME_KIND) {
    return { state: 'duplicate-name' };
  }

  if (kind === DUPLICATE_IDENTIFIER_KIND) {
    return { state: 'duplicate-identifier' };
  }

  return null;
}

// The renderable half of an `errors` map, or `null` when nothing in it can be
// rendered.
//
// **An entry with no message in it is dropped rather than rendered**, because
// an empty `mat-error` is a red border with no words in it — colour as the
// message, which the design book refuses in as many words. Dropping the entry
// and keeping its neighbours is the reading that renders every sentence the
// server actually sent; a map left holding none of them is not a validation
// answer at all and its caller falls to the unreadable sentence.
//
// The messages are handed on **verbatim**. `trim` decides only whether a string
// is a message; nothing here rewrites one, because the copy is the server's and
// a client that edited it would be the second definition the chapter forbids.
function validationErrorsOf(
  document: Record<string, unknown>,
): ReadonlyMap<string, readonly string[]> | null {
  const errors: unknown = document['errors'];

  if (!isRecord(errors)) {
    return null;
  }

  // Insertion order both sides: `Object.entries` and `Map` keep it, and the
  // chapter renders unplaceable entries "in the order the map sends them".
  const placed = new Map<string, readonly string[]>();

  for (const [key, value] of Object.entries(errors)) {
    if (!Array.isArray(value)) {
      continue;
    }

    const messages = value.filter(
      (message: unknown): message is string =>
        typeof message === 'string' && message.trim() !== '',
    );

    if (messages.length > 0) {
      placed.set(key, messages);
    }
  }

  return placed.size === 0 ? null : placed;
}

/**
 * Reads how a write ended out of whatever the write threw.
 *
 * It answers no `recorded` and no `locked`: the first is the caller's own
 * knowledge that a request succeeded, and the second is a refusal taken before
 * any request existed. Everything else a write can end as is decided here, from
 * the problem document first and the status only where the document said
 * nothing this client can act on.
 *
 * @param error What the write threw — an `HttpErrorResponse`, or anything else.
 */
export function writeOutcomeOf(error: unknown): WriteOutcome {
  // Not an HTTP answer at all: a `NarrativeFieldMisuseError`, a `TypeError`
  // from a mapper, a body that would not open. The request may well have
  // landed, so this is the state whose sentence offers a reload rather than a
  // retry — the row, if it exists, is what a reload would show.
  if (!(error instanceof HttpErrorResponse)) {
    return { state: 'unreadable' };
  }

  const document = problemDocumentOf(error);

  if (document !== null) {
    const conflict = conflictOf(document);

    if (conflict !== null) {
      return conflict;
    }

    const errors = validationErrorsOf(document);

    if (errors !== null) {
      return { state: 'invalid', errors };
    }
  }

  // Only here does the status decide anything, and only the one thing it can
  // honestly decide: whether the server failed to answer or answered something
  // this client cannot use. Status `0` is a request that never reached a
  // server; every 5xx is a server that is up and broken. A 400 with no usable
  // map, a 409 with no kind, a 403, a 404 — each is a judgement, and a minute
  // does not change a judgement.
  return error.status === 0 || error.status >= 500
    ? { state: 'unreachable' }
    : { state: 'unreadable' };
}
