// The book's error colour has to reach a refused field, and two declarations in
// `styles.scss` are what make it. This spec is what stops either being deleted
// quietly.
//
// **Two, because Material's form field reads two roles and not one.** Thirteen
// of its eighteen error tokens fall back to `var(--mat-sys-error)`, which the
// first override aliases to `--bud-over`. The other five are the hover states
// and fall back to `var(--mat-sys-on-error-container)` — a different role, so
// no spelling of the first override can reach them. The second aliases that
// role, and its container, to the pair the book mints for exactly this.
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
// **The hover half is asked as a cascade question, because it cannot be asked
// as a painting one.** jsdom matches no `:hover`, so nothing here can put a
// pointer on the field and read what came out. What it can do is find the rules
// Material would apply if something did — discovered from the stylesheets in
// the page, never named in this file — and resolve the values they carry
// through the same walk. So the claim is where those declarations *point*,
// which is the half a theme declaration decides.
//
// **What this cannot see, said rather than implied.** jsdom computes no colour,
// so nothing here judges `light-dark(#b23a2e, #e06a55)`: a `--bud-over` set to
// a value no browser can parse passes. It also does not paint, so a rule
// covering the message with something else, a hover rule some later sheet
// out-specifies, or a screen that never puts a control into its error state, is
// outside it. Proving those needs a real engine — a browser-mode runner or a
// screenshot — and this suite has neither. Nor does it read
// `branding/tokens.css`: the values it walks are the runtime copy in
// `assets/theming/_brand-tokens.scss`, and the two files being in step is held
// by review.
//
// Requires a production build: `npm run build && npm test`.
import { readFileSync } from 'node:fs';
import { basename } from 'node:path';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { beforeAll, describe, expect, it } from 'vitest';
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

/**
 * One thing Material paints on a refused field under a pointer: where it
 * paints it, which property it paints, and the value the stylesheet gives.
 */
interface HoverDeclaration {
  readonly selector: string;
  readonly property: string;
  readonly value: string;
}

// `in` rather than `instanceof`: the constructors the runner's jsdom realm
// publishes are not always the ones this module closed over, and a narrowing
// that silently goes false would empty the scan and pass over nothing.
function isStyleRule(rule: CSSRule): rule is CSSStyleRule {
  return 'selectorText' in rule && 'style' in rule;
}

/**
 * The hover declarations Material makes about a refused field, **read out of
 * the stylesheets in the page** rather than written down here.
 *
 * Written down, this would be a transcription: it would keep passing for a
 * Material that renamed the tokens, moved them to another selector or stopped
 * emitting them, because the names would still be in this file. Discovered,
 * an empty result is a finding — which is why the caller refuses one.
 */
function hoverErrorDeclarations(): HoverDeclaration[] {
  const declarations: HoverDeclaration[] = [];

  for (const sheet of Array.from(document.styleSheets)) {
    for (const rule of Array.from(sheet.cssRules)) {
      if (!isStyleRule(rule)) {
        continue;
      }

      const selector = rule.selectorText;

      // A rule that reaches this field only while a pointer rests on it, and
      // only while it is refused. Both halves, or `:hover` alone would drag in
      // every ordinary hover Material draws.
      if (!selector.includes(':hover') || !selector.includes('invalid')) {
        continue;
      }

      for (let index = 0; index < rule.style.length; index += 1) {
        const property = rule.style.item(index);
        const value = rule.style.getPropertyValue(property).trim();

        // Keyed on `error-hover` in the token's own name rather than on the
        // `--mat-form-field-` prefix: the prefix is a component's, and a
        // Material that moved these tokens under another one would then leave
        // the scan empty — which the caller reads as a finding, but a
        // misleading one. Keyed on the *value* rather than the property,
        // because the property is `color` here and `border-color` there and
        // says nothing about which role is being read.
        if (/^var\(\s*--[\w-]*error-hover-/.test(value)) {
          declarations.push({ selector, property, value });
        }
      }
    }
  }

  return declarations;
}

/**
 * Renders the host and hands back the element Material drew for the field.
 *
 * Touched, because Material's default error-state matcher shows a message for
 * a control somebody has been in or a form that was submitted, and an
 * untouched one renders no `mat-error` to read — and it is the same act that
 * puts `mdc-text-field--invalid` on the field the hover rules select.
 */
function renderRefusedField(): HTMLElement {
  TestBed.configureTestingModule({ imports: [RefusedFieldHost] });

  const fixture = TestBed.createComponent(RefusedFieldHost);

  fixture.componentInstance.control.markAsTouched();
  fixture.detectChanges();

  return fixture.nativeElement as HTMLElement;
}

describe('a refused field', () => {
  // Once for the file: the sheet is appended to `document.head`, which no
  // `TestBed` reset clears, so a per-case injection would stack copies of it.
  beforeAll(() => {
    expectProductionBuild();
    serveGlobalStylesheet();
  });

  it('takes its colour from the book and not from Material', () => {
    // Arrange
    const host = renderRefusedField();
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

  it('keeps a colour the book owns under a pointer', () => {
    // Arrange
    const host = renderRefusedField();
    const field = host.querySelector<HTMLElement>('mat-form-field');

    if (field === null) {
      throw new Error('Material rendered no form field');
    }

    const declarations = hoverErrorDeclarations();

    // Act
    // Resolved from the field, so a declaration made anywhere between it and
    // the root is honoured — the same walk the resting case does, applied to
    // the values the hover rules carry. jsdom matches no `:hover`, so what is
    // asked here is where those values *point*, which is a cascade question
    // and not a painting one.
    const book = resolveTokens(field, 'var(--bud-over-on-container)');
    const strays = declarations
      .map((declaration) => ({
        ...declaration,
        resolved: resolveTokens(field, declaration.value),
      }))
      .filter((declaration) => declaration.resolved !== book);

    // Assert
    // Both guards are against a vacuous pass and neither is decoration: an
    // empty scan filters to an empty list, and a book colour that resolved to
    // nothing would be matched by every declaration that also resolved to
    // nothing.
    expect(declarations).not.toHaveLength(0);
    expect(book).not.toBe('');
    // `toEqual([])` rather than a length: the diff then names every
    // declaration that strayed, and what it resolved to instead.
    expect(strays).toEqual([]);
  });

  it('gives Material the error container the book mints', () => {
    // Arrange
    // Read at the root, because nothing this application renders reads the
    // container half — the pair is minted together and aliased together, and
    // half an alias leaves Material pairing the book's red against Material's
    // own tint wherever a component does read it.
    const root = document.documentElement;

    // Act
    const material = {
      container: resolveTokens(root, 'var(--mat-sys-error-container)'),
      onContainer: resolveTokens(root, 'var(--mat-sys-on-error-container)'),
    };
    const book = {
      container: resolveTokens(root, 'var(--bud-over-container)'),
      onContainer: resolveTokens(root, 'var(--bud-over-on-container)'),
    };

    // Assert
    // `mat.theme-overrides` drops a key it does not recognise **silently** —
    // no warning, no error, no declaration — so a misspelled role would leave
    // both sides of this comparison holding Material's own values and nothing
    // else in the suite would notice.
    expect(book.container).not.toBe('');
    expect(book.onContainer).not.toBe('');
    expect(material).toEqual(book);
  });
});
