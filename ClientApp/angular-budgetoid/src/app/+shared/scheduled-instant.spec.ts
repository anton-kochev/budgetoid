// The scheduled erasure's instant as the reader's clock reads it: the wire
// string kept for `<time datetime>`, and a date and a time in the reader's own
// zone. Extracted from `release.component.ts` unchanged, so the release screen
// and the shell's notice cannot disagree about one instant —
// `release.component.spec.ts` stays as the screen-level guard and is not
// edited by the extraction.
//
// The runner's zone is pinned to `Pacific/Kiritimati`, UTC+14, in
// `src/test-setup.ts`: 10:30 UTC on the 9th is 00:30 on the 10th there. The
// expected strings are derived from `Intl` in the runner's own locale rather
// than written out, because the locale is the host's and an `en-US` literal
// would pin the machine, not the code.
import { describe, expect, it } from 'vitest';
import { readScheduledInstant } from './scheduled-instant';

const INSTANT = '2026-10-09T10:30:00Z';

// The formats the release screen renders with, which this module carries.
const DATE_FORMAT: Intl.DateTimeFormatOptions = {
  day: 'numeric',
  month: 'long',
  year: 'numeric',
};
const TIME_FORMAT: Intl.DateTimeFormatOptions = {
  hour: 'numeric',
  minute: '2-digit',
};

function inReadersZone(
  wire: string,
  format: Intl.DateTimeFormatOptions,
): string {
  return new Intl.DateTimeFormat(undefined, format).format(new Date(wire));
}

function inUtc(wire: string, format: Intl.DateTimeFormatOptions): string {
  return new Intl.DateTimeFormat(undefined, {
    ...format,
    timeZone: 'UTC',
  }).format(new Date(wire));
}

describe('readScheduledInstant', () => {
  // The guard on this file's ability to tell the reader's zone from UTC: it
  // goes red the day the pin in `test-setup.ts` is removed, and every case
  // below would otherwise pass against a UTC rendering on a UTC host.
  it('runs in a zone where a UTC rendering can be caught', () => {
    // Assert
    expect(new Date(INSTANT).getTimezoneOffset()).toBe(-14 * 60);
    expect(inReadersZone(INSTANT, DATE_FORMAT)).not.toBe(
      inUtc(INSTANT, DATE_FORMAT),
    );
    expect(inReadersZone(INSTANT, TIME_FORMAT)).not.toBe(
      inUtc(INSTANT, TIME_FORMAT),
    );
  });

  it('keeps the wire string exactly as it arrived', () => {
    // Act
    const read = readScheduledInstant(INSTANT);

    // Assert
    // It is what `<time datetime>` carries; a re-serialized
    // `2026-10-09T10:30:00.000Z` names the same instant in a spelling the
    // server never sent.
    expect(read?.wire).toBe(INSTANT);
  });

  it('reads the date and time in the reader’s own zone', () => {
    // Act
    const read = readScheduledInstant(INSTANT);

    // Assert
    expect(read?.date).toBe(inReadersZone(INSTANT, DATE_FORMAT));
    expect(read?.time).toBe(inReadersZone(INSTANT, TIME_FORMAT));
  });

  // The same fact pinned without `Intl` on the expected side, so an
  // implementation and an expectation that drifted together — both on
  // `timeZone: 'UTC'` — still part company here.
  it('says the 10th at half past midnight, never the 9th at 10:30', () => {
    // Act
    const read = readScheduledInstant(INSTANT);

    // Assert
    const dateWords = (read?.date ?? '').split(/\W+/);

    expect(dateWords).toContain('10');
    expect(dateWords).not.toContain('9');
    expect(read?.date).toContain('2026');
    expect(read?.time).toMatch(/(^|\D)(00|12):30(\D|$)/);
    expect(read?.time).not.toMatch(/10:30/);
  });

  it('reads an instant carrying an offset at that offset', () => {
    // Arrange
    // The same instant as `INSTANT`, written at +02:00.
    const offset = '2026-10-09T12:30:00+02:00';

    // Act
    const read = readScheduledInstant(offset);

    // Assert
    expect(read?.wire).toBe(offset);
    expect(read?.date).toBe(inReadersZone(INSTANT, DATE_FORMAT));
    expect(read?.time).toBe(inReadersZone(INSTANT, TIME_FORMAT));
  });

  it.each(['', 'not an instant', '2026-13-45T99:99:99Z'])(
    'answers null for %j rather than a date nobody can read',
    (wire) => {
      // Act
      const read = readScheduledInstant(wire);

      // Assert
      // The release screen renders nothing scheduled for `null`; an
      // `Invalid Date` string would reach the person instead.
      expect(read).toBeNull();
    },
  );
});
