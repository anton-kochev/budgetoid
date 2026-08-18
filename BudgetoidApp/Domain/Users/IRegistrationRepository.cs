using Domain.Budgets;
using Domain.Sessions;

namespace Domain.Users;

/// <summary>
/// Everything one completed registration ceremony brings into existence, already built and already
/// consistent, so the repository adds and saves and decides nothing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every member is an entity rather than the raw material it was built from.</b> Each of the domain
/// factories that produced them reads its owner, its credential and its type off the object beside it —
/// <see cref="PasskeyPublicKey.Register"/>, <see cref="RecoveryCodeHash.From"/>,
/// <see cref="WrappedAccountKeys.For"/>, <see cref="Session.Establish"/> and
/// <see cref="SessionToken.For"/> all take a loaded object rather than loose ids, and each says why. A
/// port taking bytes and identifiers would put every one of those derivations on the far side of a
/// boundary that cannot check them.
/// </para>
/// <para>
/// <b>Three credentials, one passkey factor and ten code factors.</b> The federated credential is what
/// the provider gated on, the passkey is what the account is reached by, and one row stands for the whole
/// issued set of recovery codes. <see cref="WrappedAccountKeys"/> is <b>eleven</b> rows and never two: a
/// factor is not a credential, each code derives its own key-encryption key, and one envelope pair for
/// the whole set would seal the account under one code and leave the other nine unlocking nothing.
/// </para>
/// </remarks>
/// <param name="User">The account, under the identifier the ceremony derived.</param>
/// <param name="DefaultBudget">The nameless budget the account is provisioned with.</param>
/// <param name="FederatedCredential">The provider credential later sign-ins resolve the account by.</param>
/// <param name="PasskeyCredential">The passkey's credential, which the session below is opened over.</param>
/// <param name="RecoveryCodesCredential">The one credential standing for the whole issued set.</param>
/// <param name="PublicKey">The passkey's key material.</param>
/// <param name="SignatureCounter">The passkey's counter, at the value its registration reported.</param>
/// <param name="RecoveryCodeHashes">One row per code, holding the hash of that code's verifier.</param>
/// <param name="WrappedAccountKeys">
/// The passkey factor's share of the account keys, and one share per code.
/// </param>
/// <param name="Session">The session registration signs the person in on.</param>
/// <param name="SessionToken">The handle that session is presented by.</param>
public sealed record Registration(
    User User,
    Budget DefaultBudget,
    Credential FederatedCredential,
    Credential PasskeyCredential,
    Credential RecoveryCodesCredential,
    PasskeyPublicKey PublicKey,
    PasskeySignatureCounter SignatureCounter,
    IReadOnlyList<RecoveryCodeHash> RecoveryCodeHashes,
    IReadOnlyList<WrappedAccountKeys> WrappedAccountKeys,
    Session Session,
    SessionToken SessionToken);

/// <summary>
/// What one attempt to write a whole account came to: it landed, or it lost to an existing row on one of
/// the four unique rules an account can collide on.
/// </summary>
/// <remarks>
/// <b><see cref="SubjectTaken"/> is zero so that <see langword="default"/> is a refusal</b>, the
/// fail-closed direction <see cref="CredentialType"/> and <see cref="SessionKind"/> already take for
/// their own defaults. A member added later goes on the end for the same reason.
/// </remarks>
public enum RegistrationOutcome
{
    /// <summary>An account already resolves from this provider subject.</summary>
    SubjectTaken = 0,

    /// <summary>Every row landed.</summary>
    Registered,

    /// <summary>
    /// The email is spoken for. <b>Ambiguous, and the caller resolves it</b> — a losing insert can breach
    /// the credential's <c>(provider, subject)</c> and the email at once, and PostgreSQL names only one,
    /// picked by write order.
    /// </summary>
    EmailTaken,

    /// <summary>This authenticator's WebAuthn handle is already registered, to some account.</summary>
    AuthenticatorTaken,

    /// <summary>One of the eleven client-minted factor identifiers already stands in the table.</summary>
    FactorTaken,
}

/// <summary>
/// Writes a whole account in one save: the user, its budget, its three credentials, the passkey's key
/// material and counter, ten recovery-code hashes, eleven shares of the account keys, and the session the
/// ceremony signs the person in on.
/// </summary>
public interface IRegistrationRepository
{
    /// <summary>
    /// Adds every entity of <paramref name="registration"/> and saves them once, reporting which unique
    /// rule the attempt lost to when it lost.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This port writes <c>sessions</c> and <c>session_tokens</c> itself, and that is a deliberate
    /// departure from the boundary <c>GenerateRecoveryCodesHandler</c> keeps by hand.</b> That handler
    /// refuses to let <c>IRecoveryCodeRepository</c> open a session, because a repository named for
    /// recovery codes that also opens sessions puts a route's session rule where nobody reading the route
    /// would look — and it can afford the second call, because both saves run inside one
    /// <c>ITransactionalExecutor</c> transaction. <b>There is no transaction here</b>, for the
    /// <c>22P02</c> reason <c>RegisterAccountHandler</c> states at its own call site, so two calls would
    /// be two transactions and the atomicity requirement would be lost — silently, since a committed
    /// account with no session answers 201 and signs nobody in. This repository is named for
    /// the act that opens the session, which is what makes the fold legible rather than surprising.
    /// </para>
    /// <para>
    /// <see cref="ISessionRepository.AddAsync"/>'s own invariant survives untouched:
    /// <see cref="Registration"/> carries the session <b>and</b> its token, so there is still no shape of
    /// any call in this system that writes one without the other.
    /// </para>
    /// <para>
    /// <b>One save, so roughly thirty rows land together or not at all.</b> Half an account is
    /// unreachable and unrepairable in every direction: a user with no credential holds the unique email
    /// forever, a passkey with no wrapped keys is a factor that opens nothing, a set of codes with no
    /// hashes can never be redeemed, and a session with no handle is a person told they are signed in
    /// whose next request is a 401.
    /// </para>
    /// <para>
    /// <b>A refusal is reported rather than thrown, and the sentences live above.</b> Which of the four
    /// collisions a caller is told about, and in what words, depends on what that caller was doing; the
    /// repository knows only which index PostgreSQL named. A <c>23505</c> from any other unique rule
    /// propagates on purpose — it is a constraint this method does not model, and a 500 naming it is more
    /// useful than a confident, specific, false answer.
    /// </para>
    /// </remarks>
    Task<RegistrationOutcome> RegisterAsync(
        Registration registration,
        CancellationToken cancellationToken = default);
}
