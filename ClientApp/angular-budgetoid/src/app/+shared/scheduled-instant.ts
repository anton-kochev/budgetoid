// A scheduled erasure's instant as the reader's clock reads it. One module so
// the release screen and the shell's notice cannot disagree about one instant.

/** The scheduled instant as it arrived, and as the reader's clock reads it. */
export interface ScheduledInstant {
  /** The server's string, unchanged — what `<time datetime>` carries. */
  readonly wire: string;
  readonly date: string;
  readonly time: string;
}

/**
 * Reads the server's instant in the reader's own zone and locale: the day is
 * the reader's calendar day, never the UTC one, and the date is absolute, never
 * a countdown that goes false on tomorrow's load. `Intl.DateTimeFormat` rather
 * than `DatePipe`, because nothing provides `LOCALE_ID` and the pipe would pin
 * every date to `en-US` — the credential list's rule.
 *
 * The decoders behind both writers of the schedule refuse an instant without an
 * offset, so `Date` never reads one as local time. An instant that still does
 * not parse answers `null`, which a caller renders as nothing scheduled rather
 * than an `Invalid Date` string.
 */
export function readScheduledInstant(wire: string): ScheduledInstant | null {
  const instant = new Date(wire);

  if (Number.isNaN(instant.getTime())) {
    return null;
  }

  return {
    wire,
    date: new Intl.DateTimeFormat(undefined, {
      day: 'numeric',
      month: 'long',
      year: 'numeric',
    }).format(instant),
    time: new Intl.DateTimeFormat(undefined, {
      hour: 'numeric',
      minute: '2-digit',
    }).format(instant),
  };
}
