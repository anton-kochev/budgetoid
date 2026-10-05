// The `refusal` member of a problem document, read the one strict way.
//
// **One reader, because two would disagree silently.** The erasure cancellation
// and the rotation begin each tell a declined passkey from every other `401` by
// this member alone, and the sentence each then renders says the gate judged
// the passkey. A reader that folded case, trimmed, or found the name through
// the prototype would hand that sentence to a body the gate never wrote — and a
// second copy of this function is how one caller comes to do that while the
// other does not.
//
// **In `+core/api`, beside the other wire readers**, because it reads the API's
// body and depends on nothing; the rotation driver in `+core` cannot import
// from a feature folder.

/**
 * The body's **own** `refusal` member, or `undefined` for a body without one.
 *
 * A member inherited through the prototype, a prototype key every object
 * answers (`toString`), and a body that is not an object all read as
 * `undefined`. The value comes back as it arrived and the caller compares it
 * exactly, so an array that merely stringifies to the word never equals it.
 */
export function ownRefusalOf(body: unknown): unknown {
  // The `in` test narrows the type; `Object.hasOwn` is the one that decides.
  return typeof body === 'object' &&
    body !== null &&
    'refusal' in body &&
    Object.hasOwn(body, 'refusal')
    ? body.refusal
    : undefined;
}
