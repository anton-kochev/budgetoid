// The one-shot fact the erasure dialog hands to Welcome on its way out. See
// docs/design/components.md, "Erasure dialog", *How the overlay ends*: the word
// travels in memory, never in the address, through a root-provided holder that
// Welcome reads and nothing else writes.
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { ErasureNotice } from './erasure-notice';

describe('ErasureNotice', () => {
  beforeEach(() => {
    // Nothing provided: the holder is `providedIn: 'root'`, and a version that
    // needed listing somewhere would be one a route or a component could end
    // up providing a second copy of — the flow would mark one and Welcome read
    // the other.
    TestBed.configureTestingModule({});
  });

  it('holds nothing until an erasure marks it', () => {
    // Act
    const notice = TestBed.inject(ErasureNotice);

    // Assert
    expect(notice.erased()).toBe(false);
  });

  it('says an erasure happened once marked', () => {
    // Arrange
    const notice = TestBed.inject(ErasureNotice);

    // Act
    notice.mark();

    // Assert
    expect(notice.erased()).toBe(true);
  });

  it('forgets the erasure once cleared', () => {
    // Arrange
    const notice = TestBed.inject(ErasureNotice);
    notice.mark();

    // Act
    notice.clear();

    // Assert
    expect(notice.erased()).toBe(false);
  });

  it('is one holder for the whole application', () => {
    // Act
    const first = TestBed.inject(ErasureNotice);
    first.mark();
    const second = TestBed.inject(ErasureNotice);

    // Assert
    expect(second).toBe(first);
    expect(second.erased()).toBe(true);
  });
});
