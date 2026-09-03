// The mapper's own spec, and it stands the mapper up with two lines and no
// `TestBed` — which is the whole reason `toAccountView` takes a
// {@link NarrativeOpener} rather than the service that owns one.
import type { AccountDto } from '@app-core/api/account-api.service';
import { NarrativeFieldMisuseError } from '@app-core/security/narrative-cipher';
import type {
  NarrativeOpener,
  NarrativeText,
} from '@app-core/security/narrative-text';
import { describe, expect, it } from 'vitest';
import { toAccountView } from './account-view';

// A canonical lower-case hyphenated UUID — the one spelling the codec accepts,
// and the spelling `System.Text.Json` renders every `Guid` in, so this is what a
// read really hands the client back.
const ROW_ID = '0199c3d4-5f6a-7b8c-9d0e-1f2a3b4c5d6e';

const sealedAccount: AccountDto = {
  id: ROW_ID,
  name: 'AQIDBAUGBwgJCgsMDQ4PEA',
  type: 'Savings',
  openingBalance: 125.5,
  createdAtUtc: '2026-01-02T03:04:05Z',
  currencyCode: 'EUR',
  currencyName: 'Euro',
  currencySymbol: '€',
  currencyMinorUnit: 2,
};

// Records what it was asked, so the binding can be asserted rather than
// inferred from an answer.
function recordingOpener(answer: NarrativeText): {
  readonly open: NarrativeOpener;
  readonly calls: { binding: unknown; wire: string }[];
} {
  const calls: { binding: unknown; wire: string }[] = [];

  return {
    calls,
    open: (binding, wire) => {
      calls.push({ binding, wire });

      return Promise.resolve(answer);
    },
  };
}

describe('toAccountView', () => {
  it('opens the name under the accounts name binding for the row’s own id', async () => {
    // Arrange
    const opener = recordingOpener({ state: 'text', value: 'Everyday' });

    // Act
    const view = await toAccountView(sealedAccount, opener.open);

    // Assert
    expect(opener.calls).toEqual([
      {
        binding: { table: 'accounts', column: 'name', rowId: ROW_ID },
        wire: sealedAccount.name,
      },
    ]);
    expect(view.name).toEqual({ state: 'text', value: 'Everyday' });
  });

  it('carries type, balance and currency through untouched', async () => {
    // Arrange
    const opener = recordingOpener({ state: 'text', value: 'Everyday' });

    // Act
    const view = await toAccountView(sealedAccount, opener.open);

    // Assert
    expect(view).toEqual({
      createdAtUtc: '2026-01-02T03:04:05Z',
      currencyCode: 'EUR',
      currencyMinorUnit: 2,
      currencyName: 'Euro',
      currencySymbol: '€',
      id: ROW_ID,
      name: { state: 'text', value: 'Everyday' },
      openingBalance: 125.5,
      type: 'Savings',
    });
  });

  it('answers locked when the opener answers locked', async () => {
    // Arrange
    const opener = recordingOpener({ state: 'locked' });

    // Act
    const view = await toAccountView(sealedAccount, opener.open);

    // Assert — the word, never `''` and never a dash. A mapper that collapsed
    // it would have the screen claim something about the account when the truth
    // is about this tab.
    expect(view.name).toEqual({ state: 'locked' });
  });

  it('answers unreadable when the opener answers unreadable', async () => {
    // Arrange
    const opener = recordingOpener({ state: 'unreadable' });

    // Act
    const view = await toAccountView(sealedAccount, opener.open);

    // Assert
    expect(view.name).toEqual({ state: 'unreadable' });
  });

  it('lets a misuse rejection propagate instead of reporting unreadable', async () => {
    // Arrange — the codec's word for a refusal it made about the *call*. It
    // says nothing about what is stored in the column, so turning it into
    // `unreadable` would put a sentence about damaged text in front of somebody
    // who can do nothing about it, over a row that is perfectly fine.
    const refused: NarrativeOpener = () =>
      Promise.reject(new NarrativeFieldMisuseError('refused'));

    // Act
    const mapping = toAccountView(sealedAccount, refused);

    // Assert
    await expect(mapping).rejects.toBeInstanceOf(NarrativeFieldMisuseError);
  });
});
