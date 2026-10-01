using System.Reflection;
using Domain.Common;
using Domain.Users;

namespace UnitTests;

public sealed class PasskeyPublicKeyTests
{
    [Test]
    public async Task Register_AgainstAFederatedCredential_Throws()
    {
        // Arrange — a federated credential has no authenticator behind it, so there is no public key
        // to record against one.
        Credential credential = FederatedCredential();

        // Act
        ValidationException exception = ThrowsValidationException(() => PasskeyPublicKey.Register(
            credential, WebAuthnCredentialId(), CoseKey(), CoseAlgorithm.Es256));

        // Assert — refused rather than coerced into a passkey row. A public key filed under a
        // credential the identity provider owns would let a provider sign-in be verified as if an
        // authenticator had signed it, which is the one confusion this type exists to prevent.
        await Assert.That(exception.Errors.ContainsKey(nameof(PasskeyPublicKey.CredentialType)))
            .IsTrue();
    }

    [Test]
    public async Task Register_CopiesTheCredentialsOwnerIdAndType()
    {
        // Arrange
        Credential credential = PasskeyCredential();
        ReadOnlyMemory<byte> webAuthnCredentialId = WebAuthnCredentialId();
        ReadOnlyMemory<byte> coseKey = CoseKey();

        // Act
        PasskeyPublicKey key = PasskeyPublicKey.Register(
            credential, webAuthnCredentialId, coseKey, CoseAlgorithm.Es256);

        // Assert — the owner is copied from the credential rather than passed in beside it, so there
        // is no call shape that files a key under one person's credential and another person's user
        // id. The credential type rides along for the same reason the session row carries it: the
        // database can then check the derivation instead of trusting that every INSERT came through
        // this factory.
        await Assert.That(key.CredentialId).IsEqualTo(credential.Id);
        await Assert.That(key.UserId).IsEqualTo(credential.UserId);
        await Assert.That(key.CredentialType).IsEqualTo(CredentialType.Passkey);
        await Assert.That(key.WebAuthnCredentialId.ToArray()).IsEquivalentTo(webAuthnCredentialId.ToArray());
        await Assert.That(key.CoseKey.ToArray()).IsEquivalentTo(coseKey.ToArray());
        await Assert.That(key.Algorithm).IsEqualTo(CoseAlgorithm.Es256);
    }

    [Test]
    public async Task Register_DoesNotAliasTheCallersBuffer()
    {
        // Arrange — a ReadOnlyMemory<byte> is a view over an array the caller still owns and can still
        // write to. Buffers a caller is plausibly reusing across registrations.
        byte[] webAuthnCredentialIdBuffer = Bytes(MinWebAuthnCredentialIdLength).ToArray();
        byte[] coseKeyBuffer = CoseKey().ToArray();
        byte[] originalWebAuthnCredentialId = [.. webAuthnCredentialIdBuffer];
        byte[] originalCoseKey = [.. coseKeyBuffer];

        // Act
        PasskeyPublicKey key = PasskeyPublicKey.Register(
            PasskeyCredential(), webAuthnCredentialIdBuffer, coseKeyBuffer, CoseAlgorithm.Es256);
        webAuthnCredentialIdBuffer.AsSpan().Fill(0xFF);
        coseKeyBuffer.AsSpan().Fill(0xFF);

        // Assert — the factory copies rather than aliases. Without this, a caller that rents or reuses
        // a buffer rewrites a stored public key from a distance, and the registered credential stops
        // matching the authenticator that produced it with nothing in the code path to point at.
        await Assert.That(key.WebAuthnCredentialId.ToArray())
            .IsEquivalentTo(originalWebAuthnCredentialId);
        await Assert.That(key.CoseKey.ToArray()).IsEquivalentTo(originalCoseKey);
    }

    [Test]
    public async Task Register_WithAnOverlongWebAuthnCredentialId_Throws()
    {
        // Arrange — one byte past what the column holds.
        ReadOnlyMemory<byte> webAuthnCredentialId = Bytes(MaxWebAuthnCredentialIdLength + 1);

        // Act
        ValidationException exception = ThrowsValidationException(() => PasskeyPublicKey.Register(
            PasskeyCredential(), webAuthnCredentialId, CoseKey(), CoseAlgorithm.Es256));

        // Assert
        await Assert.That(exception.Errors.ContainsKey(nameof(PasskeyPublicKey.WebAuthnCredentialId)))
            .IsTrue();
    }

