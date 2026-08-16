using System.Reflection;
using Domain.Common;
using Domain.Sessions;
using Domain.Users;

namespace UnitTests;

/// <summary>
/// Covers the three things <see cref="SessionToken" /> is: a type no presented token can travel back
/// out of, a factory that refuses a handle of the wrong width from both sides, and one definition of
/// "the hash of a token" rather than two that can drift.
/// </summary>
public sealed class SessionTokenTests
{
    [Test]
    public async Task SessionToken_ExposesNoWayToReadThePresentedToken()
    {
        // Arrange — the whole public surface, and then only the members a sequence of bytes or a
        // string could come back through. Those are the two shapes a token can leave in: the bytes
        // themselves, or a rendering of them.
        Type sessionToken = typeof(SessionToken);

        // Act
        IReadOnlyList<string> byteBearing = ByteBearingSurface.In(sessionToken);

        // Assert — pinned as a SET rather than checked for the absence of a name, because the member
        // that leaks a token will not be called Token. The two entries below are the only values this
        // type may ever hand back, and each is the digest: TokenHash is the stored one and HashOf
        // computes the same digest for a caller that has no session to compute it against. A third
        // entry is a new way for bytes to leave, whatever it is called, and it has to be looked at.
        //
        // What makes this worth pinning rather than trusting: the language already stops the ONE route
        // it can. The presented token is a ReadOnlySpan<byte>, a ref struct, so it cannot be assigned
        // to a field, captured by a lambda or closed over by an async method — it structurally cannot
        // escape For's body. Everything else is a rule only reflection can check: a property returning
        // the token, an overload accepting a caller-computed digest, a Base64 rendering added for a
        // log line. Each of those compiles, and each puts a live handle somewhere a digest was the
        // whole point.
        //
        // Rendered as CLR type names, not C# keywords — "Byte[]", not "byte[]". Editing a line here to
        // look like the declaration is how this test starts failing for no reason.
        string[] expected =
        [
            "SessionToken.HashOf : Byte[]",
            "SessionToken.TokenHash : ReadOnlyMemory<Byte>",
        ];
        await Assert.That(byteBearing).IsEquivalentTo(expected);

        // Non-vacuity, and it is not the same claim as the set above. A detector that matched nothing
        // would satisfy an emptied expectation; a detector that reached no members at all would too.
        // Both are asserted, so a reflection query that stopped finding members fails here rather than
        // going quiet.
        await Assert.That(byteBearing).IsNotEmpty();
        await Assert.That(ByteBearingSurface.MemberCount(sessionToken)).IsGreaterThan(4);
    }

    [Test]
    public async Task Detector_ReportsAMemberAPresentedTokenCouldTravelOutThrough()
    {
        // Arrange — the permanent negative control, on a synthetic type so the proof survives the day
        // SessionToken is clean rather than being a sentence about a change somebody reverted. Two
        // offending shapes, because they are the two a real leak arrives in: the raw bytes, and a
        // rendering of them added for a log line or a response body.
        Type probe = typeof(LeakyHandle);

        // Act — the same call the test above makes, over the same detector.
        IReadOnlyList<string> byteBearing = ByteBearingSurface.In(probe);

        // Assert — both offenders are named, which is what says the pin above is looking for the right
        // shape rather than passing because it is looking for nothing. Neither is called "token" in a
        // way a name rule could catch, on purpose: PresentedHandle is the member a well-meaning
        // refactor produces, and Rendered is what somebody adds to make a failure readable.
        await Assert.That(byteBearing).Contains("LeakyHandle.PresentedHandle : Byte[]");
        await Assert.That(byteBearing).Contains("LeakyHandle.Rendered : String");

        // And the members that carry no bytes are NOT named, so the control cannot be passing because
        // the detector reports everything it sees — which would make the set above pass only by being
        // edited to match, and would catch nothing ever again.
        await Assert.That(byteBearing).DoesNotContain("LeakyHandle.SessionId : Guid");
        await Assert.That(byteBearing).DoesNotContain("LeakyHandle.Length : Int32");
    }

