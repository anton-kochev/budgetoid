// The client mints the saved file's name itself, in the same shape the server
// puts in its `Content-Disposition`. It does not read that header and it
// cannot: `Api/Program.cs` sets no `Access-Control-Expose-Headers`, so the
// disposition is invisible to cross-origin JavaScript — `docs/business-logic/
// export.md` records that trap because it will look like a bug in the
// endpoint. The consequence worth keeping in mind is that the two names come
// from two clocks, the browser's and the server's, so under skew they can
// differ by seconds and neither one is authoritative.
//
// The instant arrives as a parameter instead of being read from the clock in
// here, so the format can be pinned by a test without pinning the runner's
// `TZ`; `SettingsService` is what passes `new Date()`.
export function exportFilename(instant: Date): string {
  // `toISOString` is preferred over assembling the parts from the six UTC
  // getters because it is UTC by specification and needs no zero-padding of
  // its own — the whole conversion is ISO 8601 extended to basic, which is
  // dropping the separators and the fractional seconds. Reading the local
  // getters instead would put the host's offset behind the trailing Z.
  const stamp = instant
    .toISOString()
    .replace(/[-:]/g, '')
    .replace(/\.\d+Z$/, 'Z');

  return `budgetoid-export-${stamp}.json`;
}
