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
export function credentialRegistrationDate(
  storedInstant: string,
  locale?: string,
): string {
  // Day, month and year named one by one rather than through `dateStyle`,
  // because the requirement is an absolute date with no time on it and this
  // states that directly. The order and the separators still belong to the
  // locale — `en-GB` renders the same three fields as `12 March 2026`.
  return new Intl.DateTimeFormat(locale, {
    day: 'numeric',
    month: 'long',
    year: 'numeric',
  }).format(new Date(storedInstant));
}
