# Third-party notices

Budgetoid as a whole is licensed under the AGPL-3.0 (see `LICENSE`). A small amount of
material in this repository was copied from third-party projects and remains under the
licence its authors published it under. Those licences are reproduced below, in full, and
each entry names exactly which files here carry the borrowed material.

## SimpleWebAuthn

- Project: https://github.com/MasterKale/SimpleWebAuthn
- Copied from: master at commit `b2f39ba6380d34d4b2625ba16debf2f177be0655`
- Licence: MIT

Borrowed material in this repository:

- `BudgetoidApp/tests/UnitTests/PasskeyVerificationTests.cs` — two golden WebAuthn response
  vectors used as test data. Vector A comes from
  `packages/server/src/registration/verifyRegistrationResponse.test.ts` (the `attestationNone`
  fixture); Vector B from
  `packages/server/src/authentication/verifyAuthenticationResponse.test.ts` (the
  `assertionResponse` fixture).

Licence text as published at `LICENSE.md` in that repository:

```text
MIT License

Copyright (c) 2020 Matthew Miller

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
associated documentation files (the "Software"), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute,
sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial
portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT
NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES
OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```
