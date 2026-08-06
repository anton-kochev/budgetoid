using System.Reflection;
using Domain.Common;
using Domain.Users;

namespace UnitTests;

public sealed class PasskeySignatureCounterTests
{
    [Test]
    public async Task Accept_WhenReportedExceedsStored_AdvancesAndReportsAChange()
    {
        // Arrange
        PasskeySignatureCounter counter = PasskeySignatureCounter.Start(PasskeyCredential(), 41);

        // Act
        bool changed = counter.Accept(42);

        // Assert — the counter only ever moves forward, and the return value is what tells the caller
        // there is a new value worth writing. An assertion on Value alone would pass just as well
        // against a counter that always reported a change and wrote on every single sign-in.
        await Assert.That(changed).IsTrue();
        await Assert.That(counter.Value).IsEqualTo(42u);
    }

    [Test]
    public async Task Accept_WhenBothAreZero_IsAcceptedAndReportsNoChange()
    {
        // Arrange — an authenticator backing a synced passkey has no per-device counter to increment,
        // so it reports zero every time.
        PasskeySignatureCounter counter = PasskeySignatureCounter.Start(PasskeyCredential(), 0);

        // Act
        bool changed = counter.Accept(0);

        // Assert — this arm is not a hole in the monotonic rule, it is the rule meeting the
        // authenticators that actually exist: refusing a repeated zero would refuse most real
        // passkeys. It reports no change so the sign-in path writes nothing, which is what keeps a
        // read-only verification from turning into an UPDATE on every request.
        await Assert.That(changed).IsFalse();
        await Assert.That(counter.Value).IsEqualTo(0u);
    }

    [Test]
    public async Task Accept_WhenReportedEqualsANonZeroStored_Throws()
    {
        // Arrange — an authenticator that counts at all must count up. A repeat of a non-zero value
        // is a replayed assertion, not the synced-passkey case above.
        PasskeySignatureCounter counter = PasskeySignatureCounter.Start(PasskeyCredential(), 7);

        // Act
        ValidationException exception = ThrowsValidationException(() => counter.Accept(7));

        // Assert
        await Assert.That(exception.Errors.ContainsKey(nameof(PasskeySignatureCounter.Value)))
            .IsTrue();
        await Assert.That(counter.Value).IsEqualTo(7u);
    }

    [Test]
    public async Task Accept_WhenReportedIsBelowStored_Throws()
    {
        // Arrange — a counter that went backwards is the signal a cloned authenticator produces, and
        // it is the only reason to keep a counter at all.
        PasskeySignatureCounter counter = PasskeySignatureCounter.Start(PasskeyCredential(), 7);

        // Act
        ValidationException exception = ThrowsValidationException(() => counter.Accept(6));

        // Assert — the stored value is left where it was. Writing the lower number would hand the
        // clone a counter it can now advance past, which is the failure this check exists to catch.
        await Assert.That(exception.Errors.ContainsKey(nameof(PasskeySignatureCounter.Value)))
            .IsTrue();
        await Assert.That(counter.Value).IsEqualTo(7u);
    }

    [Test]
    public async Task Start_AgainstAFederatedCredential_Throws()
    {
        // Arrange — an authorization exchange with an identity provider produces no signature and
        // therefore no counter to keep for it.
        Credential credential = FederatedCredential();

        // Act
        ValidationException exception = ThrowsValidationException(() =>
            PasskeySignatureCounter.Start(credential, 0));

        // Assert — the same refusal PasskeyPublicKey.Register makes, and for the same reason: a
        // counter filed against a federated credential would be a clone check on a credential no
        // authenticator ever signs with, which is a check that can only ever pass.
        await Assert.That(exception.Errors.ContainsKey(nameof(PasskeySignatureCounter.CredentialType)))
            .IsTrue();
    }

    [Test]
    public async Task Start_CopiesTheCredentialsOwnerIdAndType()
    {
        // Arrange
        Credential credential = PasskeyCredential();

        // Act
        PasskeySignatureCounter counter = PasskeySignatureCounter.Start(credential, 3);

        // Assert — the owner is copied from the credential rather than passed in beside it, so no
        // call shape can file a counter under one person's credential and another person's user id.
        // The user id is what the row-level policy reads, so a counter carrying the wrong one is
        // invisible to its owner and reachable by someone else.
        await Assert.That(counter.CredentialId).IsEqualTo(credential.Id);
        await Assert.That(counter.UserId).IsEqualTo(credential.UserId);
        await Assert.That(counter.CredentialType).IsEqualTo(CredentialType.Passkey);
        await Assert.That(counter.Value).IsEqualTo(3u);
    }

