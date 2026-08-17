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
import { ClipboardService } from './clipboard.service';

// The one text every test here writes. Two lines, because the caller that
// matters — the recovery codes step — hands over a multi-line payload, and a
// service that trimmed, collapsed or re-joined it would hand somebody a
// clipboard holding ten codes run together.
const TEXT = 'ABCD-EFGH\nIJKL-MNOP';

describe('ClipboardService', () => {
  let service: ClipboardService;
  let writeText: Mock<(text: string) => Promise<void>>;

  // jsdom implements no `navigator.clipboard` at all — that absence is the
  // whole reason this class exists — so the API is installed as an own
  // property of `navigator` rather than spied on, and removed again in
  // afterEach. Assigned through `Reflect.defineProperty` because
  // `navigator.clipboard` is declared read-only, and defined per test so the
  // absent case below can install `undefined` instead of a fake without
  // depending on what any other test left behind.
  function installClipboard(value: unknown): void {
    Reflect.defineProperty(navigator, 'clipboard', {
      configurable: true,
      value,
    });
  }

  beforeEach(() => {
    writeText = vi.fn<(text: string) => Promise<void>>();
    writeText.mockResolvedValue(undefined);
    installClipboard({ writeText });

    TestBed.configureTestingModule({});
    service = TestBed.inject(ClipboardService);
  });

  afterEach(() => {
    Reflect.deleteProperty(navigator, 'clipboard');
    vi.restoreAllMocks();
  });

  it('hands the browser exactly the text it was given', async () => {
    // Act
    await service.write(TEXT);

    // Assert
    // Identical text, not equivalent text. The payload this carries is a set of
    // secrets a person pastes somewhere and later types back, so a service that
    // normalized the line endings, trimmed the tail, or appended a caption
    // would hand back strings that derive verifiers matching no row.
    expect(writeText).toHaveBeenCalledOnce();
    expect(writeText.mock.calls[0][0]).toBe(TEXT);
  });

  it('resolves once the browser has taken the text', async () => {
    // Act
    const settled = await service
      .write(TEXT)
      .then(() => 'resolved' as const)
      .catch(() => 'rejected' as const);

    // Assert
    // The control for the rejection test below: without it, an implementation
    // that rejected on every call — or one that never settled at all — would
    // satisfy "the failure is propagated" and nothing else in this file would
    // notice that the success path had stopped existing.
    expect(settled).toBe('resolved');
  });

  it('hands back the browser’s refusal rather than swallowing it', async () => {
    // Arrange
    // Not exotic: a permissions policy, an iframe, or Safari deciding the press
    // was not a user gesture all land here, on browsers a person would call
    // working.
    const refusal = new Error('clipboard write refused');
    writeText.mockRejectedValue(refusal);

    // Act
    const caught = await service.write(TEXT).catch((error: unknown) => error);

    // Assert
    // The same error object, not merely some rejection. A service that caught
    // and re-raised its own error would be indistinguishable here from one that
    // caught and resolved — and a caught-and-resolved write leaves somebody
    // believing ten secrets are on their clipboard when nothing is.
    expect(caught).toBe(refusal);
  });

  it('rejects rather than throwing when the browser exposes no clipboard', async () => {
    // Arrange
    // An insecure origin or an embedded webview: `navigator.clipboard` is
    // absent outright, not merely unusable. This is the branch the calling
    // component's spec can never reach, because it always injects a fake.
    installClipboard(undefined);

    // Act
    // Called plainly rather than wrapped in `expect(...).not.toThrow()`: a
    // synchronous `TypeError` fails this line on its own, and the wrapper would
    // leave the rejected promise unobserved.
    const pending = service.write(TEXT);

    // Assert
    // The distinction is the whole test. A throw out of a click handler is not
    // something the caller's `.catch()` can turn into a sentence on screen — it
    // escapes to the console and the person is left looking at a button that
    // did nothing. A rejection lands in the same branch as every routine
    // refusal above, so one report covers both, which is exactly what the
    // caller already writes.
    expect(pending).toBeInstanceOf(Promise);
    await expect(pending).rejects.toBeInstanceOf(Error);
  });

  it('writes nothing until it is asked to', () => {
    // Assert
    // Constructing the service — which `TestBed.inject` in the setup above
    // already did — puts nothing on the clipboard. The control for the two
    // call-count assertions above: `toHaveBeenCalledOnce` after a `write` is
    // also true of a service that wrote on construction and then not at all.
    // The service is `providedIn: 'root'`, so this one runs at application
    // start on every screen, for somebody who has asked for nothing.
    expect(writeText).not.toHaveBeenCalled();
  });
});