    [Test]
    public async Task Detector_ReportsNothingForATypeThatHandsBackNoBytes()
    {
        // Arrange — the other direction of the same control. Without it, a detector reporting every
        // member of every type would satisfy the offender assertions above while making the pin on
        // SessionToken a list somebody has to keep in step with the whole surface.
        Type probe = typeof(SealedHandle);

        // Act
        IReadOnlyList<string> byteBearing = ByteBearingSurface.In(probe);

        // Assert — joined rather than counted, so a failure names what it wrongly reported, and the
        // member count beside it says the detector really walked a type with members in it.
        await Assert.That(string.Join(", ", byteBearing)).IsEqualTo(string.Empty);
        await Assert.That(ByteBearingSurface.MemberCount(probe)).IsGreaterThan(0);
    }

    [Test]
    public async Task For_WithATokenShorterThanThirtyTwoBytes_ThrowsValidationException()
    {
        // Arrange, Act — a handle one byte short, which is the direction a truncating caller produces.
        ValidationException exception = ThrowsValidationException(() =>
            SessionToken.For(LiveSession(), Token(SessionToken.TokenLength - 1)));

        // Assert — this is the ONLY place a short token stops, and the database structurally cannot
        // help: CK_session_tokens_token_hash_length watches the digest, which is 32 bytes whatever went
        // into it, so a short token hashes to a perfectly well-formed row nothing downstream could tell
        // from a real one. What a caller would have shipped is a handle carrying less entropy than the
        // design claims, in a system where every other layer reports it as fine.
        //
        // Keyed on the property the value ends up in, as PasskeyPublicKey.Register and
        // RecoveryCodeHash.From key their own length refusals, so a caller reading the errors gets the
        // same shape from all three.
        await Assert.That(exception.Errors.ContainsKey(nameof(SessionToken.TokenHash))).IsTrue();
    }

    [Test]
    public async Task For_WithATokenLongerThanThirtyTwoBytes_ThrowsValidationException()
    {
        // Arrange, Act — the mirror, and it is owed separately rather than folded into the test above.
        // A bound written as a minimum passes the short test the moment somebody "relaxes" it, and
        // nothing else in the system would object: a long token is not a stronger handle, it is the
        // minting path and this one disagreeing about what a token is.
        ValidationException exception = ThrowsValidationException(() =>
            SessionToken.For(LiveSession(), Token(SessionToken.TokenLength + 1)));

        // Assert — refused rather than truncated, and the difference is the whole point. Truncating
        // would store the hash of a PREFIX, so the row would be well-formed and the session would never
        // be found by the handle its own cookie carries — a silent sign-out with nothing to read.
        await Assert.That(exception.Errors.ContainsKey(nameof(SessionToken.TokenHash))).IsTrue();
    }

    [Test]
    public async Task For_StoresTheHashOfTheTokenAndNotTheToken()
    {
        // Arrange — a token whose bytes are recognisable, so "the stored value is not the token" is a
        // comparison a reader can follow rather than a claim about two opaque buffers.
        Session session = LiveSession();
        byte[] token = Token(SessionToken.TokenLength);

        // Act
        SessionToken stored = SessionToken.For(session, token);

        // Assert — against a KNOWN-ANSWER SHA-256 rather than against a digest this test computes with
        // the same call the production code makes. Recomputing would agree with any hash function the
        // two happened to share, so it would pin "both sides call the same thing" and not "the value is
        // SHA-256 of the token" — and the second is what a stored digest has to be for a backup or a
        // replica of this table to be worth nothing.
        await Assert.That(Hex(stored.TokenHash.Span))
            .IsEqualTo("630dcd2966c4336691125448bbb25b4ff412a49c732db2c8abc1b8581bd710dd");

        // And the token itself is not what landed. This looks redundant beside the line above — a
        // digest is not its input — and it is kept because it is the assertion that fails FIRST and
        // most legibly on the one change that matters: a factory storing the raw bytes.
        await Assert.That(stored.TokenHash.Span.SequenceEqual(token)).IsFalse();

        // Both ids come off the session rather than being supplied, which is what makes a row whose
        // owner disagrees with its session unrepresentable at this layer as well as at the schema's.
        await Assert.That(stored.SessionId).IsEqualTo(session.Id);
        await Assert.That(stored.UserId).IsEqualTo(session.UserId);
    }

