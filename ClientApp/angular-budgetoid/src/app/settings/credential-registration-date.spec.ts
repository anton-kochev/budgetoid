import { describe, expect, it } from 'vitest';
import { credentialRegistrationDate } from './credential-registration-date';

// The mirror image of `export-filename.spec.ts`, and deliberately so: that file
// proves a name is minted in **UTC** whatever zone the host sits in, this one
// proves a date is rendered in the **reader's** zone whatever UTC says. Both are
// blind on a host at offset 0, where the local getters and the UTC getters agree
// on every field, and both are gated by CI on `ubuntu-latest`, whose `TZ` is
// exactly that. `src/test-setup.ts` pins the runner to `Pacific/Kiritimati`
// (UTC+14) so the two can discriminate at all; the first test below is the guard
// on that pin.
//
// The locale is passed explicitly by every test here. Production passes nothing
// and gets the reader's own — pinning it in production would be the `DatePipe`
// bug (`LOCALE_ID` is provided nowhere, so `DatePipe` silently renders `en-US`
// for every reader on earth) reintroduced by hand.
const AMERICAN = 'en-US';

describe('credentialRegistrationDate', () => {
  it('runs in a zone where a UTC-day implementation can be caught', () => {
    // Arrange
    const instant = new Date(Date.UTC(2026, 2, 11, 22));

    // Act
    const offset = instant.getTimezoneOffset();

    // Assert
    // Not a test of credentialRegistrationDate — a test of this file's ability
    // to test it. At offset 0 the reader's calendar day and the UTC day are the
    // same day for every instant, so every other assertion below goes green on
    // an implementation that formats the stored instant in UTC.
    expect(offset).not.toBe(0);
  });

  it('renders the date the reader had when the credential was registered', () => {
    // Arrange
    // 22:00 UTC on the 11th is already the 12th for a reader at UTC+14.
    const storedInstant = '2026-03-11T22:00:00Z';

    // Act
    const rendered = credentialRegistrationDate(storedInstant, AMERICAN);

    // Assert
    expect(rendered).toBe('March 12, 2026');
  });

  it('renders one date for two instants on a single local day', () => {
    // Arrange
    // Different UTC days — the 11th and the 12th — but one local day for a
    // reader at UTC+14: 12:00 and 16:00 on the 12th.
    const lateOnTheEleventh = '2026-03-11T22:00:00Z';
    const earlyOnTheTwelfth = '2026-03-12T02:00:00Z';

    // Act
    const first = credentialRegistrationDate(lateOnTheEleventh, AMERICAN);
    const second = credentialRegistrationDate(earlyOnTheTwelfth, AMERICAN);

    // Assert
    // An implementation formatting the stored instant in UTC — `slice(0, 10)`
    // on the wire string, `getUTCDate`, `toISOString` — renders these as two
    // different days and tells the reader a credential was registered on a
    // date they never lived through.
    expect(first).toBe('March 12, 2026');
    expect(second).toBe('March 12, 2026');
  });

  it('renders two dates for two instants that share the UTC day', () => {
    // Arrange
    // The mirror of the case above, and the half it cannot cover: one UTC day,
    // two local days for a reader at UTC+14 — 23:00 on the 11th and 01:00 on
    // the 12th.
    const beforeLocalMidnight = '2026-03-11T09:00:00Z';
    const afterLocalMidnight = '2026-03-11T11:00:00Z';

    // Act
    const first = credentialRegistrationDate(beforeLocalMidnight, AMERICAN);
    const second = credentialRegistrationDate(afterLocalMidnight, AMERICAN);

    // Assert
    // Without this half, an implementation that collapsed every instant to a
    // single constant — or to the same day for every input — passes the test
    // above by rendering "March 12, 2026" twice.
    expect(first).toBe('March 11, 2026');
    expect(second).toBe('March 12, 2026');
  });

  it('renders the date in words rather than as a stamp', () => {
    // Arrange
    const storedInstant = '2026-01-12T08:30:00Z';

    // Act
    const rendered = credentialRegistrationDate(storedInstant, AMERICAN);

    // Assert
    // A row is a record of what is attached to an account, read by a person.
    // `2026-01-12` is the stored value shown raw; it is also what the `<time>`
    // element's `datetime` attribute already carries for machines.
    expect(rendered).toBe('January 12, 2026');
    expect(rendered).not.toMatch(/\d{4}-\d{2}-\d{2}/);
  });

  it('renders the date in the locale it is given', () => {
    // Arrange
    const storedInstant = '2026-03-11T22:00:00Z';

    // Act
    const british = credentialRegistrationDate(storedInstant, 'en-GB');

    // Assert
    // Control for every assertion above: they all pass 'en-US', so an
    // implementation that hard-coded that locale — which is precisely the
    // `DatePipe` defect this module exists to avoid — would satisfy all of
    // them. The same instant, the same day, a different reader's convention.
    expect(british).toBe('12 March 2026');
  });
});
