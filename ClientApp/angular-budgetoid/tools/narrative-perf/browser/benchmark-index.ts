// Lighthouse's BenchmarkIndex, lifted unchanged from the copy Chrome DevTools
// runs during CPU throttling calibration.
//
// It is here verbatim on purpose. The whole value of the calibration step is
// that the *device* is a fixed reference and the *rate* is derived per machine;
// a benchmark of our own would produce a number nothing else in the world can
// be compared against, and "low-tier mobile" would become "whatever this loop
// does on this laptop".
//
// Source: `front_end/panels/mobile_throttling/CalibrationController.ts` in
// devtools-frontend, which lifted it from Lighthouse
// (https://github.com/GoogleChrome/lighthouse/issues/9085). The two halves are
// a GC-heavy string build and a GC-free array copy, averaged.
//
// As of Chrome m86, per the Lighthouse comment:
//   1000+ desktop-class, 800+ high-end Android, 125+ mid-tier Android,
//   <125 budget Android.

/**
 * Runs the benchmark for `durationMs` and returns the index.
 *
 * Higher is faster. Called at rate 1 to learn what the measuring machine is,
 * then at candidate throttling rates to find the one that lands on a target
 * device's score.
 */
export function computeBenchmarkIndex(durationMs: number): number {
  const halfTime = durationMs / 2;

  // The GC-heavy half: build a 10000-character string over and over. The
  // division by 10 keeps the magnitude comparable with an older Lighthouse
  // version that used 100000.
  function benchmarkIndexGc(): number {
    const start = Date.now();
    let iterations = 0;

    while (Date.now() - start < halfTime) {
      let s = '';

      for (let j = 0; j < 10000; j++) {
        s += 'a';
      }

      if (s.length === 1) {
        throw new Error(
          'never happens; prevents the optimizer folding it away',
        );
      }

      iterations++;
    }

    const durationInSeconds = (Date.now() - start) / 1000;

    return Math.round(iterations / 10 / durationInSeconds);
  }

  // The GC-free half: copy 100000 integers between two arrays. The clock is
  // read every tenth iteration rather than every one, which Lighthouse does to
  // dodge a JCC-alignment performance cliff on some Intel parts
  // (https://bugs.chromium.org/p/v8/issues/detail?id=10954#c1).
  function benchmarkIndexNoGc(): number {
    const arrA: number[] = [];
    const arrB: number[] = [];

    for (let i = 0; i < 100000; i++) {
      arrA[i] = i;
      arrB[i] = i;
    }

    const start = Date.now();
    let iterations = 0;

    while (iterations % 10 !== 0 || Date.now() - start < halfTime) {
      const src = iterations % 2 === 0 ? arrA : arrB;
      const tgt = iterations % 2 === 0 ? arrB : arrA;

      for (let j = 0; j < src.length; j++) {
        tgt[j] = src[j] ?? 0;
      }

      iterations++;
    }

    const durationInSeconds = (Date.now() - start) / 1000;

    return Math.round(iterations / 10 / durationInSeconds);
  }

  return (benchmarkIndexGc() + benchmarkIndexNoGc()) / 2;
}
