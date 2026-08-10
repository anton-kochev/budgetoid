import { describe, expect, it } from 'vitest';
import { exportFilename } from './export-filename';

// The runner's time zone is pinned away from UTC, because on a host sitting at
// exactly UTC — which is what `ubuntu-latest` gives CI — an implementation
// reading `getFullYear`/`getHours` instead of the UTC getters produces the
// right answer for every instant, and this file measures nothing precisely
// where it gates merges. The first test below is the guard on that guard.
//
// The two straddling instants stay. They document the failure the offset
// produces in both directions and remain the clearest statement of it: the
// late-evening case rolls forward into the next UTC day on any host ahead of
// UTC, the early-morning case rolls back into the previous one on any host
// behind it, so the pair pins the boundary whatever sign the pinned zone has.
describe('exportFilename', () => {
  it('runs in a zone where a local-getter implementation can be caught', () => {
    // Arrange
    const instant = new Date(Date.UTC(2026, 7, 9, 12));

    // Act
    const offset = instant.getTimezoneOffset();

    // Assert
    // Not a test of exportFilename — a test of this file's ability to test it.
    // At offset 0 the local getters and the UTC getters agree on every field,
    // so every other assertion here goes green on an implementation that reads
    // the wrong ones.
    expect(offset).not.toBe(0);
  });

  it('names the file for the instant in UTC', () => {
    // Arrange
    const instant = new Date(Date.UTC(2026, 7, 9, 23, 30, 15));

    // Act
    const filename = exportFilename(instant);

    // Assert
    expect(filename).toBe('budgetoid-export-20260809T233015Z.json');
  });

  it('names the file for an instant early in the UTC day', () => {
    // Arrange
    const instant = new Date(Date.UTC(2026, 7, 9, 0, 30, 15));

    // Act
    const filename = exportFilename(instant);

    // Assert
    // Control for the case above, in the opposite direction: an
    // implementation reading getFullYear/getHours instead of the UTC getters
    // rolls this instant back into 2026-08-08 on any host behind UTC, while
    // the late-evening case rolls forward into 2026-08-10 on any host ahead
    // of it. Either half alone is green somewhere.
    expect(filename).toBe('budgetoid-export-20260809T003015Z.json');
  });

  it('pads every single-digit field', () => {
    // Arrange
    const instant = new Date(Date.UTC(2026, 0, 3, 4, 5, 7));

    // Act
    const filename = exportFilename(instant);

    // Assert
    // Both instants above sit at minute 30 and second 15, and in months and
    // days that are already two digits, so an implementation assembling the
    // stamp from UTC getters and forgetting `padStart` passes them and emits
    // `budgetoid-export-2026137Z.json` here. Green today — today's
    // implementation goes through `toISOString`, which always pads — and kept
    // as the guard on the day someone hand-rolls the format.
    expect(filename).toBe('budgetoid-export-20260103T040507Z.json');
  });
});
