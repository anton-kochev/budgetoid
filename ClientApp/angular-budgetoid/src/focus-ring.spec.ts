import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import { emittedFiles, expectProductionBuild } from './production-bundle';

// docs/design/accessibility.md, "Focus": every interactive element shows
// `2px solid var(--bud-focus-ring)` with `outline-offset: 2px` on
// `:focus-visible`, and focus is never hidden without a replacement. Material
// sets `outline: none` on its button host, so unless the application ships a
// ring of its own there is nothing to see when the page is driven from a
// keyboard.
//
// Read from the emitted stylesheet rather than from `src/`, for the reason
// no-devtools.spec.ts gives: a rule can be present in a source file and never
// reach the browser, and the bundle is what the browser applies.
//
// Requires a production build: `npm run build && npm test`.
//
// Only `.css` is read, and deliberately. Component styles are inlined into the
// JavaScript chunks, and one component — the Google sign-in button — hand-rolls
// its own `:focus-visible` ring. Searching the JavaScript would let that single
// button satisfy a rule about every interactive element in the application.
//
// The limit, stated instead of papered over: this proves the rule reaches the
// shipped global stylesheet. It does not prove the rule wins the cascade
// against Material's `outline: none` — specificity and order decide that — and
// it does not prove the ring clears 3:1 against paper in either theme. Both
// belong to the keyboard walkthrough in accessibility.md, which no automated
// check replaces.

// A CSS rule is `selector { body }`. Nested at-rules (`@media`, `@layer`) make
// a naive split wrong in general, but every construct that matters here is a
// flat declaration block, and the alternative — a CSS parser as a dev
// dependency — buys precision this assertion does not need.
function focusVisibleRules(stylesheet: string): string[] {
  return Array.from(
    stylesheet.matchAll(/([^{}]*):focus-visible([^{}]*)\{([^{}]*)\}/g),
  )
    .map((match) => `${match[1]}:focus-visible${match[2]}{${match[3]}}`)
    .filter(
      (rule) =>
        rule.includes('--bud-focus-ring') && rule.includes('outline-offset'),
    );
}

describe('focus ring', () => {
  it('is asserted against a production build', () => {
    expectProductionBuild();
  });

  it('ships a focus-visible ring in the global stylesheet', () => {
    // Arrange
    const stylesheets = emittedFiles(['.css']);

    // Act
    const rules = stylesheets.flatMap((path) =>
      focusVisibleRules(readFileSync(path, 'utf8')),
    );

    // Assert
    // Both properties are required of the same rule, and that pairing is the
    // point. A `:focus-visible` selector that only names the token draws a ring
    // flush against the control, where it reads as a border and disappears into
    // one on a focused field; an `outline-offset` with no token elsewhere in
    // the file is a rule about something other than focus. Counting occurrences
    // of `:focus-visible` across the whole stylesheet would accept both.
    expect(rules.length).toBeGreaterThan(0);
  });
});
