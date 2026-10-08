// A two-file static server on 127.0.0.1, held in memory.
//
// **`about:blank` will not do.** `crypto.subtle` exists only in a secure
// context, so the page has to arrive over an origin the browser trusts —
// 127.0.0.1 is one by definition, which is why the harness serves rather than
// navigating to a data URL. Nothing is written to disk and nothing is written
// into the repository.
//
// Port zero, so a busy port cannot make the tool flaky.
import { createServer, type Server } from 'node:http';
import type { AddressInfo } from 'node:net';

/** A file the harness serves, keyed by request path. */
export interface ServedFile {
  readonly body: string;
  readonly contentType: string;
}

/** A running server and the origin the page should be opened on. */
export interface StaticSite {
  readonly origin: string;
  /**
   * Stops listening and drops whatever is still connected.
   *
   * **The connections are dropped first, and that is not tidying.**
   * `Server.close` stops the listener and then waits for every open socket to
   * end on its own; a browser keeps its connection alive, so the wait only ever
   * returned because the harness had already `SIGKILL`ed Chrome a line earlier.
   * A kill that misses — a browser that outlived its handle, a headful window
   * somebody closed — left the harness hanging in the cleanup whose whole job
   * is to stop it hanging.
   */
  close(): Promise<void>;
}

/** Starts serving `files`, resolving once the port is known. */
export async function startStaticServer(
  files: ReadonlyMap<string, ServedFile>,
): Promise<StaticSite> {
  const server: Server = createServer((request, response) => {
    const file = files.get(request.url ?? '/');

    if (file === undefined) {
      response.writeHead(404, { 'Content-Type': 'text/plain; charset=utf-8' });
      response.end('not found');

      return;
    }

    response.writeHead(200, {
      'Cache-Control': 'no-store',
      'Content-Type': file.contentType,
    });
    response.end(file.body);
  });

  await new Promise<void>((resolve, reject) => {
    server.once('error', reject);
    server.listen(0, '127.0.0.1', resolve);
  });

  const address = server.address();

  if (address === null || typeof address === 'string') {
    throw new Error('The harness server did not bind a numbered port.');
  }

  return {
    async close(): Promise<void> {
      server.closeAllConnections();

      await new Promise<void>((resolve) => {
        server.close(() => {
          resolve();
        });
      });
    },
    origin: originOf(address),
  };
}

function originOf(address: AddressInfo): string {
  return `http://127.0.0.1:${address.port}`;
}
