import { TestBed } from '@angular/core/testing';
import type { NarrativeText } from '@app-core/security/narrative-text';
import { beforeEach, describe, expect, it } from 'vitest';
import { NarrativeValueComponent } from './narrative-value.component';

// The two accessible names are written out here as literals rather than
// imported from the component. Importing them would make this spec agree with
// whatever the component says — including with one name pasted over both, which
// is the exact defect the chapter warns about and the one a sighted reviewer
// cannot see, because the glyph is identical either way.
const UNREADABLE_NAME = 'Couldn’t be read';
const LOCKED_NAME = 'Locked';

// U+2014. Built from its code point so that a passing spec is a spec about an em
// dash rather than about whatever dash-shaped character was pasted into it.
const EM_DASH = String.fromCharCode(0x2014);

function render(value: NarrativeText | null): HTMLElement {
  const fixture = TestBed.createComponent(NarrativeValueComponent);
  fixture.componentRef.setInput('value', value);
  fixture.detectChanges();

  return fixture.nativeElement as HTMLElement;
}

describe('NarrativeValueComponent', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [NarrativeValueComponent],
    }).compileComponents();
  });

  it('renders an opened value as its text', () => {
    // Arrange
    const value: NarrativeText = { state: 'text', value: 'Groceries' };

    // Act
    const host = render(value);

    // Assert
    expect(host.querySelector('.nv-text')?.textContent).toBe('Groceries');
    expect(host.querySelector('.nv-marker')).toBeNull();
  });

  it('renders a cleared note as text that shows nothing, not as an absent value', () => {
    // Arrange — an empty string is a note somebody cleared.
    const value: NarrativeText = { state: 'text', value: '' };

    // Act
    const host = render(value);

    // Assert — the element is there and carries nothing, which is what tells it
    // apart from a column that held nothing at all.
    const text = host.querySelector('.nv-text');
    expect(text).not.toBeNull();
    expect(text?.textContent).toBe('');
    expect(host.querySelector('.nv-marker')).toBeNull();
  });

  it('renders an absent column as nothing at all, with no dash', () => {
    // Arrange — the column held nothing, so there is no value here to fail to
    // show.
    const value = null;

    // Act
    const host = render(value);

    // Assert
    expect(host.querySelector('.nv-text')).toBeNull();
    expect(host.querySelector('.nv-marker')).toBeNull();
    expect(host.textContent?.trim()).toBe('');
  });

  it('names an unreadable value “couldn’t be read”', () => {
    // Arrange
    const value: NarrativeText = { state: 'unreadable' };

    // Act
    const host = render(value);
    const marker = host.querySelector('.nv-marker');

    // Assert — the name, not just the glyph. `role="img"` is what exposes an
    // `aria-label` on a span at all: without it the label sits on a generic
    // element and the marker reaches a screen reader as a bare dash.
    expect(marker?.getAttribute('role')).toBe('img');
    expect(marker?.getAttribute('aria-label')).toBe(UNREADABLE_NAME);
    expect(marker?.textContent?.trim()).toBe(EM_DASH);
  });

  it('names a locked value “locked”', () => {
    // Arrange
    const value: NarrativeText = { state: 'locked' };

    // Act
    const host = render(value);
    const marker = host.querySelector('.nv-marker');

    // Assert
    expect(marker?.getAttribute('role')).toBe('img');
    expect(marker?.getAttribute('aria-label')).toBe(LOCKED_NAME);
    expect(marker?.textContent?.trim()).toBe(EM_DASH);
  });

  it('gives the two dashes different names and identical presentation', () => {
    // Arrange
    const unreadable = render({ state: 'unreadable' }).querySelector(
      '.nv-marker',
    );
    const locked = render({ state: 'locked' }).querySelector('.nv-marker');

    // Act
    const names = [
      unreadable?.getAttribute('aria-label'),
      locked?.getAttribute('aria-label'),
    ];

    // Assert — the difference is carried by the name and by nothing a stylesheet
    // could reach: same element, same classes, same text. Colour is never the
    // message, so a second class here (to paint one of them) reddens this.
    expect(names[0]).not.toBe(names[1]);
    expect(locked?.tagName).toBe(unreadable?.tagName);
    expect(locked?.className).toBe(unreadable?.className);
    expect(locked?.textContent).toBe(unreadable?.textContent);
  });

  it('puts no render inside a live region', () => {
    // Arrange
    const values: readonly (NarrativeText | null)[] = [
      { state: 'text', value: 'Groceries' },
      { state: 'unreadable' },
      { state: 'locked' },
      null,
    ];

    // Act
    const hosts = values.map((value) => render(value));

    // Assert — a locked list is the answer to a navigation somebody made, not
    // news that arrived; ten markers in a `role="status"` narrate ten dashes.
    for (const host of hosts) {
      expect(
        host.querySelector('[aria-live], [role="status"], [role="alert"]'),
      ).toBeNull();
      expect(host.getAttribute('aria-live')).toBeNull();
      expect(host.getAttribute('role')).toBeNull();
    }
  });
});
