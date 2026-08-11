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
            "PasskeyPublicKey.UserId",
            "PasskeySignatureCounter.UserId",
            "Payee.BudgetId",
            // The eleventh key, and the one this rule is strictest about. recovery_code_hashes is
            // exempt from row-level security — the row is found by the SHA-256 of the verifier on an
            // anonymous redemption request, before anybody has said who they are — so nothing beneath
            // the application re-checks whose row it is, exactly as on Credential.UserId. What is
            // worse here is what the column is *for*: the redemption adopts the user_id it finds, so a
            // settable one is not a row filed under the wrong owner, it is a handover of somebody
            // else's account, and no policy underneath would notice. Read the mutability test beside
            // this one as carrying that weight for this property.
            "RecoveryCodeHash.UserId",
            "Session.UserId",
            "Transaction.BudgetId",
        ];
        await Assert.That(string.Join(", ", declared.Except(expected))).IsEqualTo(string.Empty);
        await Assert.That(string.Join(", ", expected.Except(declared))).IsEqualTo(string.Empty);
        await Assert.That(declared.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task OwnershipKeys_ExposeNoSetterOutsideTheirDeclaringType()
    {
        // Arrange
        Type[] domainTypes = typeof(Budget).Assembly.GetTypes();

        // Act
        IReadOnlyList<string> offenders = OwnershipKeys.MutableIn(domainTypes);

        // Assert — joined rather than counted so a failure names the offending property.
        await Assert.That(string.Join(", ", offenders)).IsEqualTo(string.Empty);

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
