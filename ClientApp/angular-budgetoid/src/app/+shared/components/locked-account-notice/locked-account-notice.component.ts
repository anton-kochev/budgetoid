// What a content screen renders in place of its list when the words it would
// draw cannot be trusted to be this account's.
//
// `docs/design/components.md`, "The locked account", is the specification, and
// each sentence is the blocked-action pattern from `docs/design/voice.md`: the
// fact, then the way forward, and the way forward is the smallest act that
// clears the block.
//
// **Two states send it and it renders a sentence for each**, named by one
// input. The second is a key rotation in flight, and the key-rotation chapter
// owns both its copy and the reason a run takes the lists away: a row a chunk
// has already re-sealed will not open under the generation custody holds, so a
// list drawn mid-run is part names and part em dashes. The *advice* is what
// differs — pressing Unlock during a run gets the generation on its way out, so
// the smallest act that clears that block is waiting, and the sentence names
// where it can be watched instead of what to press.
//
// **The input is the whole of what the second state added here.** This class
// still injects nothing, reads no status and calls no `Router`: the screen
// already computes which state it is in, and passing a word is not reaching for
// one.
//
// **It links and it never navigates.** Two other shapes were considered and
// both were refused, and the second is the tempting one because it is a single
// change covering every screen:
//
//   * a route guard — synchronous against a fact with no resolution on the
//     navigation path, so it could only bounce every reload, and it would put
//     key state exactly where `AccountUnlockService` is built to keep it out of;
//   * swapping the shell's outlet — it would lock Settings too, and Settings is
//     the screen holding Unlock. The one surface that must stay reachable is the
//     one that shape takes away.
//
// So this component injects nothing, reads no status and calls no `Router`.
// A `routerLink` is the whole of its behaviour, and the emptiness of the class
// — one input and not a single injected member — is what makes "it cannot
// navigate" structural rather than remembered.
//
// **It is a locked *account*, never a locked session.** A locked session is a
// server fact about a federated credential's row (`docs/business-logic/
// sessions.md`); this is a browser fact — the session is live, every request is
// answered, and the words that come back cannot be read. The copy says "this
// tab" for that reason and must not be rewritten into saying anything about
// signing in.
//
// **Not a live region.** The locked state is the answer to a navigation the
// person made, not an event that arrived, and it lands in reading order in the
// space the list would have occupied.
import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { RouterLink } from '@angular/router';

/**
 * Which of the two states put this notice where a list would have been.
 *
 * **A word the screen passes and never one this component works out.** Both are
 * facts the screen is already holding — custody's status and whether a run is in
 * flight — and the reason this is a union rather than a boolean is that neither
 * of them is the negation of the other: a run wins when both are true, and the
 * screen is where that is decided.
 */
export type LockedAccountReason = 'locked' | 'rotating';

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink],
  selector: 'app-locked-account-notice',
  styleUrls: ['./locked-account-notice.component.scss'],
  templateUrl: './locked-account-notice.component.html',
})
export class LockedAccountNoticeComponent {
  /**
   * Which state sent this notice, and therefore which sentence it carries.
   *
   * **Required, so a screen cannot arrive without saying.** A default would
   * make one of the two states the one a forgotten binding falls into, and the
   * sentence it fell into would be advice about a control that cannot help.
   */
  public readonly reason = input.required<LockedAccountReason>();
}
