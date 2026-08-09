import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { BaseApiService } from './base-api.service';

export interface MeDto {
  email: string;
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
}