    [Test]
    public async Task Register_WithATooShortWebAuthnCredentialId_Throws()
    {
        // Arrange — one byte short of the floor. The lower bound is not decoration: an identifier
        // that small could not have come from a conforming authenticator, and accepting it would let
        // a guessable value be filed as a credential handle.
        ReadOnlyMemory<byte> webAuthnCredentialId = Bytes(MinWebAuthnCredentialIdLength - 1);

        // Act
        ValidationException exception = ThrowsValidationException(() => PasskeyPublicKey.Register(
            PasskeyCredential(), webAuthnCredentialId, CoseKey(), CoseAlgorithm.Es256));

        // Assert
        await Assert.That(exception.Errors.ContainsKey(nameof(PasskeyPublicKey.WebAuthnCredentialId)))
            .IsTrue();
    }

    [Test]
    public async Task Register_WithAnEmptyCoseKey_Throws()
    {
        // Arrange — the key material is the whole point of the row; an empty one verifies nothing and
        // would leave a credential that can never be used to sign in.
        ReadOnlyMemory<byte> coseKey = Bytes(0);

        // Act
        ValidationException exception = ThrowsValidationException(() => PasskeyPublicKey.Register(
            PasskeyCredential(), WebAuthnCredentialId(), coseKey, CoseAlgorithm.Es256));

        // Assert
        await Assert.That(exception.Errors.ContainsKey(nameof(PasskeyPublicKey.CoseKey))).IsTrue();
    }

    [Test]
    public async Task Register_WithAnOverlongCoseKey_Throws()
    {
        // Arrange — one byte past what the column holds.
        ReadOnlyMemory<byte> coseKey = Bytes(MaxCoseKeyLength + 1);

        // Act
        ValidationException exception = ThrowsValidationException(() => PasskeyPublicKey.Register(
            PasskeyCredential(), WebAuthnCredentialId(), coseKey, CoseAlgorithm.Es256));

        // Assert
        await Assert.That(exception.Errors.ContainsKey(nameof(PasskeyPublicKey.CoseKey))).IsTrue();
    }

    [Test]
    public async Task Register_WithAnUndeclaredAlgorithm_Throws()
    {
        // Arrange — an enum in C# holds any value of its underlying type, so a number that names no
        // member arrives as a perfectly valid CoseAlgorithm and only a range check catches it. This
        // is the value a COSE header carrying an algorithm the product does not verify would produce.
        CoseAlgorithm algorithm = (CoseAlgorithm)(-65535);

        // Act
        ValidationException exception = ThrowsValidationException(() => PasskeyPublicKey.Register(
            PasskeyCredential(), WebAuthnCredentialId(), CoseKey(), algorithm));

        // Assert — storing an algorithm nothing can verify would produce a credential that fails at
        // sign-in rather than at registration, which is the expensive end to find out.
        await Assert.That(exception.Errors.ContainsKey(nameof(PasskeyPublicKey.Algorithm))).IsTrue();
    }

