// Finds and launches the Chrome that is already on the machine.
//
// Nothing is downloaded and nothing is installed. `CHROME_PATH` wins if it is
// set; otherwise the usual macOS and Linux locations are tried in order and the
// first one that exists is used. An absent Chrome is a harness failure and the
// only kind of failure this tool exits non-zero on.
import { spawn, type ChildProcess } from 'node:child_process';
import { access } from 'node:fs/promises';
import { constants } from 'node:fs';

/** A running browser and the endpoint it is listening on. */
export interface LaunchedChrome {
  readonly executable: string;
  readonly webSocketDebuggerUrl: string;
  kill(): void;
}

/**
 * Starts Chrome with a throwaway profile and returns once it has printed its
 * DevTools endpoint.
 *
 * Port zero, so a busy port cannot make the tool flaky — Chrome picks a free
 * one and announces it on stderr. The background-throttling flags are off
 * because a headless tab is by definition never in the foreground, and letting
 * Chrome throttle timers and rendering in it would silently change every frame
 * number the harness records.
 *
 * `--enable-precise-memory-info` is for the export cell's heap figures. Without
 * it Chrome quantises `performance.memory` into coarse buckets and refreshes it
 * on a timer, so two reads a phase apart can return the same stale number. It
 * is a reporting flag rather than a scheduling one; the read cells were not
 * re-baselined with and without it.
 */
export async function launchChrome(options: {
  readonly headless: boolean;
  readonly userDataDir: string;
}): Promise<LaunchedChrome> {
  const executable = await findChrome();
  const child = spawn(
    executable,
    [
      ...(options.headless ? ['--headless=new'] : []),
      '--remote-debugging-port=0',
      `--user-data-dir=${options.userDataDir}`,
      '--no-first-run',
      '--no-default-browser-check',
      '--disable-background-timer-throttling',
      '--disable-backgrounding-occluded-windows',
      '--disable-renderer-backgrounding',
      '--disable-features=CalculateNativeWinOcclusion,Translate',
      '--disable-extensions',
      '--disable-sync',
      '--enable-precise-memory-info',
      '--mute-audio',
      '--hide-scrollbars',
      '--window-size=1280,900',
      'about:blank',
    ],
    { stdio: ['ignore', 'ignore', 'pipe'] },
  );

  const webSocketDebuggerUrl = await readEndpoint(child);

  return {
    executable,
    kill(): void {
      child.kill('SIGKILL');
    },
    webSocketDebuggerUrl,
  };
}

/** How long to wait for Chrome to announce its endpoint. */
const LAUNCH_TIMEOUT_MS = 30_000;

const CANDIDATES = [
  '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
  '/Applications/Google Chrome Canary.app/Contents/MacOS/Google Chrome Canary',
  '/Applications/Chromium.app/Contents/MacOS/Chromium',
  '/usr/bin/google-chrome',
  '/usr/bin/google-chrome-stable',
  '/usr/bin/chromium',
  '/usr/bin/chromium-browser',
];

async function findChrome(): Promise<string> {
  const configured = process.env['CHROME_PATH'];
  const candidates = configured === undefined ? CANDIDATES : [configured];

  for (const candidate of candidates) {
    try {
      await access(candidate, constants.X_OK);

      return candidate;
    } catch {
      continue;
    }
  }

  throw new Error(
    `No Chrome found. Tried ${candidates.join(', ')}. Set CHROME_PATH to point at one.`,
  );
}

/**
 * How much of Chrome's stderr is kept to quote in a failure message.
 *
 * **Bounded, and it is the first bytes that are kept.** A Chrome that refuses to
 * start says why in its first lines; a Chrome that starts writes for the whole
 * multi-minute run. Keeping all of it grew without limit on the process that
 * owns a CPU-timing measurement, for a diagnostic nobody reads past the top of.
 */
const MAX_TRANSCRIPT_CHARS = 8_192;

/**
 * How much of the previous chunk is re-scanned with the next one.
 *
 * The endpoint line can arrive split across two reads, so the scan needs some
 * overlap — but only that much. Re-running the pattern over everything read so
 * far made the scan quadratic in the length of the run.
 */
const CARRY_CHARS = 256;

const ENDPOINT = /DevTools listening on (ws:\/\/\S+)/;

function readEndpoint(child: ChildProcess): Promise<string> {
  return new Promise<string>((resolve, reject) => {
    let transcript = '';
    let carry = '';
    let settled = false;

    // Named, because every one of them has to come off again: this promise
    // settles once and the process it is listening to lives for the rest of the
    // run.
    const onData = (chunk: Buffer): void => {
      const text = chunk.toString('utf8');
      const found = ENDPOINT.exec(carry + text);

      carry = (carry + text).slice(-CARRY_CHARS);
      transcript = (transcript + text).slice(0, MAX_TRANSCRIPT_CHARS);

      if (found?.[1] !== undefined) {
        settle(() => {
          resolve(found[1]);
        });
      }
    };

    const onError = (cause: Error): void => {
      settle(() => {
        reject(cause);
      });
    };

    // **Without this a dead Chrome costs the whole timeout.** A bad flag, a
    // locked profile directory or a refused sandbox all start a process that
    // exits immediately, and waiting thirty seconds to report "no endpoint"
    // buries the complaint Chrome already made.
    const onExit = (code: number | null, signal: string | null): void => {
      settle(() => {
        reject(
          new Error(
            `Chrome exited (${describeExit(code, signal)}) without announcing a DevTools endpoint. It said: ${transcript.trim()}`,
          ),
        );
      });
    };

    const timer = setTimeout(() => {
      settle(() => {
        child.kill('SIGKILL');
        reject(
          new Error(
            `Chrome did not announce a DevTools endpoint within ${LAUNCH_TIMEOUT_MS} ms. It said: ${transcript.trim()}`,
          ),
        );
      });
    }, LAUNCH_TIMEOUT_MS);

    timer.unref();

    function settle(outcome: () => void): void {
      if (settled) {
        return;
      }

      settled = true;
      clearTimeout(timer);
      child.stderr?.off('data', onData);
      child.off('error', onError);
      child.off('exit', onExit);

      // **Detached, but still drained.** A pipe nobody reads fills up and then
      // blocks the writer, so dropping the listener alone would leave Chrome
      // stalling on its own logging part-way through a measurement. `resume()`
      // is the flowing mode that discards: nothing is kept and nothing backs up.
      child.stderr?.resume();
      outcome();
    }

    child.on('error', onError);
    child.on('exit', onExit);
    child.stderr?.on('data', onData);
  });
}

function describeExit(code: number | null, signal: string | null): string {
  return signal === null ? `code ${String(code)}` : `signal ${signal}`;
}
