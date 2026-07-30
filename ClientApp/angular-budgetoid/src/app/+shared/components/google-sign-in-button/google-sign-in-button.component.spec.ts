import { TestBed } from '@angular/core/testing';
import { authActions } from '@app-state/authentication/authentication.actions';
import { Store } from '@ngrx/store';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { GoogleSignInButtonComponent } from './google-sign-in-button.component';

describe('GoogleSignInButtonComponent', () => {
  const store = { dispatch: vi.fn() };

  beforeEach(async () => {
    store.dispatch.mockClear();
    await TestBed.configureTestingModule({
      imports: [GoogleSignInButtonComponent],
      providers: [{ provide: Store, useValue: store }],
    }).compileComponents();
  });

  it('renders the branded button', () => {
    // Arrange
    const fixture = TestBed.createComponent(GoogleSignInButtonComponent);

    // Act
    fixture.detectChanges();
    const button = (
      fixture.nativeElement as HTMLElement
    ).querySelector<HTMLButtonElement>('button.g-btn');

    // Assert
    expect(button).not.toBeNull();
    expect(button?.textContent).toContain('Continue with Google');

    fixture.destroy();
  });

  it('dispatches the login action when clicked', () => {
    // Arrange
    const fixture = TestBed.createComponent(GoogleSignInButtonComponent);
    fixture.detectChanges();
    const button = (
      fixture.nativeElement as HTMLElement
    ).querySelector<HTMLButtonElement>('button.g-btn');

    // Act
    button?.click();

    // Assert
    expect(store.dispatch).toHaveBeenCalledWith(authActions.login());

    fixture.destroy();
  });
});
