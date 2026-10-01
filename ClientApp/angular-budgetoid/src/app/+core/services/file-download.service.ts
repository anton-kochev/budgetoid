import { Injectable } from '@angular/core';

// This is a separate injectable rather than three lines inside the caller
// because the browser APIs it touches do not exist under the test runner:
// jsdom implements no `URL.createObjectURL`, and a real `click()` on an anchor
// makes it throw "Not implemented: navigation". Confining that edge to one
// class lets everything above it stub this service instead of the DOM.
@Injectable({ providedIn: 'root' })
export class FileDownloadService {
  public save(blob: Blob, filename: string): void {
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement('a');

    anchor.href = url;
    // The only thing that names the file on disk. The API sends a
    // `Content-Disposition`, but `Api/Program.cs` sets no
    // `Access-Control-Expose-Headers`, so cross-origin JavaScript cannot read
    // it; without this attribute the browser saves the file under the URL.
    anchor.download = filename;
    anchor.click();
    // The release has to be both after the click and outside the click's own
    // task. Revoking before it leaves the anchor pointing at a released URL;
    // revoking in the same task cancels the download outright in Firefox and
    // Safari, which have not started reading the blob by the time the click
    // handler returns, so the user is told the export succeeded and gets no
    // file. A macrotask is the shortest delay that clears the click's task —
    // the constant does not matter, being in a later task does.
    setTimeout(() => URL.revokeObjectURL(url), 0);
  }
}
