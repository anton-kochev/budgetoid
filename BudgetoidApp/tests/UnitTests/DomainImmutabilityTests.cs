using System.Reflection;
using Domain.Accounts;
using Domain.Budgets;

namespace UnitTests;

/// <summary>
/// Pins two immutability rules that live in the shape of the domain: a <see cref="Budget" /> cannot
/// be renamed, re-owned or given a base currency after creation, and an <see cref="Account" />'s
/// currency is fixed for its lifetime. The database writes both rules down as well — by the columns
/// left off the <c>GRANT UPDATE</c> lists in <c>app-role-grants.sql</c>, pinned by
/// <c>AppRoleGrantMatrixTests</c> — so these tests are not the only record of them. What they add is
/// the refusal of the <b>member</b> rather than of the statement, which the second paragraph below
/// argues. Otherwise adding a setter or a mutating method costs nothing anywhere: it compiles, and
/// on a column the role may write it succeeds. That is what these tests change.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="Account" /> test asserts the <b>set</b> of public instance method signatures
/// rather than probing for a currency parameter by name. That is deliberate, and it is the idiom
/// <c>SchemaConstraintSnapshotTests</c> already uses for constraints: a heuristic ("no parameter
/// whose name contains currency") passes for the wrong reason the moment someone spells it
/// differently, whereas pinning the set means a <c>Redenominate</c> method or a widened
/// <c>Update</c> moves a line and a human has to acknowledge it.
/// </para>
/// <para>
/// Neither of these is the bottom enforcement layer, and neither is waiting to become one. The
/// bottom layer shipped with <c>budgetoid_app</c> (ADR 0004): <c>app-role-grants.sql</c> grants that
/// role <c>UPDATE (name) ON budgets</c> and
/// <c>UPDATE (name, name_key, type, opening_balance) ON accounts</c>, so <c>user_id</c>,
/// <c>base_currency_code</c>, <c>created_at_utc</c> and <c>id</c> on the one, and
/// <c>currency_code</c> on the other, are immutable by being <b>absent from an explicit column
/// list</b> — and <c>AppRoleGrantMatrixTests</c> restates the whole matrix in both directions, so a
/// list widened to a table-wide <c>GRANT UPDATE</c> reddens. The shape is not decorative. What this
/// paragraph used to name — <c>REVOKE UPDATE (base_currency_code) ON budgets</c> — is the
/// formulation ADR 0004 considered and rejected, because PostgreSQL column privileges are additive:
/// revoking one column against a table-wide <c>UPDATE</c> removes a column-level grant that was
/// never issued, so the statement succeeds, changes nothing, and reads in the file as though it had
/// closed a hole that is still open. <c>name</c> is the one writable <c>budgets</c> column and that
/// is deliberate rather than an oversight — a content-key rotation (FR-099) re-seals the same text
/// under a new key, which is why ADR 0004 states B2 as "no command updates a <c>budgets</c> row"
/// rather than "a <c>budgets</c> row is never updated at all".
/// </para>
/// <para>
/// So the two layers are both present on purpose, and calling that redundant misreads what each one
/// refuses. A grant refuses a <i>statement</i> — it can only answer once an <c>UPDATE</c> has been
/// composed and sent, and it answers with <c>42501</c> from inside a request. These tests refuse the
/// <i>member</i> that would compose one: a <c>Budget.Rename</c> or a widened <c>Account.Update</c>
/// is a perfectly legal method until somebody moves a line here and says why, which happens at
/// review time rather than at run time and covers the shape of the domain regardless of which role
/// the connection is under. The escape hatch this paragraph once described as open is not open
/// either: <c>ExecuteUpdate</c> and <c>ExecuteDelete</c> are entries in
/// <c>BudgetoidApp/BannedSymbols.txt</c>, so <c>dbContext.Budgets.ExecuteUpdateAsync(...)</c> is a
/// compile error in this solution rather than merely a path nobody takes.
/// </para>
/// </remarks>
public sealed class DomainImmutabilityTests
{
    [Test]
    public async Task Budget_ExposesNoPublicPropertySetter()
    {
        // Arrange
        PropertyInfo[] properties = typeof(Budget).GetProperties(PublicInstance);

        // Act — a public setter on Name, UserId or BaseCurrencyCode is the direct route to renaming,
        // re-owning or redenominating a budget, and all three are creation-time decisions.
        string[] settableProperties = properties
            .Where(property => property.SetMethod is { IsPublic: true })
            .Select(property => property.Name)
            .ToArray();

        // Assert — joined rather than counted so a failure names the offending property.
        await Assert.That(string.Join(", ", settableProperties)).IsEqualTo(string.Empty);

        // Without this, a reflection query that silently returned nothing would pass the assertion
        // above while proving nothing at all.
        await Assert.That(properties.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task Budget_DeclaresNoPublicInstanceMethod()
    {
        // Arrange
        MethodInfo[] methods = typeof(Budget).GetMethods(PublicInstance);

        // Act — Budget is a private constructor, private setters and two static factories, and this
        // is the whole of why it is immutable. Any instance method it grew would be a mutator or a
        // route to one, so the honest assertion is that the surface is empty rather than that some
        // particular method is absent.
        string[] declaredMethods = methods
            .Where(IsDeclaredByTheType)
            .Select(Describe)
            .ToArray();

        // Assert — joined rather than counted so a failure names the offending method.
        await Assert.That(string.Join(", ", declaredMethods)).IsEqualTo(string.Empty);

        // The unfiltered list still holds object's members and the property getters, so a reflection
        // query that came back empty for the wrong reason cannot slip past the assertion above.
        await Assert.That(methods.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task Account_PinsEveryPublicInstanceMethodSignature()
    {
        // Arrange
        MethodInfo[] methods = typeof(Account).GetMethods(PublicInstance);

        // Act
        string[] signatures = methods
            .Where(IsDeclaredByTheType)
            .Select(Describe)
            .OrderBy(signature => signature, StringComparer.Ordinal)
            .ToArray();

        // Assert — an account is denominated once and never redenominated: its transactions are
        // already recorded in that currency, so changing it would silently reinterpret every one of
        // them rather than convert them. The single line below is the only thing making that true —
        // Update re-validates against the existing CurrencyCode and takes no currency parameter.
        // Adding a currencyCode parameter to it, or a Redenominate method beside it, moves this set
        // and forces the change to be looked at.
        // These are CLR type names as reflection renders them, not the C# keywords the source is
        // written in: "String", not "string". Editing a line here to look like the declaration is
        // how this test starts failing for no reason.
        // The name parameter is an IndexedName and not a String, and that is a second rule this set now
        // pins. Both halves of a name move together because this signature has no spelling for half of
        // one; an overload taking a bare NarrativeField would write new ciphertext under the previous
        // name's index — a row whose uniqueness value stops describing its own content, which no
        // constraint can see and no read can report, because recomputing either half needs the account's
        // index key and that lives in a browser. Such an overload would appear here as a second entry.
        string[] expected =
        [
            "Void Update(IndexedName name, AccountType type, Decimal openingBalance, Int32 minorUnit)",
        ];
        await Assert.That(signatures).IsEquivalentTo(expected);
        await Assert.That(signatures.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task Account_ExposesNoPublicPropertySetter()
    {
        // Arrange
        PropertyInfo[] properties = typeof(Account).GetProperties(PublicInstance);

        // Act — the pinned method surface above says nothing about properties, so without this a
        // public setter on CurrencyCode would redenominate an account with the method test still
        // green.
        string[] settableProperties = properties
            .Where(property => property.SetMethod is { IsPublic: true })
            .Select(property => property.Name)
            .ToArray();

        // Assert — joined rather than counted so a failure names the offending property.
        await Assert.That(string.Join(", ", settableProperties)).IsEqualTo(string.Empty);
        await Assert.That(properties.Length).IsGreaterThan(0);
    }

    private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;

    /// <summary>
    /// Keeps the methods a domain type declares for itself and drops the ones every object has.
    /// Property accessors go too — they are covered by the setter tests, which read them as
    /// properties rather than as <c>get_</c>/<c>set_</c> pairs.
    /// </summary>
    /// <remarks>
    /// The base definition is what decides, not the declaring type: overriding
    /// <see cref="object.ToString" /> is a rendering choice with no mutation path in it, and making
    /// this guard fail on one would only teach the next person to edit the guard.
    /// </remarks>
    private static bool IsDeclaredByTheType(MethodInfo method) =>
        !method.IsSpecialName && method.GetBaseDefinition().DeclaringType != typeof(object);

    /// <summary>
    /// Renders a method as a signature string carrying parameter names as well as types, because a
    /// type-only signature would not show a <c>currencyCode</c> parameter arriving where a
    /// <c>name</c> already sits.
    /// </summary>
    private static string Describe(MethodInfo method)
    {
        string parameters = string.Join(
            ", ",
            method.GetParameters().Select(parameter =>
                $"{parameter.ParameterType.Name} {parameter.Name}"));

        return $"{method.ReturnType.Name} {method.Name}({parameters})";
    }
}
