// One narrative value, rendered four ways, and none of the four is a stand-in
// for another.
//
// `docs/design/components.md`, "The locked account", is the specification. What
// this file adds is the reason the four renders are decided here in TypeScript
// rather than as four arms of a `@switch` in the template.
//
// **The two dashes carry different accessible names and that is the whole
// component.** They draw the identical glyph and mean opposite things:
// `unreadable` says the app could not read something it should have been able to
// read, `locked` says it did not try and the remedy is one press. Collapsed into
// one name, a screen-reader user is told their data is damaged when nothing is.
// A sighted reviewer cannot see the difference — both are an em dash — so the
// only thing standing between the product and that defect is the pair of
// literals below and the spec that reads them back.
//
// **The marker is one element in the template, not one per state.** Which is
// what makes "colour is never the message" structural here rather than
// remembered: there is no second class for a stylesheet to paint, so the two
// renders cannot drift into being told apart by ink. Splitting the element to
// style one of them is the change that breaks the rule, and it reddens the spec
// that compares the two markers' classes.
//
// **The switch is here so that a fourth word cannot be rendered as silence.**
// `NarrativeText` has three states today and `null` beside it for a column that
// held nothing. A `@switch` in the template covering three of four possible
// words compiles, ships, and renders an empty box for the one it forgot; the
// `never` in the default arm below is a compile error instead. It is the same
// decision `compare-narrative.ts` makes with `satisfies` and for the same
// reason.
//
// **`null` is the absent column and it is not a fourth word.**
// `narrative-text.ts` argues why the row's question and the key's question stay
// apart in the type; this file is where that split becomes two renders. An
// absent column draws nothing — no dash — because there is no value here that
// failed to appear. An empty string is the opposite case and draws an empty
// element: somebody cleared that note, and the two must never be folded, in
// either direction.
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  input,
} from '@angular/core';
import type { NarrativeText } from '@app-core/security/narrative-text';

// The one pair of accessible names in the product. Sentence case and a
// typographic apostrophe, per `docs/design/voice.md`.
const UNREADABLE_NAME = 'Couldn’t be read';
const LOCKED_NAME = 'Locked';

/**
 * What the template draws: the text, one dash under a name, or nothing.
 *
 * `marker` carries its name rather than its word, so the template holds one
 * element for both dashes and no branch that could style them apart.
 */
type NarrativeRender =
  | { readonly kind: 'text'; readonly text: string }
  | { readonly kind: 'marker'; readonly name: string }
  | { readonly kind: 'nothing' };

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-narrative-value',
  styleUrls: ['./narrative-value.component.scss'],
  templateUrl: './narrative-value.component.html',
})
export class NarrativeValueComponent {
  /**
   * One narrative value as the row read it, or `null` for a column that held
   * nothing.
   *
   * A mapper may never hand this component `''` or `'—'` in place of a word: the
   * moment `locked` becomes an empty string the screen is making a claim about
   * the account where the truth is about this tab, and nothing downstream can
   * tell the two apart again.
   */
  public readonly value = input.required<NarrativeText | null>();

  protected readonly render = computed<NarrativeRender>(() => {
    const narrative = this.value();

    if (narrative === null) {
      return { kind: 'nothing' };
    }

    switch (narrative.state) {
      case 'text':
        // Including the empty string, which renders as an empty element: a note
        // somebody cleared, not one nobody wrote.
        return { kind: 'text', text: narrative.value };
      case 'unreadable':
        return { kind: 'marker', name: UNREADABLE_NAME };
      case 'locked':
        return { kind: 'marker', name: LOCKED_NAME };
      default: {
        // Exhaustive over `NarrativeText`: a fourth word reddens here, where the
        // template alone would render it as an empty box and say nothing.
        const unreachable: never = narrative;

        return unreachable;
      }
    }
  });
}
