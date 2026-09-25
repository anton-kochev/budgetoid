// The two halves of the key-retention walk that no property walk over a value
// graph can perform, shared by the specs that hold the same rule about two
// different flows.
//
// **They are here because they were byte-identical in two files and their
// comments were not.** `sign-in.service.spec.ts` and
// `account-unlock.service.spec.ts` each carried a private copy, and the copies
// had already begun to describe themselves differently — which is the visible
// half of a drift whose invisible half is a walk that stops catching something
// in one file while its twin still does. There is no test anywhere that could
// notice, because the two are read by two suites that never meet.
//
// **Nothing about either is weakened by being shared.** Both are pure functions
// over a value, both take `object` rather than a service type, and each caller
// keeps its own positive control — the walk is only worth anything if the suite
// reading it has watched it find something, and that is a fact about a suite
// rather than about the function.
//
// This file is deliberately **not** a `*.spec.ts`. A spec imported by a spec
// registers its own `describe` blocks inside the importer's suite, so the
// shared tests would run once per consumer and the count would climb with the
// number of readers.
import { isSignal } from '@angular/core';

/**
 * The own properties of `value` that are ordinary functions, by name.
 *
 * **The closure half of the retention walk, and the reason a value walk alone
 * is not enough.**
 *
 * The retention these specs exist for is not only `this.lastKey = key`. It is
 * `this.retry = () => this.custody.unlock(key)` — an arrow function stored on
 * the instance that *captures* the key in its scope. Nothing in JavaScript can
 * read a captured binding out of a closure: no own-property walk reaches it, no
 * `JSON.stringify` sees it, and `Function.prototype.toString` returns the
 * source text rather than the values. So the value is unreachable to a test —
 * and the container is not. This lists the own properties that could hold one.
 *
 * Signals are excluded because `signal()` and `.asReadonly()` both return
 * callables, and the value walk beside this one already looks *inside* those by
 * calling them. Class methods never reach here at all: they live on the
 * prototype, and this walks own properties only.
 */
export function ownFunctionsOf(value: object): readonly string[] {
  return Object.entries(value)
    .filter(
      ([, member]: [string, unknown]) =>
        typeof member === 'function' && !isSignal(member),
    )
    .map(([name]) => name);
}

/**
 * A module namespace as a plain object a value walk can descend into.
 *
 * Every export, and — for every exported class or function — its own enumerable
 * properties, which is where a `static lastKey` would sit. Static *methods* are
 * non-enumerable and never appear; static *fields* are enumerable and do, which
 * is the shape the defect would take.
 */
export function moduleSurface(namespace: object): Record<string, unknown> {
  return Object.fromEntries(
    Object.entries(namespace).map(([name, value]: [string, unknown]) => [
      name,
      typeof value === 'function' || typeof value === 'object'
        ? { ...(value as object) }
        : value,
    ]),
  );
}
