// The book's error colour has to reach a refused field, and one declaration in
// `styles.scss` is what makes it. This spec is what stops that declaration
// being deleted quietly.
//
// **It is not a transcription, and the difference is the point.** A case
// asserting that `--mat-sys-error: var(--bud-over)` appears in a file passes
// whatever Material does with the token, and passes for a screen that renders
// no error at all. What is asserted here instead is a chain with a real end at
// each side: a `mat-error` **rendered** by Material under Material's own
// stylesheet, and `--bud-over` as the theme declares it. Between them the
// spec resolves the `var()` references the cascade actually delivered.
//
// **Three halves have to be brought into one document, and each is measured
// rather than assumed.** Material's form-field stylesheet arrives on its own —
// `TestBed` injects a component's styles into the page, and Material's
// components are `ViewEncapsulation.None`, so `.mat-mdc-form-field-error` is
// there unscoped. The **global** stylesheet is not: the unit-test builder emits
// `styles.css` and the jsdom page links nothing, so the sheet is read out of
// `dist/` — the bytes a browser is served — and injected. And jsdom performs no
// `var()` substitution: `getComputedStyle(node).color` comes back as the
// literal `var(--mat-form-field-error-text-color, var(--mat-sys-error))` the
// cascade put there, which is why the walk below exists.
//
// **What this cannot see, said rather than implied.** jsdom computes no colour,
// so nothing here judges `light-dark(#b23a2e, #e06a55)`: a `--bud-over` set to
// a value no browser can parse passes. It also does not paint, so a rule
// covering the message with something else, or a screen that never puts a
// control into its error state, is outside it. Proving those needs a real
// engine — a browser-mode runner or a screenshot — and this suite has neither.
//
// Requires a production build: `npm run build && npm test`.
import { readFileSync } from 'node:fs';
import { basename } from 'node:path';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { describe, expect, it } from 'vitest';
import { emittedFiles, expectProductionBuild } from './production-bundle';

/**
 * A form field in the state the chapter is about: a control the server or a
 * validator has refused, with the message Material renders for it.
 *
 * Deliberately not one of the three screens. The claim is about the theme, and
 * a screen would bring its own services, its own custody status and its own
 * reasons to fail — none of which this is asking about. What it does bring is
 * Material's real form-field stylesheet, which is the half that cannot be
 * faked.
 */
@Component({
  imports: [ReactiveFormsModule, MatFormFieldModule, MatInputModule],
  template: `
    <mat-form-field>
      <mat-label>Name</mat-label>
      <input matInput [formControl]="control" />
      <mat-error>Enter a name.</mat-error>
    </mat-form-field>
  `,
})
class RefusedFieldHost {
  public readonly control = new FormControl('', Validators.required);
}

// A value that is exactly one `var()` reference, with or without a fallback.
// Anything else — a colour, a `light-dark()`, a keyword — is where the walk
// stops.
const VAR_REFERENCE = /^var\(\s*(--[\w-]+)\s*(?:,([\s\S]*))?\)$/;

// The emitted global stylesheet, injected into the page the fixture renders
// into. One file, and the spec insists on that: silently reading the wrong one
// would make every assertion below an assertion about nothing.
function serveGlobalStylesheet(): void {
  const sheets = emittedFiles(['.css']).filter((path) =>
    basename(path).startsWith('styles-'),
  );

  expect(sheets).toHaveLength(1);

  const style = document.createElement('style');

  style.textContent = readFileSync(sheets[0], 'utf8');
  document.head.append(style);
}

// The declared value of a custom property as this element sees it.
//
// **Inheritance is walked by hand**, because jsdom resolves the cascade per
// element and does not inherit custom properties down the tree. Upwards from
// the element rather than straight to the root, so a declaration made anywhere
// between them is honoured — including a `:host` copy, which is what this
// change removed and what the walk must not pretend cannot exist.
function declaredValue(element: Element, property: string): string {
  for (
    let node: Element | null = element;
    node !== null;
    node = node.parentElement
  ) {
    const value = getComputedStyle(node).getPropertyValue(property).trim();

    if (value !== '') {
      return value;
    }
  }

  return '';
}

/**
 * Follows a declared value through the custom properties it names.
 *
 * An undeclared property falls to the `var()` fallback, which is how Material
 * reaches the system token; a property that resolves to nothing at all answers
 * `''`, which every caller here has to refuse rather than compare.
 */
function resolveTokens(
  element: Element,
  value: string,
  seen: readonly string[] = [],
): string {
  const reference = VAR_REFERENCE.exec(value.trim());

  if (reference === null) {
    return value.trim();
  }

  const [, property, fallback] = reference;

  if (seen.includes(property)) {
    throw new Error(`custom property cycle at ${property}`);
  }

  const declared = declaredValue(element, property);
  const next = declared === '' ? fallback : declared;

  return next === undefined
    ? ''
    : resolveTokens(element, next, [...seen, property]);
}

describe('a refused field', () => {
  it('takes its colour from the book and not from Material', () => {
    // Arrange
    expectProductionBuild();
    serveGlobalStylesheet();
    TestBed.configureTestingModule({ imports: [RefusedFieldHost] });

    const fixture = TestBed.createComponent(RefusedFieldHost);

    // Touched, because Material's default error-state matcher shows a message
    // for a control somebody has been in or a form that was submitted, and an
    // untouched one renders no `mat-error` to read.
    fixture.componentInstance.control.markAsTouched();
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const message = host.querySelector<HTMLElement>('mat-error');

    // Thrown rather than expected, so the rest of the case cannot run against
    // a node that is not there: a missing message is Material no longer
    // rendering one, which is a finding and not a value to compare.
    if (message === null) {
      throw new Error('Material rendered no message for a refused control');
    }

    // Act
    const rendered = resolveTokens(message, getComputedStyle(message).color);
    const book = resolveTokens(document.documentElement, 'var(--bud-over)');

    // Assert
    // The guard against a vacuous pass, and it is not decoration: a stylesheet
    // that failed to arrive leaves *both* sides `''`, and the comparison below
    // would then hold over a page with no theme in it at all.
    expect(book).not.toBe('');
    expect(rendered).toBe(book);
  });
});
