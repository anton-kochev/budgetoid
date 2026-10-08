namespace Application.Registration;

/// <summary>
/// Sentences both legs of <c>/api/registration</c> answer a conflict with, held in one place because both
/// say them.
/// </summary>
/// <remarks>
/// <para>
/// A refusal reached from two routes for one cause has to read identically on both, or a person who meets
/// the options leg on one visit and the finish leg on the next is told two stories about a single fact.
/// Every conflict in this product answers 409 under one title, so the detail sentence <em>is</em> the
/// whole of what a <em>person</em> is told — which makes "byte-for-byte the same" a requirement rather
/// than tidiness. Only the sentences with more than one speaker live here; the refusals
/// <c>RegisterAccountHandler</c> alone says stay private to it.
/// </para>
/// <para>
/// <b>The machine-readable half is not held here and that is deliberate.</b> A conflict also carries a
/// <see cref="Domain.Common.ConflictKind"/>, and the same "identical on both legs" requirement applies to
/// it — but it is an enum member rather than a string, so the two legs naming
/// <see cref="Domain.Common.ConflictKind.SubjectAlreadyRegistered"/> are already spelling one thing and
/// there is no second copy to keep in step. A constant here would be a second name for a value that
/// cannot drift.
/// </para>
/// </remarks>
internal static class RegistrationConflicts
{
    /// <summary>
    /// The sentence a caller whose provider identity already has an account is answered with, on either
    /// leg of the ceremony.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It says what to do next rather than only what went wrong, and that is worth saying to <em>both</em>
    /// of its callers even though they stand at opposite ends of the ceremony. On the finish leg the
    /// caller is holding a completed ceremony and a card of codes their client has very likely already
    /// shown somebody, and the useful fact is that none of it is needed. On the options leg nothing has
    /// been minted or shown yet, and the useful fact is the same one arriving earlier: this identity
    /// already has a way in, so there is nothing to create and two ways to get back to it.
    /// </para>
    /// <para>
    /// It names neither an account nor an address. What it reports is a fact about the identity the caller
    /// has already proved they hold, which is the whole reason it may be said out loud — see the
    /// enumeration argument in <see cref="BeginAccountRegistrationHandler"/>.
    /// </para>
    /// </remarks>
    internal const string SubjectAlreadyRegisteredMessage =
        "This Google account is already registered. Sign in with the passkey it holds, or redeem a "
        + "recovery code.";
}
