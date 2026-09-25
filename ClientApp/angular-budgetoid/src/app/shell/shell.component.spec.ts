import { provideLocationMocks } from '@angular/common/testing';
import { ChangeDetectionStrategy, Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, type Routes } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { routes } from '../app.routes';
import { SHELL_DESTINATIONS, ShellComponent } from './shell.component';

// A stand-in for every screen under `/app`. The real ones reach the network on
// creation and none of what is asserted here belongs to them: the router decides
// which destination is current, and the shell renders that decision.
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: '',
})
class StubScreen {}

// The shell mounted the way the real route table mounts it — the component of a
// parent route whose children are the four screens — so `routerLinkActive` is
// driven by a real navigation rather than by an input a test hands it.
//
// The four paths are written out rather than derived from `SHELL_DESTINATIONS`,
// which is the subject here: children generated from the destination list would
// follow a mistyped `link` wherever it went and route to it perfectly.
const testRoutes: Routes = [
  {
    path: 'app',
    component: ShellComponent,
    children: [
      { path: 'transactions', component: StubScreen },
      { path: 'accounts', component: StubScreen },
      { path: 'categories', component: StubScreen },
      { path: 'settings', component: StubScreen },
    ],
  },
];

function itemsOf(root: HTMLElement): readonly HTMLAnchorElement[] {
  return Array.from(root.querySelectorAll<HTMLAnchorElement>('nav a'));
}

// What a screen reader would announce for an item: its text with everything
// hidden from the accessibility tree taken out. Read off the DOM rather than off
// a class name, because the accessible name is the thing under test — the label
// is asserted to be the name, not merely to be on the screen somewhere.
function accessibleNameOf(item: Element): string {
  return Array.from(item.children)
    .filter((child) => child.getAttribute('aria-hidden') !== 'true')
    .map((child) => child.textContent ?? '')
    .join('')
    .trim();
}

async function shellAt(url: string): Promise<{
  readonly harness: RouterTestingHarness;
  readonly root: HTMLElement;
}> {
  const harness = await RouterTestingHarness.create(url);

  return { harness, root: harness.fixture.nativeElement as HTMLElement };
}

describe('ShellComponent', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideRouter(testRoutes), provideLocationMocks()],
    });
  });

  it('renders one named nav holding the four destinations', async () => {
    // Arrange
    const { root } = await shellAt('/app/transactions');

    // Act
    const navs = root.querySelectorAll('nav');
    const names = itemsOf(root).map(accessibleNameOf);

    // Assert
    // One nav, not two. The bar and the rail are the same element under
    // different grid rules; a second element would put a second copy of this
    // list in the accessibility tree and read every destination twice.
    expect(navs.length).toBe(1);
    expect(navs[0].getAttribute('aria-label')).toBe('Primary');
    expect(names).toEqual([
      'Transactions',
      'Accounts',
      'Categories',
      'Settings',
    ]);
  });

  it('points each destination at its own route', async () => {
    // Arrange
    const { root } = await shellAt('/app/transactions');

    // Act
    const hrefs = itemsOf(root).map((item) => item.getAttribute('href'));

    // Assert
    // Written out rather than read back from `SHELL_DESTINATIONS`: a test
    // comparing the rendered links against the list they were rendered from
    // agrees with every typo in it.
    expect(hrefs).toEqual([
      '/app/transactions',
      '/app/accounts',
      '/app/categories',
      '/app/settings',
    ]);
  });

  it('marks the destination the router is on with aria-current', async () => {
    // Arrange
    const { root } = await shellAt('/app/transactions');

    // Act
    const current = itemsOf(root).map((item) =>
      item.getAttribute('aria-current'),
    );

    // Assert
    // Asserted on the attribute and not on the class beside it: colour and a
    // bead are not a state anything but an eye can read. Both come from the one
    // `routerLinkActive` directive, so the visible state and the announced one
    // cannot disagree.
    expect(current).toEqual(['page', null, null, null]);
  });

  it('moves aria-current when the router moves', async () => {
    // Arrange
    const { harness, root } = await shellAt('/app/transactions');

    // Act
    await harness.navigateByUrl('/app/settings');

    // Assert
    // The control for the test above, and the reason both exist: an
    // `aria-current="page"` written straight into the template passes that one
    // perfectly and then marks Transactions from every screen in the product.
    expect(
      itemsOf(root).map((item) => item.getAttribute('aria-current')),
    ).toEqual([null, null, null, 'page']);
  });

  it('hides every icon from the accessibility tree', async () => {
    // Arrange
    const { root } = await shellAt('/app/transactions');
    const items = itemsOf(root);

    // Act
    // The glyph is found by its codepoint rather than by a class, so this also
    // fails on an item that renders no icon at all.
    const hidden = SHELL_DESTINATIONS.map((destination, index) =>
      Array.from(items[index].querySelectorAll('span'))
        .find((span) => span.textContent === destination.icon)
        ?.getAttribute('aria-hidden'),
    );

    // Assert
    // Private use codepoints: read aloud they are noise at best, and each glyph
    // already has the visible label beside it as the item's name.
    expect(hidden).toEqual(['true', 'true', 'true', 'true']);
  });

  it('renders the routed screen inside itself', async () => {
    // Arrange
    const { root } = await shellAt('/app/transactions');

    // Act
    const outlet = root.querySelector('main router-outlet');

    // Assert
    // Without this the shell is a nav that replaces the application rather than
    // a layout that wraps it.
    expect(outlet).not.toBeNull();
  });

  it('is the component of the /app route in the real route table', async () => {
    // Arrange
    const appRoute = routes.find((route) => route.path === 'app');

    // Act
    const loaded = await appRoute?.loadComponent?.();

    // Assert
    // The wiring, and nothing else in this file can see it: every test above
    // mounts the shell on a route table of its own, and would stay green with
    // the shell reachable from nothing the application ships.
    expect(loaded).toBe(ShellComponent);
  });

  it('leaves the anonymous screens outside the shell', () => {
    // Arrange
    const appRoute = routes.find((route) => route.path === 'app');

    // Act
    const shellChildren = (appRoute?.children ?? []).map((child) => child.path);
    const topLevel = routes.map((route) => route.path);

    // Assert
    // What keeps the bar off `/welcome` and `/register`: they are siblings of
    // the route the shell is mounted on, so nothing renders them through it.
    // Moving either under `app` would draw a signed-in navigation over a screen
    // meant for somebody who has no account yet.
    expect(shellChildren).not.toContain('welcome');
    expect(shellChildren).not.toContain('register');
    expect(topLevel).toContain('welcome');
    expect(topLevel).toContain('register');
  });
});
