// The row states *the reader's own calendar day*, computed from the stored
// instant in the reader's zone — never the UTC day. Those are different days for
// fourteen hours out of every twenty-four at UTC+14 and for ten at UTC-10, so an
// implementation that reads the UTC day is wrong for most of the planet most of
// the time while looking right to whoever wrote it.
//
// `Intl.DateTimeFormat`, not `DatePipe`: nothing in this application provides
// `LOCALE_ID`, so `DatePipe` falls back to `en-US` and silently renders every
// reader's date in American convention. The locale is a parameter with no
// default so the specs can be deterministic without production being pinned —
// passing `undefined` is what asks the runtime for the reader's own, and that is
// what the caller does.
//
// The stored instant arrives as the wire string rather than as a `Date` so the
// single conversion from "what the server holds" to "what this reader sees"
// happens here, where it is tested. A `Date` parameter would move `new Date(…)`
// into a caller and let a `Date` assembled from local getters reach a formatter
// that trusts it.
// **Total, and that is the load-bearing part.** The empty string is the answer
// for a stored instant this cannot read, because the only alternative available
// to it is a throw, and a throw here is not one bad row: the caller formats
// inside a `computed` that the template reads, so a `RangeError` escapes during
// change detection and Angular abandons the pass — every section declared after
// the credential list stops rendering for the rest of the visit, and no `try`
// can be put around a signal read to contain it. Every *network* failure on this
// screen is already caught and said in a sentence; a 200 carrying one malformed
// field was the only path with nothing between it and the template.
export function credentialRegistrationDate(
  storedInstant: string,
  locale?: string,
): string {
  // Two ways a payload defeats the formatter, and the second is not covered by
  // the first. An unparseable string yields an Invalid Date, whose `getTime()`
  // is NaN and whose formatting throws. A JSON `null` — which the declared type
  // forbids and the wire does not, since nothing between the server and here
  // validates the DTO — yields the *epoch*, which formats perfectly well as
  // `December 31, 1969`: a fabricated date the reader has no way to disbelieve,
  // which is worse than a blank. The guard is on the type, not on the value, so
  // a genuine epoch string still renders.
  if (typeof storedInstant !== 'string') {
    return '';
  }

  const instant = new Date(storedInstant);

  if (Number.isNaN(instant.getTime())) {
    return '';
  }

  // Day, month and year named one by one rather than through `dateStyle`,
  // because the requirement is an absolute date with no time on it and this
  // states that directly. The order and the separators still belong to the
  // locale — `en-GB` renders the same three fields as `12 March 2026`.
  return new Intl.DateTimeFormat(locale, {
    day: 'numeric',
    month: 'long',
    year: 'numeric',
  }).format(instant);
}
