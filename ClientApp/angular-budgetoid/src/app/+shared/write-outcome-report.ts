// What a screen puts on the page after a write, and the one place this client
// spells the four sentences the design book writes for a refusal.
//
// **`docs/design/components.md`, "A write that does not happen", is the
// authority.** That chapter's state table is the specification and says so of
// itself — "the copy is the specification, not an example of it" — so the
// strings below are transcriptions and nothing here invents one.
// `+core/api/write-outcome.ts` is the other half: it reads a refusal off the
// wire, this file decides where the refusal lands.
//
// **One module rather than one copy per screen, and the reason is the chapter's
// own.** Four writing surfaces on three screens render these sentences. Four
// copies of a sentence drift, and the drift is silent: nothing anywhere
// compares two screens' wording, so the day one of them is edited the product
// says two different things about one outcome and every test stays green.
//
// **The split is the component's and the map arrives whole.** An `errors` map
// belongs to the server and a form belongs to the client, so which of its keys
// can be *placed* is a fact only the caller knows — and it is "placed", not
// "recognised": the chapter puts a key the form has but is not currently
// rendering on the miss path, because the question is whether a message can
// land somewhere a person will see it. So the caller hands over the keys it can
// place **at this press**, and every entry it cannot goes to the region. One
// outcome, rendered in two places, which the chapter calls "one answer to one
// write rather than two".
//
// **The lookup is a `Map` and never an object literal indexed by the wire
// key.** `constructor`, `toString` and `valueOf` all hit on a literal, and the
// message is then placed under a control that does not exist. The same argument
// `write-outcome.ts` makes about the incoming document, kept on the outgoing
// side of it.
//
// **The client writes no copy for a field-keyed refusal.** The server's
// sentence is handed on verbatim: a client-authored table would have to be
// total over every string the API can send, and the fallback for a key nobody
// anticipated would be a generic sentence standing exactly where somebody is
// trying to make a correction. Rendering what arrived is total by construction.
//
// **`recorded` and `locked` are both silent, and for opposite reasons.**
// `recorded` has nothing to say because the row exists; `locked` has nothing to
// say because the screen's locked notice is already the account of it, and a
// second sentence is the duplicate the shared region refuses. The chapter's
// table gives `locked` no row for exactly that reason.
import type { AbstractControl, FormGroup } from '@angular/forms';
import type { WriteOutcome } from '@app-core/api/write-outcome';

/**
 * The validation key a control wears while it carries a server's sentence.
 *
 * **It exists to put the control into Material's error state, and for nothing
 * else.** `mat-form-field` renders its `mat-error` children only while the
 * control it wraps reports one, and it is the same machinery that binds the
 * message to the input with `aria-describedby` — which is what
 * `docs/design/accessibility.md` asks for and what a hand-rolled paragraph
 * beneath the field would not do. The message *text* lives in the
 * {@link WriteReport}, not in this error object: a template reading
 * `control.errors` would be reading a value no signal publishes.
 *
 * Angular drops it the next time the control's validators run, which is the
 * next keystroke — so a corrected field stops being red without anybody
 * remembering to say so.
 */
export const SERVER_MESSAGE_ERROR = 'server';

/**
 * The sentence for a create that collided on its own identifier.
 *
 * It says the entry is saved rather than offering another press, because the
 * id is minted in the browser and a second press sends the same one and
 * collects the same 409.
 */
export const ALREADY_SAVED_SENTENCE =
  'This entry is already saved. Reload the page to see it.';

/**
 * The sentence for the payee create's `duplicate_name`, its one source in the
 * product.
 *
 * It is reachable only when the one re-read still finds nothing to adopt, which
 * means the row holding that name did not open here — so the remedy is the
 * list, not Unlock.
 */
export const DUPLICATE_PAYEE_SENTENCE =
  'This payee already exists under a name this tab can’t read. Choose it from ' +
  'the list, or use a different name.';

/**
 * The sentence for a server that failed to answer rather than judged.
 *
 * It is the Account keys section's unreachable sentence with one clause added,
 * and the clause is the whole difference: somebody is looking at a form holding
 * text they typed, and the one thing they need before pressing anything is that
 * it is still there.
 */
export const UNREACHABLE_SENTENCE =
  'Budgetoid couldn’t reach the server. Nothing you typed has been lost — try ' +
  'again in a minute.';

/**
 * The sentence for an answer this client cannot read.
 *
 * It promises no retry — the server judged, and the same press collects the
 * same judgement — and offers the one act a person can take: **copy it, then
 * reload**, in that order, because a reload is what discards the typed value
 * and the screen lets them spend it rather than spending it for them.
 */
export const UNREADABLE_SENTENCE =
  'Budgetoid couldn’t save this, and didn’t say why. What you typed is still ' +
  'here — copy it, then reload the page.';

/**
 * The line a write of more than one request carries while it runs.
 *
 * `body` `--bud-text` rather than `--bud-over`: nothing has gone wrong. The
 * transaction form's payee-then-transaction pair is its only site today, and an
 * ordinary one-request write is not narrated at all.
 */
export const RECORDING_SENTENCE = 'Recording…';

