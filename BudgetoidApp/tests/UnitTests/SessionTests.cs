using System.Reflection;
using Domain.Common;
using Domain.Sessions;
using Domain.Users;

namespace UnitTests;

public sealed class SessionTests
{
    [Test]
    public async Task Establish_FromAFederatedCredential_ReturnsALockedSession()
    {
        // Arrange
        Credential credential = FederatedCredential();

        // Act
        Session session = Session.Establish(credential, CreatedAtUtc, ExpiresAtUtc);

        // Assert — a federated credential is held at an identity provider, and an authorization
        // exchange hands back claims rather than a secret the client can turn into a key. So a
        // session it opens can never unlock the narrative, and the product does not pretend
        // otherwise by opening one that reaches budget content.
        await Assert.That(session.Kind).IsEqualTo(SessionKind.Locked);
        await Assert.That(session.ReadsBudgetContent).IsFalse();
    }

    [Test]
    public async Task Session_ExposesNoWayToChooseItsKind()
    {
        // Arrange — every public way into the type: constructors, methods including static ones,
        // properties, and fields.
        MemberInfo[] surface =
        [
            .. typeof(Session).GetConstructors(PublicInstance),
            .. typeof(Session).GetMethods(PublicInstance | BindingFlags.Static)
                .Where(IsDeclaredByTheType),
            .. typeof(Session).GetProperties(PublicInstance),
            .. typeof(Session).GetFields(PublicInstance),
        ];

        // Act
        string[] signatures = surface
            .Select(Describe)
            .OrderBy(signature => signature, StringComparer.Ordinal)
            .ToArray();

        // Assert — this is what makes "a federated credential opens no budget-reading session"
        // unrepresentable rather than merely untested. The locked-session test above only proves the
        // factory's arithmetic today; without this, the obvious "fix" for a future caller that wants
        // a different kind is an overload taking one, and the rule dissolves without a red test.
        //
        // The whole surface is pinned rather than filtered for members that mention SessionKind,
        // because the routes to a chosen kind that do not mention it are the ones a filter misses: a
        // no-argument Unlock() assigning Kind takes no parameter and is no property, and a public
        // field is not a property at all. Anything that moves this set is a change to what a caller
        // can reach, and it has to be looked at.
        //
        // These are CLR type names as reflection renders them, not the C# keywords the source is
        // written in: "Boolean", not "bool". Editing a line here to look like the declaration is how
        // this test starts failing for no reason.
        string[] expected =
        [
            "Boolean IsActiveAt(DateTime instantUtc)",
            "Boolean ReadsBudgetContent { get; }",
            "CredentialType CredentialType { get; }",
            "DateTime CreatedAtUtc { get; }",
            "DateTime ExpiresAtUtc { get; }",
            "DateTime? RevokedAtUtc { get; }",
            "Guid CredentialId { get; }",
            "Guid Id { get; }",
            "Guid UserId { get; }",
            "SessionKind Kind { get; }",
            "Void Revoke(DateTime revokedAtUtc)",
            "static Session Establish(Credential credential, DateTime createdAtUtc, DateTime expiresAtUtc)",
        ];
        await Assert.That(signatures).IsEquivalentTo(expected);

        // A reflection query that silently returned nothing would pass an emptied expectation above
        // while proving nothing at all.
        await Assert.That(surface.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task Establish_RecordsTheCredentialThatEstablishedIt()
    {
        // Arrange
        Credential credential = FederatedCredential();

        // Act
        Session session = Session.Establish(credential, CreatedAtUtc, ExpiresAtUtc);

        // Assert — the credential, not merely the person. Revoking one credential must end the
        // sessions it opened and leave every other credential's alone, and a session recording only
        // its user cannot tell them apart.
        await Assert.That(session.Id).IsNotEqualTo(Guid.Empty);
        await Assert.That(session.CredentialId).IsEqualTo(credential.Id);
        await Assert.That(session.UserId).IsEqualTo(credential.UserId);
        await Assert.That(session.CreatedAtUtc).IsEqualTo(CreatedAtUtc);
        await Assert.That(session.ExpiresAtUtc).IsEqualTo(ExpiresAtUtc);
        await Assert.That(session.RevokedAtUtc).IsNull();
    }

    [Test]
    public async Task Establish_WithoutACredential_ThrowsArgumentNullException()
    {
        // Arrange, Act, Assert — the credential is what the kind is derived from, so there is no
        // session to establish without one. An ArgumentNullException rather than a
        // ValidationException: no user typed this, it is a caller handing over nothing at all.
        await Assert.That(() => Session.Establish(null!, CreatedAtUtc, ExpiresAtUtc))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Establish_WithAnExpiryNotAfterCreation_ThrowsValidationException()
    {
        // Arrange, Act — an expiry at or before the moment the session began is a session that was
        // never live, which is a bug on the establishing path rather than a very short session.
        ValidationException exception = ThrowsValidationException(() =>
            Session.Establish(FederatedCredential(), CreatedAtUtc, CreatedAtUtc));

        // Assert
        await Assert.That(exception.Errors.ContainsKey(nameof(Session.ExpiresAtUtc))).IsTrue();
    }

    [Test]
    public async Task Revoke_OnAnActiveSession_MarksItRevokedAtTheGivenInstant()
    {
        // Arrange
        Session session = Session.Establish(FederatedCredential(), CreatedAtUtc, ExpiresAtUtc);
        DateTime revokedAtUtc = CreatedAtUtc.AddMinutes(1);

        // Act
        session.Revoke(revokedAtUtc);

        // Assert — revocation is a fact the product records and can act on by itself. No external
        // party is asked, and nothing about the session's identity moves.
        await Assert.That(session.RevokedAtUtc).IsEqualTo(revokedAtUtc);
        await Assert.That(session.IsActiveAt(revokedAtUtc.AddSeconds(1))).IsFalse();
    }

    [Test]
    public async Task Revoke_OnAnAlreadyRevokedSession_KeepsTheFirstRevocationInstant()
    {
        // Arrange
        Session session = Session.Establish(FederatedCredential(), CreatedAtUtc, ExpiresAtUtc);
        DateTime firstRevocation = CreatedAtUtc.AddMinutes(1);
        session.Revoke(firstRevocation);

        // Act
        session.Revoke(CreatedAtUtc.AddMinutes(2));

        // Assert — idempotence is what lets "revoke every session this credential established" run
        // twice without rewriting the instant access actually ended, and it is why the transition
        // lives here rather than in a setter the caller assigns.
        await Assert.That(session.RevokedAtUtc).IsEqualTo(firstRevocation);
    }

    [Test]
    public async Task IsActiveAt_BeforeTheExpiry_ReturnsTrue()
    {
        // Arrange
        Session session = Session.Establish(FederatedCredential(), CreatedAtUtc, ExpiresAtUtc);

        // Act, Assert
        await Assert.That(session.IsActiveAt(ExpiresAtUtc.AddSeconds(-1))).IsTrue();
    }

    [Test]
    public async Task IsActiveAt_AtTheExpiry_ReturnsFalse()
    {
        // Arrange — the boundary is exclusive: a session is live up to its expiry and not at it.
        Session session = Session.Establish(FederatedCredential(), CreatedAtUtc, ExpiresAtUtc);

        // Act, Assert — expiry ends a session on its own, so a reading that consults only
        // RevokedAtUtc would report an expired session as live.
        await Assert.That(session.IsActiveAt(ExpiresAtUtc)).IsFalse();
    }

    [Test]
    public async Task Session_ExposesNoPublicPropertySetter()
    {
        // Arrange
        PropertyInfo[] properties = typeof(Session).GetProperties(PublicInstance);

        // Act — the identity columns are immutable at the grant matrix, which refuses an UPDATE of
        // any of them; a public setter above would be a domain that disagrees with its own database
        // and reports 42501 instead of a sentence.
        string[] settableProperties = properties
            .Where(property => property.SetMethod is { IsPublic: true })
            .Select(property => property.Name)
            .ToArray();

        // Assert — joined rather than counted so a failure names the offending property.
        await Assert.That(string.Join(", ", settableProperties)).IsEqualTo(string.Empty);
        await Assert.That(properties.Length).IsGreaterThan(0);
    }

    private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;

    private static readonly DateTime CreatedAtUtc = new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    private static readonly DateTime ExpiresAtUtc = CreatedAtUtc.AddHours(1);

    private static Credential FederatedCredential() => Credential.CreateFederated(
        Guid.CreateVersion7(), Credential.GoogleProvider, "google-subject", CreatedAtUtc);

    /// <summary>
    /// Keeps the methods the type declares for itself and drops the ones every object has. Property
    /// accessors go too — the properties are rendered as properties below, with their accessors, so
    /// counting them again as <c>get_</c>/<c>set_</c> pairs would say the same thing twice.
    /// </summary>
    /// <remarks>
    /// The base definition is what decides, not the declaring type: overriding
    /// <see cref="object.ToString" /> is a rendering choice with no mutation path in it, and making
    /// this guard fail on one would only teach the next person to edit the guard.
    /// </remarks>
    private static bool IsDeclaredByTheType(MethodInfo method) =>
        !method.IsSpecialName && method.GetBaseDefinition().DeclaringType != typeof(object);

    /// <summary>
    /// Renders one public member as a signature string. Parameter names are carried as well as
    /// types, because a type-only signature would not show a <c>kind</c> parameter arriving where an
    /// instant already sits; property accessors are carried because a settable <c>Kind</c> is one of
    /// the two routes this test exists to refuse.
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
    /// The CLR name, except that a nullable value type is written the way the source writes it.
    /// <c>Nullable`1</c> would carry a backtick and no element type, which names nothing a reader
    /// could check against the declaration.
    /// </summary>
    private static string TypeName(Type type) =>
        Nullable.GetUnderlyingType(type) is { } underlying ? $"{underlying.Name}?" : type.Name;

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
