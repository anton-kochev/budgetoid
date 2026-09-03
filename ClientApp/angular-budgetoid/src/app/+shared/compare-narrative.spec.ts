import type { NarrativeText } from '@app-core/security/narrative-text';
import { describe, expect, it } from 'vitest';

import { compareNarrative } from './compare-narrative';

// One value of each word, built once and shared, because none of them carries
// state a case could disturb.
const TEXT: NarrativeText = { state: 'text', value: 'Ana' };
const UNREADABLE: NarrativeText = { state: 'unreadable' };
const LOCKED: NarrativeText = { state: 'locked' };

describe('compareNarrative', () => {
  it('orders text before unreadable before locked', () => {
    // Arrange
    // Arriving in exactly the reverse of the wanted order, so a comparator that
    // answered a constant — or one that left the array alone — cannot pass by
    // the luck of how the fixture was written down.
    const arrived = [LOCKED, UNREADABLE, TEXT];

    // Act
    const ordered = [...arrived].sort(compareNarrative);

    // Assert
    expect(ordered.map((value) => value.state)).toEqual([
      'text',
      'unreadable',
      'locked',
    ]);

    // **Both directions of all three pairs**, which is the half a sort cannot
    // say. A comparator answering `-1` to everything sorts this array into the
    // right shape and is not an order at all; asking each pair the other way
    // round is what refuses it.
    expect(compareNarrative(TEXT, UNREADABLE)).toBeLessThan(0);
    expect(compareNarrative(UNREADABLE, TEXT)).toBeGreaterThan(0);
    expect(compareNarrative(UNREADABLE, LOCKED)).toBeLessThan(0);
    expect(compareNarrative(LOCKED, UNREADABLE)).toBeGreaterThan(0);
    expect(compareNarrative(TEXT, LOCKED)).toBeLessThan(0);
    expect(compareNarrative(LOCKED, TEXT)).toBeGreaterThan(0);
  });

  it('orders two opened names by localeCompare', () => {
    // Arrange
    const ana: NarrativeText = { state: 'text', value: 'Ana' };
    const bo: NarrativeText = { state: 'text', value: 'Bo' };

    // Act, Assert
    expect(compareNarrative(ana, bo)).toBeLessThan(0);
    expect(compareNarrative(bo, ana)).toBeGreaterThan(0);
    expect(compareNarrative(ana, { state: 'text', value: 'Ana' })).toBe(0);

    // **The half that tells `localeCompare` from a code-unit `<`**, which is
    // the comparison a reader reaches for first and which is wrong for every
    // name a person types with a capital in it. Under `<` every upper-case
    // letter precedes every lower-case one, so `Banana` sorts above `apple`
    // and a payee list reads as two alphabets stacked on top of each other.
    const apple: NarrativeText = { state: 'text', value: 'apple' };
    const banana: NarrativeText = { state: 'text', value: 'Banana' };

    expect(compareNarrative(apple, banana)).toBeLessThan(0);
  });

  it('returns zero for two locked values so a stable sort keeps arrival order', () => {
    // Arrange
    // **Three, not two, and told apart by identity alone.** The `locked` member
    // carries no payload, so the only thing a stable sort can be observed to
    // preserve is which object arrived first — and a two-element array is
    // ordered correctly by half the comparators that are not stable at all.
    const first: NarrativeText = { state: 'locked' };
    const second: NarrativeText = { state: 'locked' };
    const third: NarrativeText = { state: 'locked' };
    const arrived = [first, second, third];

    // Act
    const ordered = [...arrived].sort(compareNarrative);

    // Assert
    // Zero, exactly — not merely "not positive". A comparator answering `-0`,
    // or answering `0` only for the pair this case happens to ask about, is a
    // different function.
    expect(compareNarrative(first, second)).toBe(0);
    expect(compareNarrative(second, first)).toBe(0);
    expect(compareNarrative(LOCKED, LOCKED)).toBe(0);

    // The arrival order, by identity. `toEqual` would pass over any three
    // `{ state: 'locked' }` objects in any order at all, because they are
    // indistinguishable by value — which is the whole reason this rule needs
    // `toBe` on each position.
    expect(ordered[0]).toBe(first);
    expect(ordered[1]).toBe(second);
    expect(ordered[2]).toBe(third);
  });
});
