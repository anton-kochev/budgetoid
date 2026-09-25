// The canonical form of a recovery code: the exact text every derivation off a
// code is built from, owned here and imported by each of them.
//
// It has a module of its own because it now has more than one caller. The rule
// shipped as a private function inside `recovery-codes.ts`, where the verifier
// branch was the only thing that folded a typed-back code — and while that was
// true, keeping it private was the stronger position: one caller, one place for
// the rule to live, no second place for it to drift. What changed is that the
// account's key-encryption key is derived from the same recovery code on an
// independent HKDF branch, and it has to fold that code to the identical text.
// If the two derivations disagree by so much as a stripped hyphen, a code that
// redeems fine unwraps nothing, or the reverse; both symptoms are silent and
// both arrive long after the typing that caused them.
//
// One definition imported twice is the only shape that keeps "the two
// derivations agree" a fact rather than a hope. The alternatives are worse in
// the same direction: exporting it from `recovery-codes.ts` would make that
// module's public surface answer for a rule the key branch depends on, and
// adding the key derivation *into* `recovery-codes.ts` is refused there in
// writing. So the rule moves down to where both branches can reach it without
// either owning it.
//
// Nothing here is a service and nothing here is injected. There is no state, no
// configuration and no dependency, so a function is the whole of it.

// The decoding half of the alphabet's own decision, and it lives here because
// here is the only place a code is ever turned into anything.
//
// Excluding `I`, `L` and `O` from the *draw* protects nobody on its own: it
// means no code contains them, not that nobody types them. The exclusion is a
// statement that a reader resolves those glyphs as `1`, `1` and `0` — so the
// derivation has to resolve them the same way, or the confusion the alphabet
// was chosen to avoid comes back on the only path that matters. Leaving this to
// a future caller is worse than leaving it out: a redemption screen would have
// to rediscover which characters fold into which, and the symptom of getting it
// wrong is a `401` that is deliberately indistinguishable from a wrong code —
// the only way back into the account, looking broken, saying nothing.
//
// Each rule is the inverse of an exclusion the alphabet already made, so none
// of them can fold two *codes* together: no minted code contains a lowercase
// letter, an `I`, an `L`, an `O`, a space or a hyphen, which is exactly why
// this is the identity on everything the generator produces and why no verifier
// already derived can move. `U` is excluded too and is deliberately **not**
// mapped — it is excluded so a draw cannot spell an obscenity, not because it
// is read back as something else, and a rule generous enough to rescue every
// typo would quietly shrink the code's 130 bits.
//
// `toUpperCase`, never `toLocaleUpperCase`: the latter maps `i` to `İ` under a
// Turkish locale, which would make the same typed code derive different
// verifiers on two phones.
export function canonicalRecoveryCode(code: string): string {
  return code
    .toUpperCase()
    .replace(/[\s-]/g, '')
    .replace(/[IL]/g, '1')
    .replace(/O/g, '0');
}
