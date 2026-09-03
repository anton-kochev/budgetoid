// What a content screen renders in place of its list when this tab holds no
// content key.
//
// `docs/design/components.md`, "The locked account", is the specification, and
// the sentence is the blocked-action pattern from `docs/design/voice.md`: the
// fact, then the way forward, and the way forward is the smallest act that
// clears the block.
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
// is what makes "it cannot navigate" structural rather than remembered.
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
import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink } from '@angular/router';

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink],
  selector: 'app-locked-account-notice',
  styleUrls: ['./locked-account-notice.component.scss'],
  templateUrl: './locked-account-notice.component.html',
})
export class LockedAccountNoticeComponent {}
