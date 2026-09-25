namespace Domain.Users;

// Federated is declared first so that default(CredentialType) — the value a struct default, a
// zero-initialised buffer, or a deserializer that saw no member produces — describes a row the
// database refuses: CK_credentials_type_shape's federated arm demands a provider and a subject, and a
// credential nobody initialised carries neither. Passkey first would make that same accident a
// perfectly storable row, because the passkey arm requires both to be null, which is exactly what a
// zero-initialised Credential holds. The order is the fail-closed direction — the same choice
// SessionKind records — not alphabetical or historical accident; both directions of
// CredentialTypeSpelling store and read text, so reordering moves no data and no ordinal is persisted
// anywhere.
public enum CredentialType
{
    Federated,
    Passkey,

    /// <summary>
    /// A set of recovery codes, not a single code. One credential row stands for the whole set the
    /// account was issued, and each unredeemed code is a row on <c>recovery_code_hashes</c> hanging
    /// off it — so revoking the set is one delete and redeeming one code leaves the set intact.
    /// </summary>
    RecoveryCodes,
}
