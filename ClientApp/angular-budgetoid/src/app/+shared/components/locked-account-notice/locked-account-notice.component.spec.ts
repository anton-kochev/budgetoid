// The notice, driven once per reason.
//
// **The two sentences are the whole subject, and they are asserted collapsed.**
// A reader hears the fact and the way forward as one thought, so the assertion
// is over the paragraph's own text rather than over the nodes it is made of —
// which is also what keeps a change of markup from reading as a change of copy.
//
// **The input is pinned structurally as well as by what it renders.** The
// chapter's claim is that the screen computes which state it is in and passes a
// word, so the component goes on injecting nothing and reading nothing: the last
// case takes the instance's own properties and finds the input and nothing else,
// which is what an `inject()` added here would land in.
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import {
  LockedAccountNoticeComponent,
  type LockedAccountReason,
} from './locked-account-notice.component';

function render(reason: LockedAccountReason): HTMLElement {
  const fixture = TestBed.createComponent(LockedAccountNoticeComponent);

  fixture.componentRef.setInput('reason', reason);
  fixture.detectChanges();

  return fixture.nativeElement as HTMLElement;
}

// The paragraph as a reader hears it: one run of words, whatever the markup
// underneath has done with them.
function copyOf(host: HTMLElement): string {
  return (host.textContent ?? '').replace(/\s+/g, ' ').trim();
}

describe('LockedAccountNoticeComponent', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [LockedAccountNoticeComponent],
      providers: [provideRouter([])],
    }).compileComponents();
  });

  it('states the fact and then the way forward while the account is locked', () => {
    // Arrange, Act
    const host = render('locked');

    // Assert — the whole message is words. With every accent removed the two
    // sentences still say what happened and what to press, so nothing here is
    // carried by colour.
    expect(copyOf(host)).toBe(
      'This tab can’t read your account yet. Unlock it in Settings.',
    );
  });

  it('says what a run is doing and where to watch it while a rotation is in flight', () => {
    // Arrange — the second state. Pressing Unlock mid-run gets the generation
    // that is on its way out, so the other sentence's advice is false here and
    // the smallest act that clears this block is waiting.

    // Act
    const host = render('rotating');

    // Assert
    expect(copyOf(host)).toBe(
      'Budgetoid is giving this account new keys. Your records come back when ' +
        'it finishes — watch it in Settings.',
    );
  });

  it('offers Settings as a link to the settings route for either reason', () => {
    // Arrange, Act
    const locked = render('locked').querySelector('a');
    const rotating = render('rotating').querySelector('a');

    // Assert — the accessible name is the word a person is looking for, and the
    // address is the one screen holding both the way out and the run to watch.
    expect(locked?.textContent?.trim()).toBe('Settings');
    expect(locked?.getAttribute('href')).toBe('/app/settings');
    expect(rotating?.textContent?.trim()).toBe('Settings');
    expect(rotating?.getAttribute('href')).toBe('/app/settings');
  });

  it('navigates nowhere by itself', () => {
    // Arrange — a notice that redirected would take Settings away with it, and
    // Settings is where the way out lives.
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigate');
    const navigateByUrl = vi.spyOn(router, 'navigateByUrl');

    // Act
    render('locked');
    render('rotating');

    // Assert
    expect(navigate).not.toHaveBeenCalled();
    expect(navigateByUrl).not.toHaveBeenCalled();
  });

  it('is not a live region', () => {
    // Arrange, Act
    const host = render('rotating');

    // Assert — it is ordinary content in the region the list would have
    // occupied, landing in reading order where the reader already is.
    expect(
      host.querySelector('[aria-live], [role="status"], [role="alert"]'),
    ).toBeNull();
    expect(host.getAttribute('aria-live')).toBeNull();
    expect(host.getAttribute('role')).toBeNull();
  });

  it('is told which state sent it and reads nothing to find out', () => {
    // Arrange — the emptiness of this class is what makes "it cannot navigate"
    // structural. An `inject()` here would be an own property of the instance,
    // so the census below is what an injected status read or a `Router` lands
    // in.
    const fixture = TestBed.createComponent(LockedAccountNoticeComponent);

    fixture.componentRef.setInput('reason', 'locked');
    fixture.detectChanges();

    // Act — `__ngContext__` is the framework's own back-pointer, stamped onto
    // every component instance there is, so it is dropped rather than asserted.
    // Nothing a developer writes is spelled that way.
    const members = Object.keys(fixture.componentInstance).filter(
      (member) => member !== '__ngContext__',
    );

    // Assert
    expect(members).toEqual(['reason']);
  });
});
