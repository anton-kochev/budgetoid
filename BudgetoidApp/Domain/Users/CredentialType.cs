namespace Domain.Users;

public enum CredentialType
{
    Passkey,
    Federated,

    /// <summary>
    /// A set of recovery codes, not a single code. One credential row stands for the whole set the
    /// account was issued, and each unredeemed code is a row on <c>recovery_code_hashes</c> hanging
    /// off it — so revoking the set is one delete and redeeming one code leaves the set intact.
    /// </summary>
    RecoveryCodes,
}
