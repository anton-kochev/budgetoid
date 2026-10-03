import { ChangeDetectionStrategy, Component } from '@angular/core';

// The screen a locked session reaches, and the only one. Only its heading
// stands so far; the rest of it is specified in docs/design/components.md,
// "Releasing an account". One `h1`, the same in every state.
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<h1>Release your account</h1>`,
})
export class ReleaseComponent {}
