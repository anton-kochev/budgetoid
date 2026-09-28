// The typed word in the erasure dialog, and the one rule it carries: trimmed,
// case-insensitive, and nothing wider. See docs/design/components.md, "Erasure
// dialog", *The typed word*.
//
// It is a check on intent and not a security control — the server never sees
// the word — so it lives in the client as product policy. What this file holds
// is that the policy is exactly as wide as the book says: a phone that added a
// trailing space or capitalised the first letter is the person having done what
// was asked, and a prefix, a near miss or a longer phrase is not.
import { describe, expect, it } from 'vitest';
import {
  ERASURE_CONFIRMATION_WORD,
  confirmsErasure,
} from './erasure-confirmation';

describe('confirmsErasure', () => {
  it('asks for the word the field label names', () => {
    // Assert
    // The label reads *Type erase to confirm*, and the constant is the one
    // place both the label and the match are meant to come from. A constant
    // spelled `Erase` or `erase everything` would make every accepted case
    // below a lie about the label.
    expect(ERASURE_CONFIRMATION_WORD).toBe('erase');
  });

  it.each([
    { label: 'the word as asked', typed: 'erase' },
    { label: 'a phone keyboard’s padding and capital', typed: ' Erase ' },
    { label: 'caps lock', typed: 'ERASE' },
    { label: 'a trailing newline from a paste', typed: 'erase\n' },
  ])('accepts $label', ({ typed }) => {
    // Act
    const confirmed = confirmsErasure(typed);

    // Assert
    expect(confirmed).toBe(true);
  });

  it.each([
    { label: 'nothing typed', typed: '' },
    { label: 'only spaces', typed: '   ' },
    { label: 'a prefix', typed: 'eras' },
    { label: 'the past tense', typed: 'erased' },
    { label: 'a longer phrase that contains it', typed: 'erase everything' },
    { label: 'the label copied whole', typed: 'Type erase to confirm' },
    { label: 'a different verb', typed: 'delete' },
    { label: 'a space inside the word', typed: 'er ase' },
  ])('refuses $label', ({ typed }) => {
    // Act
    const confirmed = confirmsErasure(typed);

    // Assert
    // A `startsWith`, an `includes` or a `trim().length > 0` passes the accept
    // cases above and fails one of these — which is why there is a refusal for
    // each of those shapes rather than only for the obvious wrong word.
    expect(confirmed).toBe(false);
  });
});
