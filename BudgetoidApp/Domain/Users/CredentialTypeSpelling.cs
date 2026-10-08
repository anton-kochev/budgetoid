namespace Domain.Users;

/// <summary>
/// How a <see cref="CredentialType" /> member is written down: the single token that stands for it in
/// the <c>credentials.type</c> column, in every copy of that column the schema carries, and in the
/// <c>type</c> member a credential leaves <c>GET /api/me/credentials</c> under.
/// </summary>
/// <remarks>
/// <para>
/// <b>It lives in the Domain because two layers have to agree on it and neither may be the other's
/// source.</b> The persistence configurations own the column and the API owns the wire; while each
/// spelled the vocabulary for itself, "the wire agrees with the column" was a coincidence that held
/// only as long as nobody edited one side — and it stopped holding the moment a member was declared
/// whose name camel-cases to something the column does not say. Reading one definition from both sides
/// turns that agreement into a fact. The Domain is the lowest layer both can reach: <c>Api</c> and
/// <c>Infrastructure</c> both reference it, and it references neither, so the spelling can be shared
/// without pointing a dependency edge outward.
/// </para>
/// <para>
/// <b>Nothing here is derived from a member's name.</b> Not <see cref="object.ToString" />, not a
/// naming policy over it: the column's vocabulary is snake_case and a member's name is PascalCase, so
/// any transformation between the two is a rule about how members happen to be spelled today rather
/// than a decision about what the schema stores. Writing each token out is what makes adding a
/// <see cref="CredentialType" /> member a decision somebody takes here at compile time — and what makes
/// it a decision that has to come with a migration, because <c>CK_credentials_type</c> bounds the same
/// vocabulary.
/// </para>
/// <para>
/// <b>What this deliberately does not do is decide which spellings a given table accepts.</b>
/// <c>passkey_public_keys</c> and <c>passkey_signature_counters</c> take a copy of
/// <c>credentials.type</c> that may only ever say <c>passkey</c> or <c>federated</c> — a signature
/// counter cannot hang off a set of recovery codes — and each keeps that refusal of its own on top of
/// the spelling it reads here. A restriction folded into this type would become a restriction on
/// everything reading it.
/// </para>
/// </remarks>
public static class CredentialTypeSpelling
{
    private const string PasskeySpelling = "passkey";

    private const string FederatedSpelling = "federated";

    private const string RecoveryCodesSpelling = "recovery_codes";

    /// <summary>The token standing for <paramref name="type" />.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="type" /> is not a declared member. Unreachable from anything the domain can
    /// produce: it means a member was added to <see cref="CredentialType" /> and nobody chose a spelling
    /// for it here, or an undeclared value was cast into the enum. Either way the enum member is what is
    /// wrong, not the place the token was about to be written to.
    /// </exception>
    public static string Of(CredentialType type) => type switch
    {
        CredentialType.Passkey => PasskeySpelling,
        CredentialType.Federated => FederatedSpelling,
        CredentialType.RecoveryCodes => RecoveryCodesSpelling,
        _ => throw new ArgumentOutOfRangeException(
            nameof(type),
            type,
            $"No spelling is defined for this {nameof(CredentialType)} member."),
    };

    /// <summary>
    /// The inverse of <see cref="Of" />: the member <paramref name="spelling" /> stands for, if any.
    /// </summary>
    /// <remarks>
    /// A <c>Try</c> shape rather than a throwing parse, because every caller of the inverse today is
    /// reading a column and each one has a different thing to say about a value it cannot read — which
    /// column held it, and which constraint or foreign key should have refused it before it was ever
    /// stored. A throwing parse here would either lose that attribution or force each caller to catch an
    /// exception to restore it. Case-sensitive and untrimmed on purpose: the tokens are a closed
    /// vocabulary a CHECK constraint also bounds, so <c>PASSKEY</c> is not a lenient spelling of
    /// anything, it is a value the database would have refused.
    /// </remarks>
    public static bool TryParse(string spelling, out CredentialType type)
    {
        CredentialType? parsed = spelling switch
        {
            PasskeySpelling => CredentialType.Passkey,
            FederatedSpelling => CredentialType.Federated,
            RecoveryCodesSpelling => CredentialType.RecoveryCodes,
            _ => null,
        };

        type = parsed.GetValueOrDefault();

        return parsed.HasValue;
    }
}
