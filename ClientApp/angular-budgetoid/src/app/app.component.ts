import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { ThemeService } from '@app-core/services/theme.service';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet],
  templateUrl: './app.component.html',
  styleUrl: './app.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AppComponent {
  constructor() {
    // Instantiated so ThemeService stays the runtime theme authority (its
    // constructor applies the color scheme and watches the OS preference),
    // even though there's no toggle UI referencing it yet.
    inject(ThemeService);
  }
}
