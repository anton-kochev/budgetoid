using System.Reflection;
using Domain.Budgets;

namespace UnitTests;

/// <summary>
/// Pins the rule that a row never changes tenant: every <c>UserId</c> and <c>BudgetId</c> the Domain
/// declares is written once, at construction, and by nothing afterwards.
/// </summary>
/// <remarks>
/// <para>
/// <c>credentials.user_id</c> carries this twice over. It is one of the tables exempt from row-level
/// security — it has to be read before the request has an identity a policy could key on — so
/// nothing beneath the application re-checks the owner, and
/// <see href="../../../docs/decisions/0014-scope-the-credential-delete-in-the-application.md">ADR
/// 0014</see> scopes the credential delete by the loaded entity for exactly that reason. A settable
/// <c>Credential.UserId</c> makes that delete forgeable, and no policy underneath would notice.
/// </para>
/// <para>
/// Until now this was spot-checked in three places, each naming its own type: <c>Budget</c> and
/// <c>Account</c> in <c>DomainImmutabilityTests</c>, and <c>Transaction.BudgetId</c> in
/// <c>TransactionTests</c>. Seven of the ten keys were pinned by nothing — <c>Credential.UserId</c>
/// among them — and, worse, a new entity carrying a key arrived with no check at all, because a
/// per-type test cannot fail for a type nobody has written yet. Deriving the subject from the
/// assembly is what closes that.
/// </para>
/// <para>
/// That argument is no longer hypothetical. <c>WrappedAccountKeys</c> arrived carrying a
/// <c>UserId</c> and no test of its own naming it, and the pinned set below is what said so — by
/// failing, on purpose, until a human wrote the literal. The set is deliberately a checkpoint and
/// not a derived list: a test that widened itself to whatever the assembly happens to declare would
/// have accepted the new key in silence, and accepting it in silence is the one outcome this file
/// exists to prevent.
/// </para>
/// <para>
/// <b>The two-property overlap with those tests is deliberate; do not de-duplicate it.</b> They rest
/// on different reasons: a budget is not renamed or re-owned because that is the business rule about
/// what a budget is, whereas a row does not change tenant because isolation depends on it. Two
/// guards over one property for two reasons is the same arrangement as the RLS policy and the EF
/// query filter, and collapsing them would leave whichever reason survives holding both weights.
/// </para>
/// <para>
/// <b>What this cannot see:</b> a method that reassigns the key. Reflection over properties says
/// nothing about a <c>Reassign(Guid)</c> beside them; the pinned method-signature surface in
/// <c>DomainImmutabilityTests</c> is the complementary technique, applied there to the two types
/// where its cost is justified.
/// </para>
/// <para>
/// Sabotaged three times before it was believed: a public setter on <c>Credential.UserId</c>, an
/// <c>init</c> setter, and a pinned key the Domain no longer declares. Each named the offender.
/// </para>
/// </remarks>
public sealed class OwnershipKeyImmutabilityTests
{
    [Test]
    public async Task Detector_ReportsAPublicSetterOnAnOwnershipKey()
    {
        // Arrange — the violation this guard exists to catch, on a synthetic type so the proof is
        // permanent rather than a sentence about a change that was reverted.
        Type[] types = [typeof(ReassignableOwner)];

        // Act
        IReadOnlyList<string> offenders = OwnershipKeys.MutableIn(types);

        // Assert
        await Assert.That(offenders).IsEquivalentTo(new[] { "ReassignableOwner.UserId" });
    }

    [Test]
    public async Task Detector_ReportsAnInitOnlySetterOnAnOwnershipKey()
    {
        // Arrange — init reads as immutable and is not. `with { UserId = someoneElse }` on a record
        // produces a copy filed under another user, and the copy is a perfectly ordinary object
        // that any repository will happily persist. No Domain entity is a record today; the moment
        // one becomes a positional record its key is init by default and nothing announces it.
        Type[] types = [typeof(CopyableOwner)];

        // Act
        IReadOnlyList<string> offenders = OwnershipKeys.MutableIn(types);

        // Assert
        await Assert.That(offenders).IsEquivalentTo(new[] { "CopyableOwner.BudgetId" });
    }

