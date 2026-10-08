import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  afterNextRender,
  afterRenderEffect,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { RouterLink } from '@angular/router';
import { ProviderDepartureService } from '@app-core/services/provider-departure.service';
import { SessionService } from '@app-core/session/session.service';
import { BrandLockupComponent } from '@app-shared/components/brand-lockup/brand-lockup.component';
import {
  readScheduledInstant,
  type ScheduledInstant,
} from '@app-shared/scheduled-instant';
import {
  ReleaseFlowService,
  type ReleaseScheduleFailure,
  type ReleaseSignInFailure,
} from './release-flow.service';

/**
 * Which surface the screen draws. The session status and the schedule pick it,
 * and nothing else — a sign-in's refusal lands in the region over `before`,
 * and a schedule's over `nothing-scheduled`; neither is a surface of its own.
 */
type ReleaseState = 'before' | 'signing-in' | 'nothing-scheduled' | 'scheduled';

/** Every line the status region can carry, keyed by what raised it. */
type RegionLine =
  | 'departing'
  | 'signing-in'
  | 'scheduling'
  | `sign-in:${ReleaseSignInFailure}`
  | `schedule:${ReleaseScheduleFailure}`;

// The screen for somebody who has lost every passkey and every recovery code:
// it signs them in with Google into a locked session and, from there, files the
// account's erasure, so the account and its address are released. It recovers
// nothing and says so before the first press. See docs/design/components.md,
// "Releasing an account", and docs/design/voice.md, "An account nobody can
// open".
//
// One `h1`, the same in every state, and one polite `role="status"` region in
// the DOM from first paint. The states replace each other on one route, so a
// heading that changed would rename the page under the reader; the region is
// where a change is announced.
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    BrandLockupComponent,
    MatButtonModule,
    MatCheckboxModule,
    RouterLink,
  ],
  // **The custody decision**, the one `/register` and Welcome make: the Google
  // answer is taken by the flow this screen owns, and dies with it.
  providers: [ReleaseFlowService],
  styleUrls: ['./release.component.scss'],
  templateUrl: './release.component.html',
})
export class ReleaseComponent {
  private readonly session = inject(SessionService);
  private readonly departure = inject(ProviderDepartureService);

  protected readonly flow = inject(ReleaseFlowService);
  protected readonly departing = this.departure.departing;

  // Whether the screen has rendered once. Lines wait for it, as the email
  // change's do: `unconfirmed` arrives with the load, and a line present at
  // the first paint is announced unreliably.
  protected readonly painted = signal(false);

  protected readonly scheduledAt = computed<ScheduledInstant | null>(() => {
    const schedule = this.session.scheduledErasure();

    return schedule === null || schedule === 'unread'
      ? null
      : readScheduledInstant(schedule.takesEffectAtUtc);
  });

  // An instant that does not parse renders as nothing scheduled, whose commit
  // answers with the stored instant.
  //
  // A locked session whose schedule this tab has not read renders as nothing
  // scheduled. That is safe rather than optimistic: a repeated schedule is
  // answered with the instant stored the first time.
  protected readonly state = computed<ReleaseState>(() => {
    if (this.session.status() === 'locked-session') {
      return this.scheduledAt() === null ? 'nothing-scheduled' : 'scheduled';
    }

    return this.flow.signingIn() ? 'signing-in' : 'before';
  });

  // Both locked states: the statement and Sign out are drawn in either.
  protected readonly locked = computed<boolean>(
    () => this.state() === 'nothing-scheduled' || this.state() === 'scheduled',
  );

  protected readonly line = computed<RegionLine | null>(() => {
    switch (this.state()) {
      case 'signing-in':
        return 'signing-in';
      case 'before': {
        if (this.departing()) {
          return 'departing';
        }

        const failure = this.flow.signInFailure();

        return failure === null ? null : `sign-in:${failure}`;
      }
      case 'nothing-scheduled':
      case 'scheduled': {
        if (this.flow.scheduling()) {
          return 'scheduling';
        }

        const failure = this.flow.scheduleFailure();

        return failure === null ? null : `schedule:${failure}`;
      }
    }
  });

