export type Guid = GuidInternal;

export function guid(value: string): Guid {
  return new GuidInternal(value);
}

export function guidEmpty(): Guid {
  return new GuidInternal('00000000-0000-0000-0000-000000000000');
}

class GuidInternal {
  private readonly value: string = '';

  constructor(value: string | Guid) {
    if (GuidInternal.isValid(value.toString()) === false) {
      throw new Error('Invalid value');
    }
    this.value = value.toString();
  }

  private static readonly isValid = (value: string): boolean => {
    const regex = new RegExp(
      /^[{]?[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}[}]?$/,
    );

    return regex.test(value);
  };

  public toString = (): string => this.value;
}