    [Test]
    public async Task PasskeySignatureCounter_ExposesNoWayToSetItsValueDirectly()
    {
        // Arrange — every public way into the type: constructors, methods including static ones,
        // properties, and fields.
        MemberInfo[] surface =
        [
            .. typeof(PasskeySignatureCounter).GetConstructors(PublicInstance),
            .. typeof(PasskeySignatureCounter).GetMethods(PublicInstance | BindingFlags.Static)
                .Where(IsDeclaredByTheType),
            .. typeof(PasskeySignatureCounter).GetProperties(PublicInstance),
            .. typeof(PasskeySignatureCounter).GetFields(PublicInstance),
        ];

        // Act
        string[] signatures = surface
            .Select(Describe)
            .OrderBy(signature => signature, StringComparer.Ordinal)
            .ToArray();

        // Assert — Accept is the one mutator, and pinning the surface is what keeps it the only one.
        // The tests above prove Accept refuses a value that did not move forward; none of them notice
        // a public setter on Value, and a counter that can be assigned is a clone check that any
        // caller can quietly satisfy. The other route is a second factory taking a CredentialType
        // directly, which reintroduces the counter-on-a-federated-credential the Start test refuses.
        //
        // The whole surface is pinned rather than filtered, because the routes that do not name Value
        // or CredentialType are the ones a filter misses: a Reset() takes no parameter and is no
        // property, and a public field is not a property at all.
        //
        // These are CLR type names as reflection renders them, not the C# keywords the source is
        // written in: "UInt32", not "uint". Editing a line here to look like the declaration is how
        // this test starts failing for no reason.
        string[] expected =
        [
            "Boolean Accept(UInt32 reported)",
            "CredentialType CredentialType { get; }",
            "Guid CredentialId { get; }",
            "Guid UserId { get; }",
            "UInt32 Value { get; }",
            "static PasskeySignatureCounter Start(Credential credential, UInt32 value)",
        ];
        await Assert.That(signatures).IsEquivalentTo(expected);

        // A reflection query that silently returned nothing would pass an emptied expectation above
        // while proving nothing at all.
        await Assert.That(surface.Length).IsGreaterThan(0);
    }

    private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;

    private static readonly DateTime CreatedAtUtc = new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    private static Credential PasskeyCredential() =>
        Credential.CreatePasskey(Guid.CreateVersion7(), CreatedAtUtc);

    private static Credential FederatedCredential() => Credential.CreateFederated(
        Guid.CreateVersion7(), Credential.GoogleProvider, "google-subject", CreatedAtUtc);

    /// <summary>
    /// Keeps the methods the type declares for itself and drops the ones every object has. Property
    /// accessors go too — the properties are rendered as properties below, with their accessors, so
    /// counting them again as <c>get_</c>/<c>set_</c> pairs would say the same thing twice.
    /// </summary>
    private static bool IsDeclaredByTheType(MethodInfo method) =>
        !method.IsSpecialName && method.GetBaseDefinition().DeclaringType != typeof(object);

    /// <summary>
    /// Renders one public member as a signature string. Parameter names are carried as well as types,
    /// because a type-only signature would not show a <c>credentialType</c> parameter arriving where a
    /// credential already sits; property accessors are carried because a settable <c>Value</c> is the
    /// route this test exists to refuse.
    /// </summary>
    private static string Describe(MemberInfo member) => member switch
    {
        ConstructorInfo constructor => $"ctor({DescribeParameters(constructor)})",
        MethodInfo method =>
            $"{(method.IsStatic ? "static " : string.Empty)}{TypeName(method.ReturnType)} "
            + $"{method.Name}({DescribeParameters(method)})",
        PropertyInfo property =>
            $"{TypeName(property.PropertyType)} {property.Name} {{ {Accessors(property)} }}",

        // A public field would be an assignable route with no accessor to inspect at all, which is
        // why it is rendered rather than assumed absent.
        FieldInfo field => $"{TypeName(field.FieldType)} {field.Name} (field)",
        _ => $"{member.MemberType} {member.Name}",
    };

    private static string DescribeParameters(MethodBase method) => string.Join(
        ", ",
        method.GetParameters().Select(parameter =>
            $"{TypeName(parameter.ParameterType)} {parameter.Name}"));

    /// <summary>
    /// The CLR name, except that a nullable value type is written the way the source writes it and a
    /// constructed generic is written with its arguments. A bare <c>`1</c> would carry a backtick and
    /// no element type, which would let an argument of the wrong element type pass unnoticed.
    /// </summary>
    private static string TypeName(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return $"{underlying.Name}?";
        }

        if (!type.IsGenericType)
        {
            return type.Name;
        }

        string name = type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)];
        string arguments = string.Join(", ", type.GetGenericArguments().Select(TypeName));

        return $"{name}<{arguments}>";
    }

    private static string Accessors(PropertyInfo property) =>
        property.SetMethod is { IsPublic: true } ? "get; set;" : "get;";

    private static ValidationException ThrowsValidationException(Action action)
    {
        try
        {
            action();
        }
        catch (ValidationException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Expected ValidationException.");
    }
}