    [Test]
    public async Task HashOf_ProducesTheSameDigestForStores()
    {
        // Arrange — a different token from the test above, so this cannot pass on a value that file
        // already pinned.
        byte[] token = Token(SessionToken.TokenLength, fill: 0x00);

        // Act — the two spellings of "the hash of a token": the one a write goes through, and the one
        // the lookup uses because it has no session to hand For.
        SessionToken stored = SessionToken.For(LiveSession(), token);
        byte[] looked = SessionToken.HashOf(token);

        // Assert — if these two ever disagree, NO SESSION IN THE SYSTEM IS EVER FOUND, and the symptom
        // is silent: every request simply arrives unauthenticated, with no error naming a cause. That
        // is why For calls HashOf rather than repeating the digest, and this is the test that says the
        // call is still there — a For that inlined SHA256.HashData would pass every other test in this
        // file and this one too, which is the honest limit of what it proves. What it does refuse is
        // the version of that mistake that matters: two implementations that have drifted.
        await Assert.That(looked).IsEquivalentTo(stored.TokenHash.ToArray());

        // Against the known answer as well, for the reason the test above gives: agreement between two
        // implementations of the wrong function is still agreement.
        await Assert.That(Hex(looked))
            .IsEqualTo("66687aadf862bd776c8fc18b8e9f8e20089714856ee233b3902a591d0d5f2925");
    }

    [Test]
    public async Task For_WithoutASession_ThrowsArgumentNullException()
    {
        // Arrange, Act, Assert — the session is where both ids come from, so there is nothing to file
        // without one. An ArgumentNullException rather than a ValidationException, exactly as
        // Session.Establish answers the same shape: no user typed this, a caller handed over nothing.
        await Assert.That(() => SessionToken.For(null!, Token(SessionToken.TokenLength)))
            .Throws<ArgumentNullException>();
    }

    private static readonly DateTime CreatedAtUtc = new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    private static readonly DateTime ExpiresAtUtc = CreatedAtUtc.AddHours(1);

    /// <summary>
    /// A live session to hang a handle off. Federated because nothing here reads the kind, and the
    /// cheapest credential the domain mints is the right one.
    /// </summary>
    private static Session LiveSession() => Session.Establish(
        Credential.CreateFederated(
            Guid.CreateVersion7(), Credential.GoogleProvider, "google-subject", CreatedAtUtc),
        CreatedAtUtc,
        ExpiresAtUtc);

    /// <summary>
    /// A token of <paramref name="length" /> bytes: <c>0x00, 0x01, …</c> from <paramref name="fill" />
    /// when it is null, or that byte repeated when it is not.
    /// </summary>
    /// <remarks>
    /// The counting pattern is what makes the known-answer vectors in this file readable — a reader can
    /// reproduce <c>sha256(bytes(range(32)))</c> and <c>sha256(bytes(32))</c> in one line of anything.
    /// </remarks>
    private static byte[] Token(int length, byte? fill = null) =>
        fill is { } value
            ? [.. Enumerable.Repeat(value, length)]
            : [.. Enumerable.Range(0, length).Select(index => (byte)index)];

    private static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(bytes);

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

