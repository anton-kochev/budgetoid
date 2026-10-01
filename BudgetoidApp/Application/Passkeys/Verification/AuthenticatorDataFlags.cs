namespace Application.Passkeys.Verification;

/// <summary>
/// The packed flag byte at offset 32 of authenticator data.
/// </summary>
[Flags]
public enum AuthenticatorDataFlags : byte
{
    None = 0,

    /// <summary>UP — someone interacted with the authenticator.</summary>
    UserPresent = 0x01,

    /// <summary>UV — the authenticator verified who that someone is.</summary>
    UserVerified = 0x04,

    /// <summary>BE — the credential may be backed up.</summary>
    BackupEligible = 0x08,

    /// <summary>BS — the credential is currently backed up.</summary>
    BackedUp = 0x10,

    /// <summary>AT — attested credential data follows the fixed header.</summary>
    AttestedCredentialData = 0x40,

    /// <summary>ED — an extension-output CBOR map follows everything else.</summary>
    ExtensionData = 0x80,
}
