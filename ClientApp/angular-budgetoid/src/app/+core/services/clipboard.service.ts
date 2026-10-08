import { Injectable } from '@angular/core';

// A seam over the one clipboard API, for `FileDownloadService`'s reason and one
// of its own.
//
// The reason it shares: jsdom implements no `navigator.clipboard`, so a
// component reaching for it directly cannot be rendered under the test runner
// at all — not stubbed badly, not stubbed at all. Confining the browser edge to
// one class lets everything above it replace this service instead of patching a
// read-only property on a global.
//
// The reason it does not share: `writeText` **rejects routinely**, on browsers
// a person would call working. A permissions policy, an iframe, or Safari
// deciding the press did not count as a user gesture all land in the rejected
// branch, and none of them is a defect in this app. The promise is handed back
// unswallowed precisely so the caller has to say something when it settles the
// wrong way — a caller that ignored the outcome would leave somebody believing
// ten secrets are on their clipboard when nothing is.
@Injectable({ providedIn: 'root' })
export class ClipboardService {
  // `navigator.clipboard` is itself absent — not just unusable — on an insecure
  // origin and in some embedded webviews, so the optional chain is not defensive
  // decoration: without it the caller gets a synchronous `TypeError` thrown out
  // of a click handler instead of a rejected promise it already knows how to
  // report. Both failures are the same failure to a person, so they are handed
  // back the same way.
  public write(text: string): Promise<void> {
    return (
      navigator.clipboard?.writeText(text) ??
      Promise.reject(new Error('This browser exposes no clipboard.'))
    );
  }
}
