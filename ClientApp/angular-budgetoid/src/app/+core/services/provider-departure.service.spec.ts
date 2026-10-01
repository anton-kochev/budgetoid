// The one place this page says it is leaving for the identity provider, and
// the one place it leaves. `AuthService` hands the provider client this
// service's `depart` as its `openUri`, raises `departing` itself before a press
// awaits anything, and lowers it whenever a press does not leave or a restore
// from the back-forward cache brings the page back. The settings screen reads
// `departing` and keeps no copy of its own. See docs/design/components.md,
// "Changing the email address".
//
// The document is a stub: jsdom cannot leave its own origin, and the address
// assigned is the thing under test. Every spy is built inside its case,
// because spies persist across cases here.
import { DOCUMENT } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it, vi, type Mock } from 'vitest';
import { ProviderDepartureService } from './provider-departure.service';

const LOGIN_URL =
  'https://accounts.google.com/o/oauth2/v2/auth?client_id=client&state=s';

function departureOver(assign: Mock<(url: string) => void>): {
  readonly departure: ProviderDepartureService;
} {
  TestBed.configureTestingModule({
    providers: [{ provide: DOCUMENT, useValue: { location: { assign } } }],
  });

  return { departure: TestBed.inject(ProviderDepartureService) };
}

describe('ProviderDepartureService', () => {
  // Root-provided, so `AuthService` and the settings screen's flow read one
  // flag. Registered anywhere narrower, the screen would read a copy that a
  // press never raises.
  it('is one instance for the whole application, with no registration', () => {
    // Arrange
    departureOver(vi.fn());

    // Act
    const first = TestBed.inject(ProviderDepartureService);
    const second = TestBed.inject(ProviderDepartureService);

    // Assert
    expect(first).toBe(second);
  });

  it('reads departing as false before anything departs', () => {
    // Arrange
    const { departure } = departureOver(vi.fn());

    // Act
    const departing = departure.departing();

    // Assert
    expect(departing).toBe(false);
  });

  it('depart opens the address it is handed, unchanged, as a top-level navigation', () => {
    // Arrange
    const assign = vi.fn<(url: string) => void>();
    const { departure } = departureOver(assign);

    // Act
    departure.depart(LOGIN_URL);

    // Assert
    expect(assign).toHaveBeenCalledOnce();
    expect(assign).toHaveBeenCalledWith(LOGIN_URL);
  });

  // The page may be gone the moment the address is assigned, so the flag has
  // to be up before, not after.
  it('depart says the page is departing before it assigns the address', () => {
    // Arrange
    const seenAtAssignment: boolean[] = [];
    let departure: ProviderDepartureService | null = null;
    const assign = vi.fn<(url: string) => void>(() => {
      seenAtAssignment.push(departure?.departing() ?? false);
    });
    departure = departureOver(assign).departure;

    // Act
    departure.depart(LOGIN_URL);

    // Assert
    expect(seenAtAssignment).toEqual([true]);
  });

  // The press raises the flag before its first await, long before there is
  // an address to open.
  it('begin says the page is departing and opens nothing', () => {
    // Arrange
    const assign = vi.fn<(url: string) => void>();
    const { departure } = departureOver(assign);

    // Act
    departure.begin();

    // Assert
    expect(departure.departing()).toBe(true);
    expect(assign).not.toHaveBeenCalled();
  });

  it('settle puts departing back to false after a begin', () => {
    // Arrange
    const { departure } = departureOver(vi.fn());
    departure.begin();

    // Act
    departure.settle();

    // Assert
    expect(departure.departing()).toBe(false);
  });

  // A restore from the back-forward cache comes back to a page whose last act
  // was `depart`.
  it('settle puts departing back to false after a depart', () => {
    // Arrange
    const { departure } = departureOver(vi.fn());
    departure.depart(LOGIN_URL);

    // Act
    departure.settle();

    // Assert
    expect(departure.departing()).toBe(false);
  });

  // Nothing outside this service writes the flag. A writable signal here
  // would let a screen raise or lower it under a press it knows nothing of.
  it('publishes departing as a reading, not a writable signal', () => {
    // Arrange
    const { departure } = departureOver(vi.fn());

    // Act
    const reading: object = departure.departing;

    // Assert
    expect('set' in reading).toBe(false);
    expect('update' in reading).toBe(false);
  });
});
