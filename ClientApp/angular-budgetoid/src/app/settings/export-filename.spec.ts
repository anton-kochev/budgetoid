import { describe, expect, it } from 'vitest';
import { exportFilename } from './export-filename';

// The two cases below straddle midnight UTC on purpose. A host time zone is
// not pinned for the runner — src/app/transactions/transactions.component.spec
// builds a local-zone Date and would have to be re-checked — so the pair is
// what catches a local-getter implementation instead: the late-evening case
// reds on any host ahead of UTC, the early-morning case on any host behind it.
// The gap, stated rather than papered over: on a host sitting at exactly UTC
// both cases pass whatever the implementation does.
describe('exportFilename', () => {
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
});
