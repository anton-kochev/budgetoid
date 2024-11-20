import { CommonModule } from '@angular/common';
import { Component, inject } from '@angular/core';
import { selectUserName } from '@app-state/profile/profile.selectors';
import { Store } from '@ngrx/store';

@Component({
  imports: [CommonModule],
  selector: 'app-home',
  standalone: true,
  templateUrl: './home.component.html',
})
export class HomeComponent {
  private readonly store = inject(Store);

  public readonly userName$ = this.store.select(selectUserName);
}
