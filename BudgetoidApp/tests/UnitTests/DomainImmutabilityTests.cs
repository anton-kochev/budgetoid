using System.Reflection;
using Domain.Accounts;
using Domain.Budgets;

namespace UnitTests;

/// <summary>
/// Pins two immutability rules that are true today only by construction and written down nowhere:
/// a <see cref="Budget" /> cannot be renamed, re-owned or given a base currency after creation, and
/// an <see cref="Account" />'s currency is fixed for its lifetime. Nothing fails today when someone
/// adds a setter or a mutating method, which is exactly what these tests change.
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
/// Both of these are an <b>interim</b> enforcement layer, not the bottom one. The genuine bottom is
/// <c>REVOKE UPDATE (name, user_id, base_currency_code) ON budgets</c> and
/// <c>REVOKE UPDATE (currency_code) ON accounts</c> from a least-privilege application role — a
/// grant, not a trigger, so it is legitimately declarative — and it is blocked on that role
/// existing. Until it does, <c>dbContext.Budgets.ExecuteUpdateAsync(...)</c> bypasses the domain
/// entirely and no arrangement of private setters stops it. That hole is the reason to want the
/// role, and it is stated here because here is where the interim guard lives.
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
        string[] expected =
        [
            "Void Update(String name, AccountType type, Decimal openingBalance, Int32 minorUnit)",
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