    [Test]
    public async Task Register_WithANullCredential_Throws()
    {
        // Arrange, Act, Assert — the owner and the type are both read off the credential, so there is
        // nothing to register without one. An ArgumentNullException rather than a ValidationException
        // for the reason Session.Establish gives: no user typed this, a caller handed over nothing.
        await Assert.That(() => PasskeyPublicKey.Register(
                null!, WebAuthnCredentialId(), CoseKey(), CoseAlgorithm.Es256))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task PasskeyPublicKey_ExposesNoWayToChangeItsKeyOrItsOwner()
    {
        // Arrange — every public way into the type: constructors, methods including static ones,
        // properties, and fields.
        MemberInfo[] surface =
        [
            .. typeof(PasskeyPublicKey).GetConstructors(PublicInstance),
            .. typeof(PasskeyPublicKey).GetMethods(PublicInstance | BindingFlags.Static)
                .Where(IsDeclaredByTheType),
            .. typeof(PasskeyPublicKey).GetProperties(PublicInstance),
            .. typeof(PasskeyPublicKey).GetFields(PublicInstance),
        ];

        // Act
        string[] signatures = surface
            .Select(Describe)
            .OrderBy(signature => signature, StringComparer.Ordinal)
            .ToArray();

        // Assert — this is what makes "the key material and its owner are fixed at registration"
        // unrepresentable rather than merely untested. The copying test above only proves what the
        // factory assigns today; without this, the obvious accommodation for a caller that wants a
        // key on a credential of its own choosing is a second factory taking a CredentialType, or a
        // setter on one of these properties, and the rule dissolves with no red test anywhere.
        //
        // The whole surface is pinned rather than filtered for members that mention CredentialType,
        // because the routes that do not mention it are the ones a filter misses: a no-argument
        // Rotate() reassigning CoseKey takes no parameter and is no property, and a public field is
        // not a property at all. Anything that moves this set changes what a caller can reach, and it
        // has to be looked at.
        //
        // These are CLR type names as reflection renders them, not the C# keywords the source is
        // written in: "Byte", not "byte". Editing a line here to look like the declaration is how
        // this test starts failing for no reason.
        string[] expected =
        [
            "CoseAlgorithm Algorithm { get; }",
            "CredentialType CredentialType { get; }",
            "Guid CredentialId { get; }",
            "Guid UserId { get; }",
            "ReadOnlyMemory<Byte> CoseKey { get; }",
            "ReadOnlyMemory<Byte> WebAuthnCredentialId { get; }",
            "static PasskeyPublicKey Register(Credential credential, "
            + "ReadOnlyMemory<Byte> webAuthnCredentialId, ReadOnlyMemory<Byte> coseKey, "
            + "CoseAlgorithm algorithm)",
        ];
        await Assert.That(signatures).IsEquivalentTo(expected);

        // A reflection query that silently returned nothing would pass an emptied expectation above
        // while proving nothing at all.
        await Assert.That(surface.Length).IsGreaterThan(0);
    }

    private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;

    // The bounds the factory enforces, restated here so a failure reads as a boundary rather than as
    // an unexplained number. A constant appearing on the production type later should replace these.
    private const int MinWebAuthnCredentialIdLength = 16;

    private const int MaxWebAuthnCredentialIdLength = 1023;

    private const int MaxCoseKeyLength = 1024;

    private static readonly DateTime CreatedAtUtc = new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    private static Credential PasskeyCredential() =>
        Credential.CreatePasskey(Guid.CreateVersion7(), CreatedAtUtc);

    private static Credential FederatedCredential() => Credential.CreateFederated(
        Guid.CreateVersion7(), Credential.GoogleProvider, "google-subject", CreatedAtUtc);

    private static ReadOnlyMemory<byte> WebAuthnCredentialId() =>
        Bytes(MinWebAuthnCredentialIdLength);

    private static ReadOnlyMemory<byte> CoseKey() => Bytes(77);

    /// <summary>
    /// A buffer of the requested length whose contents vary by position, so that a factory copying
    /// the wrong argument is visible in an assertion rather than hidden behind two identical runs of
    /// zeroes.
    /// </summary>
    private static ReadOnlyMemory<byte> Bytes(int length) =>
        Enumerable.Range(1, length).Select(value => (byte)value).ToArray();

    /// <summary>
    /// Keeps the methods the type declares for itself and drops the ones every object has. Property
    /// accessors go too — the properties are rendered as properties below, with their accessors, so
    /// counting them again as <c>get_</c>/<c>set_</c> pairs would say the same thing twice.
    /// </summary>
    private static bool IsDeclaredByTheType(MethodInfo method) =>
        !method.IsSpecialName && method.GetBaseDefinition().DeclaringType != typeof(object);

    /// <summary>
    /// Renders one public member as a signature string. Parameter names are carried as well as
    /// types, because a type-only signature would not show a <c>credentialType</c> parameter arriving
    /// where a credential already sits; property accessors are carried because a settable
    /// <c>CoseKey</c> is one of the routes this test exists to refuse.
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
    /// constructed generic is written with its arguments. <c>ReadOnlyMemory`1</c> would carry a
    /// backtick and no element type, which would let a key of the wrong element type pass unnoticed.
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
