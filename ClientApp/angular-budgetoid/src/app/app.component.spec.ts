import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { describe, expect, it } from 'vitest';
import { AppComponent } from './app.component';

describe('AppComponent', () => {
  // The root shell is an outlet and nothing else, and that is the whole of the
  // control over where the navigation appears. Moving the bar up here is the
  // obvious way to build it, and it would draw a signed-in navigation over
  // `/welcome` and over `/register` — screens whose visitors have no account
  // yet. What decides which screens carry a bar is the route table: the shell
  // hangs off `app`, and `welcome` and `register` are its siblings.
  it('renders an outlet and no navigation of its own', () => {
    // Arrange
    TestBed.configureTestingModule({
      imports: [AppComponent],
      providers: [provideRouter([])],
    });
    const fixture = TestBed.createComponent(AppComponent);

    // Act
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;

    // Assert
    expect(host.querySelector('nav')).toBeNull();
    // The other half of "an outlet and nothing else", and the reason this test
    // is not green on a root component with an empty template.
    expect(host.querySelector('router-outlet')).not.toBeNull();

    fixture.destroy();
  });
});
