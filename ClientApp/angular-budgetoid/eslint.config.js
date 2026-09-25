// @ts-check
const eslint = require('@eslint/js');
const tseslint = require('typescript-eslint');
const angular = require('angular-eslint');

module.exports = tseslint.config(
  {
    ignores: ['.angular/**', 'coverage/**', 'dist/**', 'node_modules/**'],
    linterOptions: {
      reportUnusedDisableDirectives: 'error',
    },
  },
  {
    files: ['**/*.ts'],
    extends: [
      eslint.configs.recommended,
      ...tseslint.configs.recommendedTypeChecked,
      ...tseslint.configs.stylistic,
      ...angular.configs.tsRecommended,
    ],
    languageOptions: {
      parserOptions: {
        projectService: true,
        tsconfigRootDir: __dirname,
      },
    },
    processor: angular.processInlineTemplates,
    rules: {
      '@angular-eslint/component-selector': [
        'error',
        {
          type: 'element',
          prefix: 'app',
          style: 'kebab-case',
        },
      ],
      '@angular-eslint/directive-selector': [
        'error',
        {
          type: 'attribute',
          prefix: 'app',
          style: 'camelCase',
        },
      ],
      '@angular-eslint/prefer-standalone': 'error',

      // Team conventions: explicit, consistent application code.
      '@typescript-eslint/explicit-function-return-type': [
        'error',
        {
          allowExpressions: true,
          allowHigherOrderFunctions: true,
          allowTypedFunctionExpressions: true,
        },
      ],
      '@typescript-eslint/explicit-member-accessibility': [
        'error',
        {
          accessibility: 'explicit',
          overrides: {
            constructors: 'no-public',
          },
        },
      ],
      '@typescript-eslint/naming-convention': [
        'error',
        {
          selector: ['objectLiteralProperty', 'typeProperty'],
          modifiers: ['requiresQuotes'],
          format: null,
        },
        {
          selector: 'default',
          format: ['camelCase'],
        },
        {
          selector: 'variable',
          format: ['camelCase', 'UPPER_CASE'],
        },
        {
          selector: 'memberLike',
          format: ['camelCase'],
        },
        {
          selector: 'memberLike',
          modifiers: ['readonly'],
          format: ['camelCase', 'UPPER_CASE'],
        },
        {
          selector: 'typeLike',
          format: ['PascalCase'],
        },
        {
          selector: 'enumMember',
          format: ['PascalCase'],
        },
        {
          selector: 'function',
          format: ['camelCase'],
        },
      ],
      '@typescript-eslint/no-extraneous-class': [
        'error',
        { allowWithDecorator: true },
      ],
      '@typescript-eslint/no-explicit-any': ['warn', { fixToUnknown: true }],
      '@typescript-eslint/no-shadow': 'error',
      '@typescript-eslint/no-unused-vars': ['error', { args: 'none' }],
      '@typescript-eslint/prefer-readonly': 'error',
      '@typescript-eslint/unbound-method': ['error', { ignoreStatic: true }],

      // No log record may carry an email, a credential subject or a narrative
      // value, so every line goes through `logFailure`, which prints a literal
      // reason and a closed projection of the cause. Three rules because each
      // misses a spelling: `no-console` sees `console.x`, the global rule sees
      // `console` passed or destructured, and the property rule sees it reached
      // through a global object.
      'no-console': 'error',
      'no-restricted-globals': [
        'error',
        {
          name: 'console',
          message: 'Log through logFailure in @app-core/logging/log-failure.',
        },
      ],
      'no-restricted-properties': [
        'error',
        ...['globalThis', 'window', 'self'].map((object) => ({
          object,
          property: 'console',
          message: 'Log through logFailure in @app-core/logging/log-failure.',
        })),
      ],
    },
  },
  {
    // The funnel itself, and the specs and helper that spy on `console` to
    // hold it. Nothing else.
    files: [
      'src/app/+core/logging/log-failure.ts',
      'src/testing/console-spies.ts',
      '**/*.spec.ts',
    ],
    rules: {
      'no-console': 'off',
      'no-restricted-globals': 'off',
      'no-restricted-properties': 'off',
    },
  },
  {
    files: ['**/*.html'],
    extends: [
      ...angular.configs.templateRecommended,
      ...angular.configs.templateAccessibility,
    ],
    rules: {
      '@angular-eslint/template/prefer-control-flow': 'error',
    },
  },
  {
    // Vitest test globals are imported explicitly from 'vitest' in specs,
    // so no ambient test globals are declared here.
    files: ['**/*.spec.ts'],
    rules: {
      '@typescript-eslint/unbound-method': 'off',
    },
  },
);
