import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { BrandLockupComponent } from '@app-shared/components/brand-lockup/brand-lockup.component';

// The glyphs the navigation draws, addressed by **codepoint**.
//
// Material Symbols can also be addressed by ligature — write `settings` in the
// markup and let an `rlig` feature swap the letters for the glyph — and that was
// rejected when the face was subset: the ligature closure drags in the letters
// that spell every name plus a thousand placeholder glyphs, about 87 kB against
// the 1.5 kB that ships. What the cheap option costs is markup nobody can read,
// and this map is what buys that back, so a codepoint is never written into a
// template. See public/fonts/README.md — and note that adding a destination
// means regenerating the subset with its codepoint in it, under a new filename.
//
// The numbers rather than the characters: these sit in a private use area, so
// pasted literally they are an empty box in an editor and nothing at all in a
// diff, and a wrong one would be invisible to review. Written this way each
// value can be checked against the `--unicodes=` list in the subsetting recipe.
const ICON_GLYPH = {
  /** `receipt_long` */
  receiptLong: String.fromCodePoint(0xef6e),
  /** `account_balance_wallet` */
  accountBalanceWallet: String.fromCodePoint(0xe850),
  /** `category` */
  category: String.fromCodePoint(0xe574),
  /** `settings` */
  settings: String.fromCodePoint(0xe8b8),
} as const;

/** One item of the navigation; the bar and the rail render the same list. */
export interface ShellDestination {
  /** Absolute route the item links to. Every destination lives under `/app`. */
  readonly link: string;
  /** The visible label, which is also the item's accessible name. */
  readonly label: string;
  /** A glyph from `ICON_GLYPH` — never a codepoint written at the call site. */
  readonly icon: string;
}

// **Four destinations, and two departures from docs/design/components.md that
// were decided rather than forgotten.**
//
// *Settings is a destination although the book says it is not.* That sentence
// was a product decision and the product owner has overturned it; the chapter is
// being corrected separately. `/app/settings` has existed for a while and was
// reachable only by typing the URL.
//
// *Home is absent although the book lists it, and the Add action is absent
// although the book centres the bar on it and calls it the most important
// control in the product.* There is no `/app/home` route, and there is no global
// add flow — each screen carries its own inline form — so both controls would
// lead nowhere. A destination pointing at something that does not exist is worse
// than a missing one: it is a promise the product breaks on the first tap. The
// four below are therefore spread evenly across the bar rather than parted
// around the slot the Add button would have filled, because a gap is itself a
// claim that something belongs in it.
export const SHELL_DESTINATIONS = [
  {
    link: '/app/transactions',
    label: 'Transactions',
    icon: ICON_GLYPH.receiptLong,
  },
  {
    link: '/app/accounts',
    label: 'Accounts',
    icon: ICON_GLYPH.accountBalanceWallet,
  },
  {
    link: '/app/categories',
    label: 'Categories',
    icon: ICON_GLYPH.category,
  },
  {
    link: '/app/settings',
    label: 'Settings',
    icon: ICON_GLYPH.settings,
  },
] as const satisfies readonly ShellDestination[];

// The layout of the signed-in surface, and the only place the navigation is
// rendered.
//
// It hangs off the `app` route rather than off the root shell, and reading the
// session status here to decide whether to draw a bar would be the wrong
// question asked in the wrong place: which routes are the signed-in surface is a
// fact about the route table, and `authGuard` on each child already governs who
// reaches them. A status test would also keep the bar over `/welcome` for as
// long as a stale status said the visitor was signed in.
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BrandLockupComponent, RouterLink, RouterLinkActive, RouterOutlet],
  selector: 'app-shell',
  styleUrls: ['./shell.component.scss'],
  templateUrl: './shell.component.html',
})
export class ShellComponent {
  protected readonly destinations = SHELL_DESTINATIONS;
}
