// The notice the signed-in shell carries while the account's erasure is
// scheduled (FR-116). A sign-in with the account's Google account can file the
// erasure from `/release` without any passkey; this is how the owner, signed
// in with a passkey that survived, learns that somebody did.
//
// `SessionService` is replaced by the one signal the notice reads, driven by
// hand: the notice renders what this tab has learned and asks nothing itself.
//
// The runner's zone is pinned to `Pacific/Kiritimati`, UTC+14
// (`src/test-setup.ts`): 10:30 UTC on the 9th is 00:30 on the 10th for this
// reader. Expected strings come from `Intl` in the runner's own locale, never
// from an `en-US` literal.
import { signal, type WritableSignal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import {
  SessionService,
  type ScheduledErasure,
} from '@app-core/session/session.service';
import { beforeEach, describe, expect, it } from 'vitest';
import { ScheduledErasureNoticeComponent } from './scheduled-erasure-notice.component';

const INSTANT = '2026-10-09T10:30:00Z';

const DATE = new Intl.DateTimeFormat(undefined, {
  day: 'numeric',
  month: 'long',
  year: 'numeric',
}).format(new Date(INSTANT));
const TIME = new Intl.DateTimeFormat(undefined, {
  hour: 'numeric',
  minute: '2-digit',
}).format(new Date(INSTANT));

// The book's sentence, word for word, typographic apostrophes included.
const NOTICE =
  `This account will be erased on ${DATE} at ${TIME}. ` +
  'A sign-in with its Google account asked for this. ' +
  'If that wasn’t you, or you’ve changed your mind, cancel it in Settings ' +
  'with a passkey.';

const LIVE_REGION_SELECTOR =
  '[role="status"], [role="alert"], [role="log"], [aria-live]';

function collapse(text: string | null | undefined): string {
  return (text ?? '').replace(/\s+/g, ' ').trim();
}

describe('ScheduledErasureNoticeComponent', () => {
  let scheduled: WritableSignal<ScheduledErasure>;
  let fixture: ComponentFixture<ScheduledErasureNoticeComponent>;
  let host: HTMLElement;

  function render(): void {
    fixture = TestBed.createComponent(ScheduledErasureNoticeComponent);
    host = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();
  }

  beforeEach(() => {
    scheduled = signal<ScheduledErasure>({ takesEffectAtUtc: INSTANT });
    TestBed.configureTestingModule({
      imports: [ScheduledErasureNoticeComponent],
      providers: [
        { provide: SessionService, useValue: { scheduledErasure: scheduled } },
      ],
    });
  });

  // The guard on this file's ability to tell the reader's zone from UTC.
  it('runs in a zone where a UTC rendering can be caught', () => {
    // Assert
    expect(new Date(INSTANT).getTimezoneOffset()).toBe(-14 * 60);
  });

  it('says the book’s sentence while an erasure is scheduled', () => {
    // Act
    render();

    // Assert
    expect(collapse(host.textContent)).toBe(NOTICE);
  });

  // 10:30 UTC on the 9th is 00:30 on the 10th at UTC+14. A rendering in UTC —
  // `slice` on the wire string, `getUTCDate`, `timeZone: 'UTC'` — says the 9th
  // at 10:30, and passes nothing below. Asserted without `Intl` on the
  // expected side too, so the derivation above cannot drift with the code.
  it('renders the date and time in the reader’s own zone', () => {
    // Act
    render();

    // Assert
    const time = host.querySelector('time');
    const words = collapse(time?.textContent).split(/\W+/);

    expect(words).toContain('10');
    expect(words).not.toContain('9');
    expect(collapse(time?.textContent)).toMatch(/(^|\D)(00|12):30(\D|$)/);
    expect(collapse(time?.textContent)).not.toMatch(/10:30/);
  });

  it('wraps the date and time in a time element carrying the instant as it arrived', () => {
    // Act
    render();

    // Assert
    const times = host.querySelectorAll('time');

    expect(times).toHaveLength(1);
    expect(times[0]?.getAttribute('datetime')).toBe(INSTANT);
    expect(collapse(times[0]?.textContent)).toBe(`${DATE} at ${TIME}`);
  });

  // **Not a live region.** It is a standing fact about the account, there on
  // every screen of the visit; announced, it would interrupt every navigation
  // with the same sentence.
  it('is plain prose, not a live region', () => {
    // Act
    render();

    // Assert
    expect(collapse(host.textContent)).toBe(NOTICE);
    expect(host.querySelector(LIVE_REGION_SELECTOR)).toBeNull();
    expect(host.closest(LIVE_REGION_SELECTOR)).toBeNull();
    expect(host.matches(LIVE_REGION_SELECTOR)).toBe(false);
  });

  // No link: Settings is always in the navigation, a link would point at the
  // page the person is already on when they are there, and it would add a tab
  // stop to every screen. No "Warning" label, and no address — the notice is on
  // a screen anyone at the device can see.
  it('carries no link, no warning label and no address', () => {
    // Act
    render();

    // Assert
    expect(collapse(host.textContent)).toBe(NOTICE);
    expect(host.querySelector('a, button, [tabindex]')).toBeNull();
    expect(collapse(host.textContent)).not.toMatch(/warning/i);
    expect(collapse(host.textContent)).not.toContain('@');
  });

  it.each([
    { label: 'nothing has been read', value: 'unread' as const },
    { label: 'nothing is scheduled', value: null },
  ])('renders nothing while $label', ({ value }) => {
    // Arrange
    scheduled.set(value);

    // Act
    render();

    // Assert
    expect(collapse(host.textContent)).toBe('');
    expect(host.querySelector('p, time')).toBeNull();
  });

  // The cancellation's 204 publishes `null`, and the notice goes with it on
  // every screen — not on the next reload.
  it('leaves once the erasure is cancelled', () => {
    // Arrange
    render();
    expect(collapse(host.textContent)).toBe(NOTICE);

    // Act
    scheduled.set(null);
    fixture.detectChanges();

    // Assert
    expect(collapse(host.textContent)).toBe('');
  });

  // A schedule learned after the shell drew — the read `established()` makes,
  // or a refresh when the tab comes back into view — appears without a reload.
  it('appears once a schedule is learned after it drew', () => {
    // Arrange
    scheduled.set(null);
    render();

    // Act
    scheduled.set({ takesEffectAtUtc: INSTANT });
    fixture.detectChanges();

    // Assert
    expect(collapse(host.textContent)).toBe(NOTICE);
  });
});
