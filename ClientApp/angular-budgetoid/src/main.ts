import { bootstrapApplication } from '@angular/platform-browser';
import { logFailure } from '@app-core/logging/log-failure';
import { AppComponent } from './app/app.component';
import { appConfig } from './app/app.config';

bootstrapApplication(AppComponent, appConfig).catch((err: unknown) =>
  logFailure('Bootstrap failed', err),
);
