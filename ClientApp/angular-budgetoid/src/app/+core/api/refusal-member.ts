// The `refusal` member of a problem document, read the one strict way.
//
// **Shared by the two callers that tell a declined passkey from every other
// `401` by this member alone** — the erasure cancellation and the rotation
// begin — and the sentence each then renders says the gate judged the passkey.
// A reader that folded case, trimmed, or found the name through the prototype
// would hand that sentence to a body the gate never wrote, and one function
// for both is what keeps them from disagreeing about it silently.
//
// **It is not yet the only reader of the member.** `email-change-flow.service.ts`
// and `release-flow.service.ts` each keep a lenient copy that finds it with
// `in`, so through the prototype — the first for the same `assertion` word, the
// second for `no_account`. Moving them onto this one is known work, not done.
//
// **In `+core/api`, beside the other wire readers**, because it reads the API's
// body and depends on nothing; the rotation driver in `+core` must not import
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