    [Test]
    public async Task Detector_AcceptsAPrivateSetterAndAGetOnlyProperty()
    {
        // Arrange
        Type[] types = [typeof(SealedOwner)];

        // Act — without this the detector could report every property it sees and both tests above
        // would still pass, which would make the real assertions below fire on a clean Domain.
        IReadOnlyList<string> offenders = OwnershipKeys.MutableIn(types);

        // Assert
        await Assert.That(string.Join(", ", offenders)).IsEqualTo(string.Empty);
        await Assert.That(OwnershipKeys.DeclaredIn(types)).IsEquivalentTo(
            new[] { "SealedOwner.BudgetId", "SealedOwner.UserId" });
    }

    [Test]
    public async Task Detector_IsBlindToAKeyUnderAnotherName()
    {
        // Arrange — the same property, spelled differently.
        Type[] types = [typeof(RenamedOwner)];

        // Act
        IReadOnlyList<string> declared = OwnershipKeys.DeclaredIn(types);
        IReadOnlyList<string> offenders = OwnershipKeys.MutableIn(types);

        // Assert — a public setter, and the detector reports nothing. This is not a defect to fix
        // by widening the name list, which would only move the blind spot to the next spelling: it
        // is the whole reason the pinned set below exists. Rename Credential.UserId and the
        // mutability test goes on passing while checking nothing, so the set is what must go red.
        await Assert.That(string.Join(", ", declared)).IsEqualTo(string.Empty);
        await Assert.That(string.Join(", ", offenders)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task OwnershipKeys_AreExactlyTheSetTheDomainDeclares()
    {
        // Arrange
        Type[] domainTypes = typeof(Budget).Assembly.GetTypes();

        // Act
        IReadOnlyList<string> declared = OwnershipKeys.DeclaredIn(domainTypes);

        // Assert — pinning the set is what catches a *rename*. Spell Credential.UserId as OwnerId
        // and the mutability test below goes on passing forever, having found nothing to check;
        // this line goes red instead and a human has to say what the new name means.
        string[] expected =
        [
            "Account.BudgetId",
            "Budget.UserId",
            "Category.BudgetId",
            "CategoryGroup.BudgetId",
            "Credential.UserId",
            // The primary key of factor_manifests, a per-account table policed by user_isolation —
            // the manifest belongs to the account, not to any one factor, so there is exactly one row
            // per user rather than one per credential. FactorManifest.For reads the owner off the
            // loaded User rather than taking an id argument, the same decision every other factory on
            // this list makes; a settable UserId would let the sole authenticated carrier of every
            // factor's public key be re-filed against another account's row.
            "FactorManifest.UserId",
            // The key whose column is the table's own primary key, which is unusual on this list and
            // is the point. KeyRotation is the staging row a content-key rotation runs under:
            // re-encrypting every narrative field of an account is more work than one request, so the
            // next generation of wrapped keys is held here across several of them until one completion
            // step promotes it into wrapped_account_keys. Keyed on the account, "at most one rotation
            // in flight per account" is a primary key rather than a rule somebody has to enforce.
            // Begin() reads the owner off the loaded Credential rather than taking it as an argument,
            // the same decision every other factory on this list makes. What a settable UserId would
            // cost is specific to this row: it names the factor whose envelopes the completion step
            // overwrites in place — wrapped_account_keys already grants
            // UPDATE (wrapped_private_key, encapsulated_account_keys) for exactly that — so re-filing this row
            // against another account would point a destructive UPDATE at somebody else's wrapped
            // keys. That is worse than a mis-owned row, and it is the concrete reason the property has
            // no setter. The persistence half is FK_key_rotations_users, an ON DELETE CASCADE from
            // users — which is also what puts the table back on the erasure chain now that the
            // composite key to wrapped_account_keys has gone with factor_id.
            "KeyRotation.UserId",
            // The newest entry, and the one whose factory earns the idiom more than any other on this
            // list. KeyRotationSeal.For takes the loaded KeyRotation and the loaded WrappedAccountKeys,
            // reads UserId off the first and FactorId off the second, and REFUSES WHEN THE TWO
            // DISAGREE — two independent statements of an owner from two sources, which no signature
            // taking loose ids can compare at all. A settable UserId would hand back exactly what that
            // comparison exists to refuse: this account's next generation of keys staged against
            // somebody else's factor, formed after the check has already run. The persistence half is
            // the composite foreign key to wrapped_account_keys(factor_id, user_id), which makes the
            // same row unstorable — the entity makes it unconstructable, which is one 23503 earlier,
            // before a partially-staged run exists.
            "KeyRotationSeal.UserId",
            "PasskeyPublicKey.UserId",
            "PasskeySignatureCounter.UserId",
            "Payee.BudgetId",
            // One of the two keys this rule is strictest about. recovery_code_hashes is
            // exempt from row-level security — the row is found by the SHA-256 of the verifier on an
            // anonymous redemption request, before anybody has said who they are — so nothing beneath
            // the application re-checks whose row it is, exactly as on Credential.UserId. What is
            // worse here is what the column is *for*: the redemption adopts the user_id it finds, so a
            // settable one is not a row filed under the wrong owner, it is a handover of somebody
            // else's account, and no policy underneath would notice. Read the mutability test beside
            // this one as carrying that weight for this property.
            "RecoveryCodeHash.UserId",
            "Session.UserId",
            // The other of those two, and the stricter, for the reason RecoveryCodeHash.UserId is,
            // only more so. session_tokens is exempt from row-level
            // security — the row is found by the digest of a presented token before anybody has said
            // who they are — so nothing beneath the application re-checks whose row it is, and what the
            // column is FOR is that the request adopts the owner it finds here. A settable UserId is
            // not a row filed under the wrong owner; it is a handover of somebody else's account on
            // every request the token is presented on, and no policy underneath would notice. What
            // makes it unsettable is SessionToken.For reading both ids off the loaded Session rather
            // than taking them as arguments, which is the same decision every other factory on this
            // list makes.
            "SessionToken.UserId",
            "Transaction.BudgetId",
            // Held at two layers now, and each catches a different thing. The database half is in
            // place: wrapped_account_keys is a real table, granted SELECT, INSERT and
            // UPDATE (wrapped_private_key, encapsulated_account_keys), policed by user_isolation, with
            // AppRoleGrantMatrixTests pinning both the table's verb set and that column list. user_id
            // is absent from the list, which is how this schema spells an immutable column, so an
            // UPDATE naming it is refused by Postgres with 42501 however it was built. What a grant
            // cannot refuse is the property: assigning WrappedAccountKeys.UserId in C# compiles, and
            // the grant only ever fires if EF then emits that column in a SET list. On an INSERT it
            // never fires at all — every column is insertable — and a foreign owner there is caught,
            // if at all, by user_isolation's WITH CHECK. A grant refuses a statement; this census
            // refuses the value being formed. What it protects is the factory's one decision: For()
            // reads the owner off the loaded credential rather than taking it as an argument, so a
            // settable UserId would hand back a way to re-file an account's two envelopes against
            // another account's factor after every validation that could have caught it has already
            // run.
            "WrappedAccountKeys.UserId",
        ];
        // Asserted as collections, and the difference is what a failure can SAY rather than when it
        // fires. Both directions used to be joined into one string and compared with the empty one.
        // That went red at exactly the same moments — the rule below is unchanged, and both
        // directions are still compared — but it could not name what it caught: TUnit truncates the
        // received string at about a hundred characters, so five offenders of ordinary length lose
        // the tail mid-word and nothing says whether there was a sixth. What it prints in place of
        // the missing names is a "differs at index 0" caret diagram drawing the empty string
        // against itself, which is noise here because the expected side is always "". A census that
        // reddens without naming its offenders sends the next person hunting through 14 literals.
        // IsEmpty() lists every item. Measured against this file with five entries removed.
        await Assert.That(declared.Except(expected)).IsEmpty();
        await Assert.That(expected.Except(declared)).IsEmpty();

        // Left as a count deliberately: a different assertion doing a different job. It catches a
        // reflection query that enumerated nothing and passed both lines above vacuously, and a
        // number is the whole of what it has to report.
        await Assert.That(declared.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task OwnershipKeys_ExposeNoSetterOutsideTheirDeclaringType()
    {
        // Arrange
        Type[] domainTypes = typeof(Budget).Assembly.GetTypes();

        // Act
        IReadOnlyList<string> offenders = OwnershipKeys.MutableIn(domainTypes);

        // Assert — asserted on the collection rather than counted, so a failure names the offending
        // properties, and rather than joined, so it names ALL of them. Joining and comparing with
        // the empty string reddens identically; it just truncates the report at about a hundred
        // characters, and the refactor that opens several setters at once is exactly the change
        // that produces more offenders than fit.
        await Assert.That(offenders).IsEmpty();

        // A reflection query that silently came back empty would satisfy the line above while
        // proving nothing, so the subject is asserted to exist as well.
        await Assert.That(OwnershipKeys.DeclaredIn(domainTypes).Count).IsGreaterThan(0);
    }

    /// <summary>
    /// Finds the properties that say which user or which budget a row belongs to, and decides
    /// whether any of them can be written from outside the type that declares it.
    /// </summary>
    /// <remarks>
    /// Both methods take <see cref="IEnumerable{T}" /> of <see cref="Type" /> rather than reaching
    /// for the Domain assembly themselves, which is what lets the synthetic types above prove the
    /// detector works without anyone editing a real entity to watch it go red.
    /// </remarks>
    private static class OwnershipKeys
    {
        private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;

        /// <summary>Renders every ownership key the given types declare as "Type.Property".</summary>
        internal static IReadOnlyList<string> DeclaredIn(IEnumerable<Type> types) =>
            [.. KeysIn(types).Select(Describe).Order(StringComparer.Ordinal)];

        /// <summary>Renders the subset whose setter is reachable from outside the declaring type.</summary>
        /// <remarks>
        /// A private setter is the only accepted shape. <c>public</c>, <c>protected</c>,
        /// <c>internal</c> and <c>init</c> all leave a writable path from elsewhere, and <c>init</c>
        /// is the one that does not look like one.
        /// </remarks>
        internal static IReadOnlyList<string> MutableIn(IEnumerable<Type> types) =>
        [
            .. KeysIn(types)
                .Where(property => property.SetMethod is { IsPrivate: false })
                .Select(Describe)
                .Order(StringComparer.Ordinal),
        ];

        private static IEnumerable<PropertyInfo> KeysIn(IEnumerable<Type> types) =>
            types.SelectMany(type => type.GetProperties(PublicInstance))
                .Where(property => property.Name is "UserId" or "BudgetId");

        private static string Describe(PropertyInfo property) =>
            $"{property.DeclaringType?.Name}.{property.Name}";
    }

    private sealed class ReassignableOwner
    {
        public Guid UserId { get; set; }
    }

    private sealed class CopyableOwner
    {
        public Guid BudgetId { get; init; }
    }

    private sealed class SealedOwner
    {
        public Guid UserId { get; private set; }

        public Guid BudgetId { get; }
    }

    private sealed class RenamedOwner
    {
        public Guid OwnerId { get; set; }
    }
}
