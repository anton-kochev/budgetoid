// The name of the file a person saves their recovery codes into. It is minted
// on the client and by nothing else: no server ever sees a code, so no server
// can name a file holding ten of them.
//
// A sibling of `settings/export-filename.ts` rather than a parameter on it. The
// two share a stamp and share nothing else — a different stem, a different
// extension, and a subject that is a plain-text list of secrets rather than a
// JSON document — so folding them together behind a discriminant would make
// every change to either name a change to both. The three duplicated lines
// below are the price of that, and they are the cheaper half.
//
// What the name deliberately does not say is what is inside it. It says
// "recovery codes" because a person with a downloads folder has to be able to
// find the file again, and that is the one place this cost is paid: the
// *contents* stay bare, with no header and no product name, so a stranger
// holding the disk finds ten anonymous grouped strings rather than a labelled
// secret. `steps/codes-step.component.ts` restates that where the file is
// built, and is the caller that passes `new Date()`.
//
// The instant arrives as a parameter instead of being read from the clock in
// here, which is what makes the format testable at all — the runner's zone is
// pinned away from UTC in `src/test-setup.ts` precisely so a local-getter
// implementation is caught.
export function recoveryCodesFilename(instant: Date): string {
  // `toISOString` over assembling the six UTC getters by hand: it is UTC by
  // specification and pads every field of its own accord, so the whole
  // conversion is ISO 8601 extended to basic — drop the separators, drop the
  // fractional seconds. Reading the local getters instead would put the host's
  // offset behind the trailing `Z`, and a hand-rolled stamp that forgets
  // `padStart` emits `2026137Z`, which sorts wrongly beside its neighbours and
  // parses as nothing.
  //
  // An unrepresentable instant throws out of `toISOString` rather than
  // reaching disk as `Invalid DateZ`, which is the behaviour worth having: the
  // caller reads the clock, so a throw here means the clock is broken, and a
  // file of secrets saved under a name nobody can order is worse than a press
  // that visibly failed.
  const stamp = instant
    .toISOString()
    .replace(/[-:]/g, '')
    .replace(/\.\d+Z$/, 'Z');

  return `budgetoid-recovery-codes-${stamp}.txt`;
}