    /// <summary>
    /// Finds the public members of a type through which a sequence of bytes — or a rendering of one —
    /// can come back out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It takes a <see cref="Type" /> rather than reaching for <see cref="SessionToken" /> itself,
    /// which is what lets the synthetic probes above prove the detector works without anyone editing
    /// the real type to watch it go red. Same reason
    /// <c>OwnershipKeyImmutabilityTests.OwnershipKeys</c> takes its types as an argument.
    /// </para>
    /// <para>
    /// <b>What it deliberately does not look at:</b> parameters. Accepting a token is the whole job of
    /// <see cref="SessionToken.For" /> and of <see cref="SessionToken.HashOf" />; the rule is about what
    /// comes back. A member that took a token and wrote it somewhere would walk past this, which is a
    /// limit and not a defect to fix by widening the scan — the type has no field a token could reach
    /// and no collaborator to hand one to, and that is what the surface pin above is for.
    /// </para>
    /// </remarks>
    private static class ByteBearingSurface
    {
        private const BindingFlags PublicSurface =
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

        /// <summary>Renders every byte-bearing member as "Type.Member : Type".</summary>
        internal static IReadOnlyList<string> In(Type type) =>
        [
            .. MembersOf(type)
                .Where(member => CarriesBytes(TypeOf(member)))
                .Select(member => $"{type.Name}.{member.Name} : {Describe(TypeOf(member))}")
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];

        /// <summary>How many members the scan reached at all, byte-bearing or not.</summary>
        internal static int MemberCount(Type type) => MembersOf(type).Count();

        private static IEnumerable<MemberInfo> MembersOf(Type type) =>
        [
            .. type.GetProperties(PublicSurface),
            .. type.GetFields(PublicSurface),

            // Property accessors are dropped as methods because the properties are already carried
            // above; object's own members are dropped because ToString is a rendering choice on every
            // type in the runtime and failing on it would only teach the next person to edit the scan.
            .. type.GetMethods(PublicSurface)
                .Where(method => !method.IsSpecialName
                                 && method.GetBaseDefinition().DeclaringType != typeof(object)),
        ];

        /// <summary>
        /// The type a member hands back: a property's or field's own type, a method's return type.
        /// </summary>
        private static Type TypeOf(MemberInfo member) => member switch
        {
            PropertyInfo property => property.PropertyType,
            FieldInfo field => field.FieldType,
            MethodInfo method => method.ReturnType,
            _ => typeof(void),
        };

        /// <summary>
        /// Whether a value of this type could carry a token out.
        /// </summary>
        /// <remarks>
        /// The generic arm is written structurally rather than as a list of names —
        /// <c>Memory&lt;byte&gt;</c>, <c>Span&lt;byte&gt;</c>, <c>IReadOnlyList&lt;byte&gt;</c> and
        /// every other single-argument container of bytes match the same rule — because a list of names
        /// is a list somebody has to remember to extend, and the container that gets used instead is the
        /// one nobody added. <see cref="string" /> is in because a Base64 rendering of a handle is the
        /// shape a leak takes when somebody wants a log line to be readable.
        /// </remarks>
        private static bool CarriesBytes(Type type) =>
            type == typeof(string)
            || (type.IsArray && type.GetElementType() == typeof(byte))
            || (type.IsGenericType
                && type.GetGenericArguments() is [Type argument]
                && argument == typeof(byte));

        private static string Describe(Type type) => type.IsGenericType
            ? $"{type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)]}"
              + $"<{string.Join(", ", type.GetGenericArguments().Select(argument => argument.Name))}>"
            : type.Name;
    }

    /// <summary>
    /// The permanent negative control: a handle that hands its token back, in the two shapes a real
    /// leak arrives in.
    /// </summary>
    /// <remarks>
    /// Neither member is named "token", on purpose. A detector that worked by name would pass on this
    /// type and catch nothing in anger, which is exactly the weakness the byte-shaped rule replaces.
    /// </remarks>
    private sealed class LeakyHandle
    {
        public byte[] PresentedHandle { get; } = [];

        public string Rendered { get; } = string.Empty;

        public Guid SessionId { get; }

        public int Length { get; }
    }

    /// <summary>
    /// The other half of the control: a handle carrying members and no bytes, so a detector that
    /// reported everything would be caught rather than mistaken for a strict one.
    /// </summary>
    private sealed class SealedHandle
    {
        public Guid SessionId { get; }

        public Guid UserId { get; }

        public int Length => 0;
    }
}
