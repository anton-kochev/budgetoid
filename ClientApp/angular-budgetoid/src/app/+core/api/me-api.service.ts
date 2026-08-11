import { Injectable } from '@angular/core';
import { map, Observable } from 'rxjs';
import { BaseApiService } from './base-api.service';

export interface MeDto {
  email: string;
}

// One member, and the route will never grow another: no id, no issued instant,
// no total, and above all no hash. It is unwrapped at this boundary rather than
// carried inward, because the screen renders a number and a one-member envelope
// is a shape only the wire has a use for.
interface RecoveryCodeCountDto {
  remaining: number;
}

// The three kinds of thing that can sign this account in, and the union is
// closed on purpose: a fourth kind is a change to what the screen must render,
// not a string that arrives one day and falls through a template. `federated`
// is the provider sign-in; the response carries no provider subject and never
// will, so there is nothing here to render but the kind and when it was
// attached.
//
// `recovery_codes` is the schema's own token and is deliberately not
// camel-cased. It is a discriminant *value*, not a property name: the naming
// policy that turns `CreatedAtUtc` into `createdAtUtc` applies to members, and
// `recoveryCodes` is what applying it here would produce — a spelling that
// agrees with nothing on either side of the wire. A set is listed by
// `GET /api/me/credentials` like any other way in, because redeeming a code
// opens a full session.
export type CredentialKind = 'passkey' | 'federated' | 'recovery_codes';

export interface CredentialSummary {
  id: string;
  type: CredentialKind;
  // The stored instant, as the server wrote it, always carrying the `Z`
  // designator. It stays a string all the way to the `<time datetime>`
  // attribute; the reader's calendar day is computed from it separately by
  // `credential-registration-date.ts`, which is the only place that converts.
  createdAtUtc: string;
}

@Injectable({ providedIn: 'root' })
export class MeApiService extends BaseApiService {
  public getMe(): Observable<MeDto> {
    return this.get<MeDto>('api/me');
  }

  // The export is bytes, never a parsed document, and that is the whole point
  // of the feature rather than a stylistic choice. Amounts ship as JSON numbers
  // at `numeric(14,4)` scale, which is exact for the .NET writer but not for a
  // JavaScript reader: JSON.parse turns them into IEEE-754 doubles whose
  // significand does not cover that column's range. Anything that parses the
  // response and re-serializes it — including `get<ExportDocument>()`, which is
  // what this will look like it should have been — silently degrades the very
  // file the feature exists to hand over. The client is a pipe: it reads no
  // property of the document and writes the bytes it received to disk.
  // See docs/business-logic/export.md, "Money ships as JSON numbers".
  public getExport(): Observable<Blob> {
    return this.getBlob('api/me/export');
  }

  // Ascending by `createdAtUtc`, as the server sends it. The array is `readonly`
  // from here down: nothing in the client sorts, filters or appends to it, and
  // saying so keeps a caller from reordering a list whose order is the server's
  // statement rather than the screen's preference.
  public getCredentials(): Observable<readonly CredentialSummary[]> {
    return this.get<readonly CredentialSummary[]>('api/me/credentials');
  }

  // How many codes are left, and nothing else. An account that has never
  // generated a set answers `0` rather than `404` — zero codes left is an
  // answer, and the two readings of it ("never generated" and "all spent")
  // share a next step, so nothing downstream needs them told apart. Whatever
  // consumes this must therefore keep `0` and "no answer yet" apart itself;
  // collapsing them is the one defect this whole path is shaped to prevent.
  //
  // There is deliberately **no** counterpart that generates a set.
  // `POST /api/me/recovery-codes` takes five members of a fresh WebAuthn
  // assertion this client cannot produce, so a method for it would be API
  // surface no test could execute — a signature that compiles, is called by
  // nothing, and is wrong in a way nothing on the screen would show.
  public getRecoveryCodes(): Observable<number> {
    return this.get<RecoveryCodeCountDto>('api/me/recovery-codes').pipe(
      map((count) => count.remaining),
    );
  }
}
