import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { LockedAccountNoticeComponent } from './locked-account-notice.component';

function render(): HTMLElement {
  const fixture = TestBed.createComponent(LockedAccountNoticeComponent);
  fixture.detectChanges();

  return fixture.nativeElement as HTMLElement;
}

describe('LockedAccountNoticeComponent', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [LockedAccountNoticeComponent],
      providers: [provideRouter([])],
    }).compileComponents();
  });

  it('states the fact and then the way forward', () => {
    // Arrange, Act
    const host = render();

    // Act — one sentence pair, collapsed the way a reader hears it.
    const copy = host.textContent?.replace(/\s+/g, ' ').trim();

    // Assert — the whole message is words. With every accent removed the two
    // sentences still say what happened and what to press, so nothing here is
    // carried by colour.
    expect(copy).toBe(
      'This tab can’t read your account yet. Unlock it in Settings.',
    );
  });

  it('offers Settings as a link to the settings route', () => {
    // Arrange, Act
    const link = render().querySelector('a');

    // Assert — the accessible name is the word a person is looking for, and the
    // address is the one screen holding Unlock.
    expect(link?.textContent?.trim()).toBe('Settings');
    expect(link?.getAttribute('href')).toBe('/app/settings');
  });

  it('navigates nowhere by itself', () => {
    // Arrange — a notice that redirected would take Settings away with it, and
    // Settings is where the way out lives.
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigate');
    const navigateByUrl = vi.spyOn(router, 'navigateByUrl');

    // Act
    render();

    // Assert
    expect(navigate).not.toHaveBeenCalled();
    expect(navigateByUrl).not.toHaveBeenCalled();
  });

  it('is not a live region', () => {
    // Arrange, Act
    const host = render();

    // Assert — it is ordinary content in the region the list would have
    // occupied, landing in reading order where the reader already is.
    expect(
      host.querySelector('[aria-live], [role="status"], [role="alert"]'),
    ).toBeNull();
    expect(host.getAttribute('aria-live')).toBeNull();
    expect(host.getAttribute('role')).toBeNull();
  });
});
