import { HttpContextToken } from '@angular/common/http';

// The provider's id token for the two requests that carry it from a hand-off
// rather than from the library's storage: the email change's confirmation and
// the locked sign-in. `apiCredentialsInterceptor` reads it on exactly two
// routes, `EMAIL_CHANGE_PATH` and `LOCKED_SESSION_PATH`, and only once it has
// decided the request is going to our API's origin; on every other route it is
// ignored, set or not. So a token put on the wrong request is dropped, never
// sent.
//
// **Carried on the request, because there is nowhere else to read it from.**
// The two registration routes take their bearer from the library's storage,
// and neither of these returns leaves anything there: `AuthService.initialize`
// discards the library's copy on the return that read it, and a signed-in
// browser's tokens were discarded the moment its session began
// (`SessionService`, through `AuthService.forgetProviderToken`). Each return's
// hand-off — `AuthService.takeEmailChangeReturn` for the email change,
// `AuthService.takeLockedSignInReturn` for the locked sign-in — is the only
// source of its token, and lives in memory alone, so the caller that took it
// passes it here.
//
// **A module of its own, holding the token and nothing else**, for the reason
// `expects-unauthenticated.token.ts` gives: a declaration with no dependencies
// of its own cannot close an import cycle, whatever imports it.
export const PROVIDER_CREDENTIAL = new HttpContextToken<string | null>(
  () => null,
);
