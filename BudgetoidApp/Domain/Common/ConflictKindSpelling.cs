namespace Domain.Common;

/// <summary>
/// How a <see cref="ConflictKind"/> member is written down: the single token that stands for it in the
/// <c>conflictKind</c> extension member every 409 in this product carries.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here is derived from a member's name.</b> Not <see cref="object.ToString"/>, not a naming
/// policy over it. The precedent is <c>Domain.Users.CredentialTypeSpelling</c> and the argument is the
/// same one: a member name and a wire value that drift apart is a defect nothing reddens, because a
/// rename that reads as tidying up silently reissues the contract under a new word. Writing each token
/// out makes adding a <see cref="ConflictKind"/> member a decision somebody takes here, at compile time,
/// beside the other tokens they have to be unlike.
/// </para>
/// <para>
/// <b>It lives in the Domain because the kind does.</b> A remedy is a fact about the rule that refused
/// the request, and the ring that raises the conflict is the one that knows it; <c>Api</c> renders the
/// token and owns none of it. Both rings reference this one, and it references neither.
/// </para>
/// <para>
/// <b>There is no inverse, deliberately.</b> Nothing on this side reads a conflict kind back — the token
/// is written to a response and never parsed from one — so a <c>TryParse</c> beside <see cref="Of"/>
/// would be a member with no caller, and the first thing a reader would do with it is find somewhere to
/// accept a kind from a client.
/// </para>
/// </remarks>
public static class ConflictKindSpelling
{
    private const string DuplicateIdentifierSpelling = "duplicate_identifier";

    private const string DuplicateNameSpelling = "duplicate_name";

    private const string SubjectAlreadyRegisteredSpelling = "subject_already_registered";

    private const string EmailAlreadyLinkedSpelling = "email_already_linked";

    private const string AuthenticatorAlreadyRegisteredSpelling = "authenticator_already_registered";

    private const string FactorAlreadyRegisteredSpelling = "factor_already_registered";

    private const string LastPasskeySpelling = "last_passkey";

    private const string RecoveryCodesReplacedSpelling = "recovery_codes_replaced";

    /// <summary>The token standing for <paramref name="kind"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="kind"/> is not a declared member. Reachable two ways, and both are defects at the
    /// site that produced the value rather than here: a member was added to <see cref="ConflictKind"/>
    /// and nobody chose a token for it, or <c>default</c> was written where a kind was required — no
    /// member takes the value zero, which is what makes that second case loud. <see cref="ConflictException"/>
    /// calls this from its constructor so the failure carries the stack of the code that raised the
    /// conflict, rather than surfacing later inside the handler that was about to write the response.
    /// </exception>
    public static string Of(ConflictKind kind) => kind switch
    {
        ConflictKind.DuplicateIdentifier => DuplicateIdentifierSpelling,
        ConflictKind.DuplicateName => DuplicateNameSpelling,
        ConflictKind.SubjectAlreadyRegistered => SubjectAlreadyRegisteredSpelling,
        ConflictKind.EmailAlreadyLinked => EmailAlreadyLinkedSpelling,
        ConflictKind.AuthenticatorAlreadyRegistered => AuthenticatorAlreadyRegisteredSpelling,
        ConflictKind.FactorAlreadyRegistered => FactorAlreadyRegisteredSpelling,
        ConflictKind.LastPasskey => LastPasskeySpelling,
        ConflictKind.RecoveryCodesReplaced => RecoveryCodesReplacedSpelling,
        _ => throw new ArgumentOutOfRangeException(
            nameof(kind),
            kind,
            $"No spelling is defined for this {nameof(ConflictKind)} member."),
    };
}
