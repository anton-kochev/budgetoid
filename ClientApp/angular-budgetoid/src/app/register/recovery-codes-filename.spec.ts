import { describe, expect, it } from 'vitest';
import { recoveryCodesFilename } from './recovery-codes-filename';

// A mirror of `settings/export-filename.spec.ts`, guard included, because the
// two modules have the same shape and fail the same way. The guard is not
// ceremony copied along with the rest: the runner's time zone is pinned away
// from UTC in `src/test-setup.ts`, and on a host sitting at exactly UTC — which
// is what `ubuntu-latest` gives CI — an implementation reading
// `getFullYear`/`getHours` instead of the UTC getters produces the right answer
// for every instant, so this file would measure nothing precisely where it
// gates merges.
//
// The two straddling instants are what the offset buys: the late-evening case
// rolls forward into the next UTC day on any host ahead of UTC, the
// early-morning case rolls back into the previous one on any host behind it, so
// the pair pins the boundary whatever sign the pinned zone has.
//
// Why this file exists at all rather than a parameter on `exportFilename`: the
// two names share a stamp and share nothing else. This one has a different
// stem, a different extension and a different subject — a plain-text list of
// secrets rather than a JSON document — and folding them into one function with
// a discriminant would make a change to either name a change to both.
describe('recoveryCodesFilename', () => {
  it('runs in a zone where a local-getter implementation can be caught', () => {
    // Arrange
    const instant = new Date(Date.UTC(2026, 7, 9, 12));

    // Act
    const offset = instant.getTimezoneOffset();

    // Assert
    // Not a test of recoveryCodesFilename — a test of this file's ability to
    // test it. At offset 0 the local getters and the UTC getters agree on every
    // field, so every other assertion here goes green on an implementation that
    // reads the wrong ones.
    expect(offset).not.toBe(0);
  });

  it('names the file for the instant in UTC', () => {
    // Arrange
    const instant = new Date(Date.UTC(2026, 7, 9, 23, 30, 15));

    // Act
    const filename = recoveryCodesFilename(instant);

    // Assert
    // The whole name, not a prefix match: the stem is what tells this file from
    // the export beside it in a downloads folder, and the extension is what
    // makes a double-click open something a person can read the codes out of.
    expect(filename).toBe('budgetoid-recovery-codes-20260809T233015Z.txt');
  });

  it('names the file for an instant early in the UTC day', () => {
    // Arrange
    const instant = new Date(Date.UTC(2026, 7, 9, 0, 30, 15));

    // Act
    const filename = recoveryCodesFilename(instant);

    // Assert
    // Control for the case above, in the opposite direction: an implementation
    // reading getFullYear/getHours instead of the UTC getters rolls this
    // instant back into 2026-08-08 on any host behind UTC, while the
    // late-evening case rolls forward into 2026-08-10 on any host ahead of it.
    // Either half alone is green somewhere.
    expect(filename).toBe('budgetoid-recovery-codes-20260809T003015Z.txt');
  });

  it('pads every single-digit field', () => {
    // Arrange
    const instant = new Date(Date.UTC(2026, 0, 3, 4, 5, 7));

    // Act
    const filename = recoveryCodesFilename(instant);

    // Assert
    // Both instants above sit at minute 30 and second 15, in months and days
    // that are already two digits, so an implementation assembling the stamp
    // from the UTC getters and forgetting `padStart` passes them and emits
    // `budgetoid-recovery-codes-2026137Z.txt` here — a name that sorts wrongly
    // beside its neighbours and parses as nothing.
    expect(filename).toBe('budgetoid-recovery-codes-20260103T040507Z.txt');
  });
});
