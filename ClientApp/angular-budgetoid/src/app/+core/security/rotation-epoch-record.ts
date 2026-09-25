// The highest rotation epoch this device has watched an account reach. Nothing
// here refuses anything; it is the memory a refusal elsewhere is built on — the
// unlock gate in `account-key-custody.service.ts` and the rotation begin in
// `key-rotation-material.ts` each read this record and turn away a response
// whose epoch is below it, beside the checks each makes on the manifest itself.
// Only custody writes it. Memory here and judgement there is what stops this
// module growing a policy: a store that also decided what to do about what it
// remembers would be the one place a rollback is judged, away from the rest of
// the response it has to be judged against.
//
// **What it is for is replay, not forgery.** An operator who can write the
// database serves back an old `(manifest, epoch)` pair the account genuinely was
// in once. It is correctly sealed, correctly framed and opens under the content
// key, because it is not fake — it is simply from before, and no amount of
// cryptography can see that. The only party able to tell is one that watched the
// account move past it, and on a browser that party is a device. Hence a
// per-device high-water mark, and hence ASM-016: a device that has never seen
// the account cannot detect a rollback at all. This narrows the window; it does
// not close it.
//
// **One key per account, never one JSON map.** A map is a read-modify-write, so
// two tabs opening two accounts lose an update between them, and the loser is
// the lower record — exactly the direction this defence cannot afford. A map is
// also a shape: an attacker who can write this store gets to choose the object a
// `JSON.parse` reader walks. A key each is a single write per account and one
// string to validate.
//
// **A throw reads as "this device has not seen the account", never as a
// refusal.** Blocked-storage modes — private browsing, a cleared origin, a quota
// — throw on the read as well as on the write. A throw escaping from here would
// turn a manifest this client can perfectly well open into a failure, and lock
// private-mode browsing out of the product. A browser that cannot remember is
// permanently in ASM-016's first-visit state, which is a state this product
// already accepts. Note that `theme.service.ts` guards only its write and reads
// bare: that asymmetry is affordable for a colour scheme and is not the pattern
// here.
//
// Nothing here is a service and nothing is injected — no state beyond the store,
// no configuration, no dependency — so two functions are the whole of it. In
// particular there is **no module-level cache**: a remembered value answers the
// first read of a tab and knows nothing about what another tab has written
// since, and the store is cheap enough that the second copy buys nothing.

// The store's own spelling, built here and nowhere else. `budgetId` is the only
// per-account identifier a browser holds — an account id is derived server-side
// and never served, and a credential id names one factor rather than the
// account.
const KEY_PREFIX = 'budgetoid-rotation-epoch:';

// Decimal digits denoting an epoch, with no leading zero and no other spelling.
//
// **Every coercion in the language is looser than this, and each is looser in a
// way that costs something.** Measured against the junk an attacker who can
// reach this store would write: `Number('')` and `Number('   ')` are 0,
// `Number('0x2')` is 2 — a second spelling of an epoch — `Number('1e21')` is an
// integer by `Number.isInteger` and past the point where `+ 1` still changes the
// value, `Number('Infinity')` compares higher than every real epoch and so
// refuses *genuine* manifests forever, and `parseInt` turns `'1.5'` into 1 and
// `'3abc'` into 3 without a word. A parse here is a security decision.
//
// **The floor is 1 because 0 is the server's "no manifest row" sentinel**, a
// state no account this product creates can be in. A stored 0 is therefore never
// an observation, and letting one through would hand the refusal a high-water
// mark below every real epoch — which is having no memory at all, silently.
const DECIMAL_EPOCH = /^[1-9][0-9]*$/;

/**
 * The highest rotation epoch this device has recorded for `budgetId`, or `null`
 * when it holds no usable record of that account.
 *
 * **One answer covers three situations on purpose**: never recorded, a store
 * that cannot be read, and a stored value this reader will not accept. All three
 * mean the same thing to the caller — this device knows of no epoch to compare
 * against — and a caller that could tell them apart would have nothing different
 * to do about them. Both gates that read it do the one thing all three allow:
 * each treats `null` as a first visit and lets the response through on the other
 * checks, which is ASM-016 written as a branch rather than as a promise.
 *
 * Answers a `number` and never the digits it stored: `'9' < '10'` is false and
 * `'9' < 7` is false, so a caller comparing a string high-water mark against a
 * served epoch is wrong in both directions and is never told.
 */
export function highestRotationEpochSeen(budgetId: string): number | null {
  return storedEpoch(storageKeyFor(budgetId));
}

/**
 * Records that this device has seen `epoch` for `budgetId`, if that is news.
 *
 * Never lowers a record — the whole point — and never fails the caller, which is
 * the unlock attempt in `account-key-custody.service.ts`: mid-way through
 * opening an account, with a factor's keys already drawn and every refusal
 * already passed. A record this device could not keep is a smaller failure than
 * an unlock that does not finish in front of somebody.
 */
export function recordRotationEpochSeen(budgetId: string, epoch: number): void {
  // **Refused rather than stored, and nothing observes this.** The reader would
  // turn junk away whichever way it arrived, so no test can tell this line from
  // its absence. It is here because this store is one an attacker also reads: a
  // `1e21` written by a caller that miscomputed an epoch is a cell that then
  // reads as no record forever, and telling those two apart afterwards is
  // impossible. The condition is `factor-manifest.ts`'s rule for the same
  // number, restated rather than imported because that module owns a *grammar*
  // and this one owns a *store*.
  if (!Number.isSafeInteger(epoch) || epoch < 1) {
    return;
  }

  const key = storageKeyFor(budgetId);
  const current = storedEpoch(key);

  // **Strictly higher, and an unreadable current value is not a ceiling.** This
  // is the reason the check is written against `null` rather than against a
  // number that might be `NaN`: `NaN < 3` is false, so an implementation
  // comparing against an unparsed current writes nothing and the device becomes
  // permanently unable to record anything for this account — one junk cell
  // silently disabling the whole control. A value this reader could not read is
  // not an observation, so it is overwritten.
  if (current !== null && epoch <= current) {
    return;
  }

  try {
    localStorage.setItem(key, String(epoch));
  } catch {
    // A quota, a blocked origin, a private window. See the file header: a device
    // that cannot remember is in the first-visit state this product accepts.
  }
}

// The store's value for `key` as the epoch it spells, or `null`.
//
// Shared by both exports because the write has to know the current record to
// know whether it is news — which is what puts the *read* inside the write, and
// why the guard below covers a store that throws on `getItem` even for a caller
// that only ever writes.
function storedEpoch(key: string): number | null {
  let stored: string | null;

  try {
    stored = localStorage.getItem(key);
  } catch {
    // Read as "not seen", never rethrown. See the file header.
    return null;
  }

  if (stored === null || !DECIMAL_EPOCH.test(stored)) {
    return null;
  }

  const epoch = Number(stored);

  // The regex admits digits of any length, and enough of them are past
  // `Number.MAX_SAFE_INTEGER` — where `+ 1` stops changing the value, so a
  // record holding one refuses every later epoch forever. Checked after the
  // shape rather than instead of it: `Number.isSafeInteger` alone would take
  // `'0x2'` and `''` through `Number`, and the shape check alone would take
  // twenty digits.
  return Number.isSafeInteger(epoch) ? epoch : null;
}

function storageKeyFor(budgetId: string): string {
  return `${KEY_PREFIX}${budgetId}`;
}
