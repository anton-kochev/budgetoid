// @ts-check
const eslint = require('@eslint/js');
const tseslint = require('typescript-eslint');
const angular = require('angular-eslint');

module.exports = tseslint.config(
  {
    files: ['**/*.ts'],
    extends: [
      eslint.configs.recommended,
      ...tseslint.configs.recommended,
      ...tseslint.configs.stylistic,
      ...angular.configs.tsRecommended,
    ],
    processor: angular.processInlineTemplates,
    rules: {
      'lines-between-class-members': [
        'error',
        'always',
        { exceptAfterSingleLine: true },
      ],
      'max-classes-per-file': ['warn', 1],
      'max-len': 'off',
      'no-shadow': 'off',
      'no-undef': 'error',
      'no-undef-init': 'error',
      'no-undefined': 'warn',
      'no-void': 'error',
      'no-underscore-dangle': [
        'warn',
        { allow: ['_desc'], allowAfterThis: true },
      ],
      'no-unused-vars': 'off',
      'padding-line-between-statements': [
        'warn',
        {
          blankLine: 'always',
          prev: '*',
          next: 'return',
        },
        {
          blankLine: 'always',
          prev: ['const', 'let', 'var'],
          next: '*',
        },
        {
          blankLine: 'any',
          prev: ['const', 'let', 'var'],
          next: ['const', 'let', 'var'],
        },
      ],
      // 'prefer-arrow/prefer-arrow-functions': [
      //   'warn',
      //   {
      //     allowStandaloneDeclarations: true,
      //     classPropertiesAllowed: false,
      //     disallowPrototype: true,
      //     singleReturnOnly: false,
      //   },
      // ],
      '@angular-eslint/directive-selector': [
        'error',
        {
          type: 'attribute',
          prefix: 'app',
          style: 'camelCase',
        },
      ],
      '@angular-eslint/component-selector': [
        'error',
        {
          type: 'element',
          prefix: 'app',
          style: 'kebab-case',
        },
      ],
      // '@typescript-eslint/explicit-function-return-type': [
      //   'error',
      //   {
      //     allowExpressions: true,
      //     allowTypedFunctionExpressions: true,
      //     allowHigherOrderFunctions: true,
      //   },
      // ],
      // '@typescript-eslint/explicit-member-accessibility': [
      //   'error',
      //   {
      //     accessibility: 'explicit',
      //     overrides: {
      //       constructors: 'no-public',
      //     },
      //   },
      // ],
      // '@typescript-eslint/naming-convention': [
      //   'error',
      //   {
      //     selector: 'default',
      //     format: ['camelCase'],
      //   },
      //   {
      //     selector: 'variable',
      //     format: ['camelCase', 'UPPER_CASE'],
      //   },
      //   {
      //     selector: 'memberLike',
      //     format: ['camelCase'],
      //   },
      //   {
      //     selector: 'memberLike',
      //     modifiers: ['readonly'],
      //     format: ['camelCase', 'UPPER_CASE'],
      //   },
      //   {
      //     selector: 'typeLike',
      //     format: ['PascalCase'],
      //   },
      //   {
      //     selector: 'enumMember',
      //     format: ['PascalCase'],
      //   },
      //   {
      //     selector: 'function',
      //     format: ['camelCase'],
      //   },
      // ],
      // '@typescript-eslint/no-explicit-any': [
      //   'warn',
      //   {
      //     fixToUnknown: true,
      //   },
      // ],
      // '@typescript-eslint/no-shadow': ['error', {}],
      // '@typescript-eslint/no-unused-vars': [
      //   'error',
      //   {
      //     args: 'none',
      //   },
      // ],
      // '@typescript-eslint/prefer-readonly': 'error',
    },
  },
  {
    files: ['**/*.html'],
    extends: [
      ...angular.configs.templateRecommended,
      ...angular.configs.templateAccessibility,
    ],
    rules: {},
  },
);