  protected readonly lineText = computed<string | null>(() => {
    const line = this.line();

    return line === null ? null : textOf(line);
  });

  // Waiting lines read as prose; refusals take `--bud-over`, and every one of
  // them reads the same with the colour removed.
  protected readonly lineIsRefusal = computed<boolean>(() => {
    const line = this.line();

    return (
      line !== null &&
      line !== 'departing' &&
      line !== 'signing-in' &&
      line !== 'scheduling'
    );
  });

  // Rendered below the region only while the `no-account` line stands: the
  // region carries no control.
  protected readonly offersRegistration = computed<boolean>(
    () => this.line() === 'sign-in:no-account',
  );

  // The result sentence, present only on `scheduled`. `read: ElementRef` keeps
  // the query on the paragraph rather than on anything Angular attaches to it.
  private readonly result = viewChild<string, ElementRef<HTMLElement>>(
    'result',
    { read: ElementRef },
  );

  constructor() {
    afterNextRender(() => {
      this.painted.set(true);
    });

    // **The commit leaving moves focus to the result sentence**, because the
    // control focus stood on has gone. Watched as the surface going from
    // nothing scheduled to scheduled — never as the sentence appearing — so a
    // load that finds a schedule, or a sign-in that answers with one, moves
    // nothing: the person made no press on this load.
    //
    // A render effect and not an `effect()`: it touches the DOM. The move is
    // *owed* from the transition until the sentence is there to take it, and
    // the query is read on every run, so a pass that saw the state change
    // before the view drew the sentence pays the debt on the pass that does.
    let previous: ReleaseState | null = null;
    let owed = false;

    afterRenderEffect(() => {
      const current = this.state();
      const sentence = this.result();

      if (previous === 'nothing-scheduled' && current === 'scheduled') {
        owed = true;
      } else if (current !== 'scheduled') {
        owed = false;
      }

      previous = current;

      if (owed && sentence !== undefined) {
        owed = false;
        sentence.nativeElement.focus();
      }
    });
  }
}

// The copy is the specification, not an example of it: the design book's Copy
// table, character for character. A `switch` over the closed union, so a word
// the flow grows fails to compile here instead of rendering an empty region.
function textOf(line: RegionLine): string {
  switch (line) {
    case 'departing':
      return 'Taking you to Google…';
    case 'signing-in':
      return 'Checking your Google sign-in…';
    case 'scheduling':
      return 'Scheduling the erasure…';
    case 'sign-in:no-account':
      return 'There’s no Budgetoid account for that Google account. Nothing has changed.';
    case 'sign-in:provider-refused':
      return 'Google didn’t confirm that account, so nothing has changed. Try again, or choose another Google account.';
    case 'sign-in:unrecognised':
      return 'Budgetoid couldn’t read this request. Reload the page and try again — nothing has changed.';
    case 'sign-in:full-session':
      return 'This browser is already signed in to Budgetoid, so nothing has changed. Reload the page to open that account.';
    case 'sign-in:undetermined':
      return 'Budgetoid can’t tell whether you’re signed in. Reload the page to find out.';
    case 'sign-in:unconfirmed':
      return 'Signing in with Google didn’t finish. Nothing has changed — try again whenever you’re ready.';
    case 'sign-in:unavailable':
      return 'Budgetoid couldn’t reach Google, so nothing has changed. Try again in a minute.';
    case 'schedule:unrecognised':
      return 'Budgetoid couldn’t read this request, so nothing was scheduled. Reload the page and try again.';
    case 'schedule:undetermined':
      return 'Budgetoid didn’t hear back, so this may already be scheduled. Press again to check — asking twice never changes the date.';
  }
}
