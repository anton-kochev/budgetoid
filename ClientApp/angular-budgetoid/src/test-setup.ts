// The runner's local time zone is pinned away from UTC so that
// `src/app/settings/export-filename.spec.ts` can do its job at all. That spec
// proves `exportFilename` reads the UTC getters rather than the local ones by
// running two instants either side of midnight UTC, and that pair only
// discriminates on a host whose local zone is not UTC. CI is `ubuntu-latest`,
// whose `TZ` is UTC — so without this line the guard is disabled on precisely
// the machine that gates merges, and a local-getter implementation sails
// through it.
//
// `Pacific/Kiritimati` is UTC+14, the largest standard offset in the database,
// and it keeps no daylight saving, so the pin cannot drift mid-year and make an
// expected filename depend on which half of the year the suite ran in.
//
// The spec `runs in a zone where a local-getter implementation can be caught`
// asserts `getTimezoneOffset()` is not `0`. That is the guard on this guard: it
// goes red the day someone deletes this file.
//
// The one other time-zone-sensitive spec,
// `src/app/transactions/transactions.component.spec.ts`, builds a local `Date`
// and its component serializes with local getters, so it is zone-invariant and
// this pin does not disturb it.
//
// This file must not be named `*.spec.ts`, or the runner collects it as a test
// file containing no tests.
// Indexed rather than dotted because `noPropertyAccessFromIndexSignature` is
// on and `TZ` reaches `process.env` through its index signature.
process.env['TZ'] = 'Pacific/Kiritimati';
