// The rules a credential row is composed by, tested where they are rather than
// through the DOM the component renders them into.
//
// Two of them are only observable here at all. One is a claim about a kind this
// application does not ship — the accessible name and the visible caption
// cannot drift apart — which through the component can only be exercised
// against today's single revocable kind, whose caption happens to be the word
// that was hard-coded. The other is a claim about a kind no *version* of this
// application ships, because it arrives from a server the deployed bundle is
// older than.
import { describe, expect, it } from 'vitest';
import type { CredentialSummary } from '@app-core/api/me-api.service';
import {
  revokeLabelFor,
  toCredentialRow,
  type KindPresentation,
} from './credential-row';

const PASSKEY: CredentialSummary = {
  id: '019f4c0a-0000-7000-8000-0000000000a1',
  type: 'passkey',
  createdAtUtc: '2026-03-11T22:00:00Z',
};
// The day a reader at the pinned test zone has for that instant: 22:00Z on the
// 11th is already the 12th at UTC+14.
const PASSKEY_DATE = 'March 12, 2026';

describe('the accessible name of a revoke control', () => {
  it('names the row by the same word the row shows', () => {
    // Arrange
    // A revocable kind whose caption is not `Registered`. Nothing ships one
    // today — passkeys are the only revocable kind and they are captioned
    // `Registered` — which is exactly why the divergence cannot be seen through
    // the screen and has to be stated here. A recovery-code set is captioned
    // `Generated` and is unrevocable; a kind that is both is one product
    // decision away, and the decision is not supposed to also be a defect.
    const revocableSet: KindPresentation = {
      label: 'Recovery codes',
      dateCaption: 'Generated',
      revocable: true,
    };

    // Act
    const name = revokeLabelFor(revocableSet, 'February 2, 2026');

    // Assert
    // The screen reads `Generated February 2, 2026` and the screen reader has
    // to say the same thing. A hard-coded `registered` describes the control by
    // a word the sighted reader is not looking at — on the one action that
    // cannot be undone, where reaching the wrong control is unrecoverable.
    expect(name).toBe('Revoke Recovery codes, generated February 2, 2026');
    expect(name).not.toContain('registered');
  });

  it('still reads the way it always has for a passkey', () => {
    // Arrange
    // Control for the test above, and the reason it is safe: `Registered`
    // lower-cases to the literal that was there, so nothing any reader hears
    // today changes. A fix that altered this string would be a change to every
    // revoke control that exists.
    const passkey: KindPresentation = {
      label: 'Passkey',
      dateCaption: 'Registered',
      revocable: true,
    };

    // Act
    const name = revokeLabelFor(passkey, PASSKEY_DATE);

    // Assert
    expect(name).toBe('Revoke Passkey, registered March 12, 2026');
  });

  it('drops the clause it cannot fill rather than ending mid-sentence', () => {
    // Arrange
    const passkey: KindPresentation = {
      label: 'Passkey',
      dateCaption: 'Registered',
      revocable: true,
    };

    // Act
    const name = revokeLabelFor(passkey, '');

    // Assert
    // `Revoke Passkey, registered ` is read out exactly as written.
    expect(name).toBe('Revoke Passkey');
  });

  it('is absent on a row nothing can ever revoke', () => {
    // Arrange
    const unrevocable: KindPresentation = {
      label: 'Google',
      dateCaption: 'Registered',
      revocable: false,
    };

    // Act
    const name = revokeLabelFor(unrevocable, PASSKEY_DATE);

    // Assert
    // Null, not a name for a disabled control: the template renders no button
    // at all, because a control that will never be enabled promises a release
    // that is not coming.
    expect(name).toBeNull();
  });
});

describe('a row composed from a kind this bundle does not know', () => {
  // Constructing these needs an assertion, and the assertion is the subject
  // rather than a workaround: `CredentialKind` is closed over what *this
  // source* knows, which is a compile-time guarantee about our code and says
  // nothing about what a deployed bundle is sent. A browser holding yesterday's
  // bundle against today's API is the ordinary way this arrives.
  function credentialOfKind(type: string): CredentialSummary {
    return {
      id: '019f4c0a-0000-7000-8000-0000000000f6',
      type,
      createdAtUtc: '2026-03-11T22:00:00Z',
    } as unknown as CredentialSummary;
  }

  it('is composed rather than thrown over', () => {
    // Arrange
    const unknown = credentialOfKind('sms_one_time_code');

    // Act
    const row = toCredentialRow(unknown);

    // Assert
    // Totality is a requirement of where this runs, not a preference: the
    // caller composes rows inside a `computed` the template reads, Angular
    // caches a throw on the signal and rethrows it on every later read, and no
    // `try` can be put around a signal read. One unrecognised kind took the
    // list, the recovery count, the export and the erasure control off the
    // screen for the rest of the visit.
    expect(row.type).toBe('Sign-in method');
    expect(row.type).not.toContain('sms_one_time_code');
    // The day is still the reader's own, through the one shared formatter: not
    // knowing what a credential is called says nothing about when it arrived.
    expect(row.registeredOn).toBe(PASSKEY_DATE);
    expect(row.storedInstant).toBe('2026-03-11T22:00:00Z');
    // But not a caption claiming which of two things happened.
    expect(row.dateCaption).toBe('Added');
    // And no action. Unknown is the row nobody has decided about, and
    // revocation is the one unrecoverable act on this screen.
    expect(row.revokeLabel).toBeNull();
  });

  it('is composed the same way for a kind every object already answers to', () => {
    // Arrange
    // The negative control the plain-object lookup fails. `MAP['constructor']`
    // does not miss — it *hits*, on `Object.prototype.constructor` — so a
    // fallback written as `MAP[type] ?? UNKNOWN` never fires and the row is
    // handed a function whose `label` is undefined. The row then renders blank
    // rather than neutrally, and the guard looks correct everywhere else it is
    // tested.
    const inherited = [
      'constructor',
      'toString',
      '__proto__',
      'hasOwnProperty',
    ];

    // Act
    const rows = inherited.map((type) =>
      toCredentialRow(credentialOfKind(type)),
    );

    // Assert
    for (const row of rows) {
      expect(row.type).toBe('Sign-in method');
      expect(row.dateCaption).toBe('Added');
      expect(row.revokeLabel).toBeNull();
    }
  });

  it('leaves the kinds it does know exactly as they were', () => {
    // Arrange, Act
    // Control for both tests above: a `presentationFor` that returned the
    // fallback for everything satisfies each of them, and would render every
    // credential on the screen as an unnamed sign-in method with no action.
    const row = toCredentialRow(PASSKEY);

    // Assert
    expect(row.type).toBe('Passkey');
    expect(row.dateCaption).toBe('Registered');
    expect(row.revokeLabel).toBe('Revoke Passkey, registered March 12, 2026');
  });
});
