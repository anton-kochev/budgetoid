import { HttpContextToken } from '@angular/common/http';

// The provider's id token for the one request that must carry it from a
// browser already holding a session: the email change's confirmation.
// `apiCredentialsInterceptor` reads it on exactly one route,
// `EMAIL_CHANGE_PATH`, and only once it has decided the request is going to
// our API's origin; on every other route it is ignored, set or not. So a
// token put on the wrong request is dropped, never sent.
//
// **Carried on the request, because there is nowhere else to read it from.**
// The two registration routes take their bearer from the library's storage,
// and a signed-in browser holds nothing there: the provider's tokens were
// discarded the moment its session began (`SessionService`, through
// `AuthService.forgetProviderToken`). The email-change return's hand-off,
// `AuthService.takeEmailChangeReturn`, is the only source of this token, and
// it lives in memory alone — so the caller that took it passes it here.
//
// **A module of its own, holding the token and nothing else**, for the reason
// `expects-unauthenticated.token.ts` gives: a declaration with no dependencies
// of its own cannot close an import cycle, whatever imports it.
export const PROVIDER_CREDENTIAL = new HttpContextToken<string | null>(
  () => null,
);
