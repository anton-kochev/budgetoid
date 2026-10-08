import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
} from '@angular/core';
import { SessionService } from '@app-core/session/session.service';
import { readScheduledInstant } from '@app-shared/scheduled-instant';

// The standing notice the signed-in shell carries while the account's erasure
// is scheduled (FR-116). A sign-in with the account's Google account can file
// the erasure from `/release` without any passkey; this is how the owner,
// signed in with a passkey that survived, learns that somebody did.
//
// **It renders what this tab has learned and asks nothing itself.** The
// schedule is `SessionService`'s — the probe, the read beside an established
// session, the refresh `ShellComponent` makes when the tab comes back into
// view, and a cancellation's `204` all write it there — so the notice and the
// Settings section read one fact.
//
// **Its own component and not shell markup**, because the shell's stylesheet
// budget is spent on the navigation.
//
// **Not a live region.** A standing fact about the account, there on every
// screen of the visit; announced, it would interrupt every navigation with the
// same sentence. And **no link**: Settings is always in the navigation, a link
// would point at the page the person is already on when they are there, and it
// would add a tab stop to every screen.
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-scheduled-erasure-notice',
  styleUrls: ['./scheduled-erasure-notice.component.scss'],
  templateUrl: './scheduled-erasure-notice.component.html',
})
export class ScheduledErasureNoticeComponent {
  private readonly session = inject(SessionService);

  // The instant in the reader's own zone and locale, or `null` while nothing
  // is scheduled or nothing has been read. `readScheduledInstant` is the
  // release screen's formatter, so the two screens cannot disagree about one
  // instant.
  protected readonly instant = computed(() => {
    const scheduled = this.session.scheduledErasure();

    return scheduled === null || scheduled === 'unread'
      ? null
      : readScheduledInstant(scheduled.takesEffectAtUtc);
  });
}
