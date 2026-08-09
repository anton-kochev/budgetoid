import { TestBed } from '@angular/core/testing';
import {
  afterEach,
  beforeEach,
  describe,
  expect,
  it,
  vi,
  type Mock,
} from 'vitest';
import { FileDownloadService } from './file-download.service';

describe('FileDownloadService', () => {
  let service: FileDownloadService;
  let createObjectUrl: Mock<(obj: Blob | MediaSource) => string>;
  let revokeObjectUrl: Mock<(url: string) => void>;
  let clicked: HTMLElement[];
  let callOrder: string[];

  // The clicked element arrives as the spy's `this`, typed HTMLElement because
  // `click` is declared on HTMLElement. Narrow rather than assert, so a
  // save() that clicked something other than an anchor fails loudly here
  // instead of reading `undefined` off the wrong element.
  function clickedAnchor(): HTMLAnchorElement {
    const element = clicked[0];

    if (!(element instanceof HTMLAnchorElement)) {
      throw new Error('save() clicked no anchor element');
    }

    return element;
  }

  beforeEach(() => {
    clicked = [];
    callOrder = [];
    createObjectUrl = vi.fn((obj: Blob | MediaSource): string => 'blob:test');
    revokeObjectUrl = vi.fn((url: string): void => {
      callOrder.push('revoke');
    });

    // jsdom implements neither of these, so they are assigned onto URL rather
    // than spied on, and removed again in afterEach.
    URL.createObjectURL = createObjectUrl;
    URL.revokeObjectURL = revokeObjectUrl;

    // A real click on an anchor makes jsdom throw "Not implemented:
    // navigation", so the click is the boundary mock — and it is also how the
    // anchor itself is captured, since the implementation never exposes it.
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (
      this: HTMLElement,
    ): void {
      clicked.push(this);
      callOrder.push('click');
    });

    TestBed.configureTestingModule({});
    service = TestBed.inject(FileDownloadService);
  });

  afterEach(() => {
    Reflect.deleteProperty(URL, 'createObjectURL');
    Reflect.deleteProperty(URL, 'revokeObjectURL');
    vi.restoreAllMocks();
  });

  it('hands the blob to the browser under the given name', () => {
    // Arrange
    const blob = new Blob(['{"schemaVersion":1}'], {
      type: 'application/json',
    });

    // Act
    service.save(blob, 'budgetoid-export-20260809T233015Z.json');

    // Assert
    const anchor = clickedAnchor();
    // The download attribute is the only thing that names the file on disk:
    // the API's Content-Disposition is unreadable cross-origin, so an
    // implementation that omitted it would save a file named after the URL.
    expect(anchor.download).toBe('budgetoid-export-20260809T233015Z.json');
    expect(anchor.href).toBe('blob:test');
  });

  it('creates the object URL from the blob it was given', () => {
    // Arrange
    const blob = new Blob(['{"schemaVersion":1}'], {
      type: 'application/json',
    });

    // Act
    service.save(blob, 'budgetoid-export-20260809T233015Z.json');

    // Assert
    // Control for the test above: an implementation that minted a URL for a
    // fresh empty blob still sets the right `download` and the right `href`,
    // and hands over a zero-byte file. Identity, not equality — a copy of the
    // same bytes is the very re-serialization this feature must not do.
    expect(createObjectUrl).toHaveBeenCalledTimes(1);
    expect(createObjectUrl.mock.calls[0][0]).toBe(blob);
  });

  it('releases the object URL it created', () => {
    // Arrange
    const blob = new Blob(['{"schemaVersion":1}'], {
      type: 'application/json',
    });

    // Act
    service.save(blob, 'budgetoid-export-20260809T233015Z.json');

    // Assert
    expect(revokeObjectUrl).toHaveBeenCalledWith('blob:test');
  });

  it('releases the object URL only after the click', () => {
    // Arrange
    const blob = new Blob(['{"schemaVersion":1}'], {
      type: 'application/json',
    });

    // Act
    service.save(blob, 'budgetoid-export-20260809T233015Z.json');

    // Assert
    // Control for the test above. Revoking before the click leaves a dead
    // href that downloads nothing, and `toHaveBeenCalledWith` cannot tell
    // that apart from a correct release — only the order can.
    expect(callOrder).toEqual(['click', 'revoke']);
  });
});
