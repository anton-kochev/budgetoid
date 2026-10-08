// The typed word in the erasure dialog. See docs/design/components.md, "Erasure
// dialog", *The typed word*.
//
// **A check on intent and not a security control.** The server never sees the
// word and enforces nothing about it — what it enforces is the passkey — so the
// rule lives here as product policy, per ADR 0002, and nothing may describe it
// as protecting the account.
//
// **Exactly as wide as the book says, and no wider.** Trimmed, because a phone
// keyboard pads with a space and a paste carries a newline; case-insensitive,
// because a keyboard that capitalises the first letter anyway is the person
// having done what was asked. Then equality — never `startsWith` or `includes`,
// each of which accepts a prefix or the field's own label pasted back.
//
// One predicate with two readers: the dialog's commit attribute (through
// `ErasureFlowService.pressable`) and the flow's own guard. A second spelling
// of the match in either would let the attribute and the handler disagree.

/** The word the field's label asks for, and the only word the match accepts. */
export const ERASURE_CONFIRMATION_WORD = 'erase';

/** Whether what somebody typed is the confirmation word. */
export function confirmsErasure(typed: string): boolean {
  // `toLowerCase`, locale-free: the word is fixed ASCII and the match should
  // not depend on the reader's locale.
  return typed.trim().toLowerCase() === ERASURE_CONFIRMATION_WORD;
}