/**
 * Where one write's answer renders.
 *
 * Two members and not two outcomes: an `errors` map some of whose keys a form
 * can place puts a message under each of those controls **and** a line in the
 * region for every key it cannot, which is one answer to one write.
 */
export interface WriteReport {
  /**
   * The server's sentences, keyed by the **control** they go beneath.
   *
   * Verbatim, and never rewritten — the copy is the API's and a client that
   * edited it would be the second definition the chapter forbids.
   */
  readonly fields: ReadonlyMap<string, readonly string[]>;
  /**
   * The lines the screen's one `role="status"` region carries, in order.
   *
   * One entry for a conflict or an unanswered request; one per unplaceable
   * `errors` entry, in the order the map sends them.
   */
  readonly lines: readonly string[];
}

/**
 * A report with nothing in it: the state a screen is in before its first write
 * and after one that landed.
 */
export const SILENT_WRITE_REPORT: WriteReport = {
  fields: new Map<string, readonly string[]>(),
  lines: [],
};

function assertNever(outcome: never): never {
  throw new Error(`unhandled write outcome: ${JSON.stringify(outcome)}`);
}

// The `invalid` half, split against the controls this press can place a message
// on. Insertion order is kept on both sides — `Map` preserves it and the region
// renders unplaceable entries "in the order the map sends them".
//
// **A key placed twice concatenates rather than replacing.** Two wire members
// can map onto one control, and dropping the earlier one loses a sentence the
// server sent — the defect this whole chapter is about, one layer in.
function splitValidation(
  errors: ReadonlyMap<string, readonly string[]>,
  controls: ReadonlyMap<string, string>,
): WriteReport {
  const fields = new Map<string, readonly string[]>();
  const lines: string[] = [];

  for (const [key, messages] of errors) {
    const control = controls.get(key);

    if (control === undefined) {
      lines.push(...messages);

      continue;
    }

    fields.set(control, [...(fields.get(control) ?? []), ...messages]);
  }

  return { fields, lines };
}

/**
 * Decides where one write's answer renders.
 *
 * @param outcome How the write ended, as `+core/api/write-outcome.ts` read it.
 * @param controls The wire keys this form can place a message on **right now**,
 *   each mapped to the control it goes beneath. A key absent from this map is a
 *   miss and its sentences go to the region, which covers a member the form
 *   does not have and a control the form is not currently rendering alike.
 */
export function writeReportOf(
  outcome: WriteOutcome,
  controls: ReadonlyMap<string, string>,
): WriteReport {
  switch (outcome.state) {
    case 'recorded':
    case 'locked':
      return SILENT_WRITE_REPORT;
    case 'invalid':
      return splitValidation(outcome.errors, controls);
    case 'duplicate-identifier':
      return {
        fields: SILENT_WRITE_REPORT.fields,
        lines: [ALREADY_SAVED_SENTENCE],
      };
    case 'duplicate-name':
      return {
        fields: SILENT_WRITE_REPORT.fields,
        lines: [DUPLICATE_PAYEE_SENTENCE],
      };
    case 'unreachable':
      return {
        fields: SILENT_WRITE_REPORT.fields,
        lines: [UNREACHABLE_SENTENCE],
      };
    case 'unreadable':
      return {
        fields: SILENT_WRITE_REPORT.fields,
        lines: [UNREADABLE_SENTENCE],
      };
    default:
      // A word added to `WriteOutcome` and not to the table above is a compile
      // error here rather than a screen that silently says nothing about it.
      return assertNever(outcome);
  }
}

/**
 * Puts every control named by a report into its error state, and answers the
 * first of them.
 *
 * The answer is what the chapter's *focus moves to the first such control* is
 * built on, and it is `null` where no control carries a message — where focus
 * stays exactly where the press left it, because moving a keyboard user into a
 * region takes them away from the control they are about to press again.
 *
 * @throws Error when the report names a control the form does not have, which
 *   is a caller defect rather than a state: the report was built from the
 *   caller's own lookup, so a name that misses here means a sentence the server
 *   sent would render nowhere at all.
 */
export function markFieldMessages(
  form: FormGroup,
  report: WriteReport,
): string | null {
  let first: string | null = null;

  for (const name of report.fields.keys()) {
    const control: AbstractControl | null = form.get(name);

    if (control === null) {
      throw new Error(`no control named ${name} to place a write's message on`);
    }

    control.setErrors({ [SERVER_MESSAGE_ERROR]: true });
    // Marked touched as well as errored, because a save reached from anywhere
    // but the form's own submit leaves Material's default error-state matcher
    // with neither a submitted form nor a touched control to go on.
    control.markAsTouched();
    first ??= name;
  }

  return first;
}

/**
 * Takes the server's sentences off every control wearing one.
 *
 * **Its one caller is the start of the next write**, which is the only thing
 * the chapter lets clear a write's account of itself. `updateValueAndValidity`
 * rather than `setErrors(null)`: the first re-runs the control's own
 * validators, so a field that is *also* empty or too long stays red for its own
 * reason, where the second would clear both and let a form submit something it
 * had already refused.
 */
export function clearFieldMessages(form: FormGroup): void {
  for (const control of Object.values(form.controls)) {
    if (control.hasError(SERVER_MESSAGE_ERROR)) {
      control.updateValueAndValidity({ emitEvent: false });
    }
  }
}
