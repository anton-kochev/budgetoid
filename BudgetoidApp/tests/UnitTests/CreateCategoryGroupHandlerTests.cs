using Application.CategoryGroups;
using Application.CategoryGroups.CreateCategoryGroup;
using Domain.CategoryGroups;
using Domain.Common;
using Domain.Security;
using Microsoft.Extensions.Time.Testing;
using TestSupport;
using TUnit.Assertions.Enums;
using UnitTests.Fakes;

namespace UnitTests;

/// <summary>
/// The create leg of <c>/api/category-groups</c>, once its body carried four members this server cannot
/// read a single one of.
/// </summary>
/// <remarks>
/// <para>
/// <b>The description is the fourth member and the only optional one, and its absence test is the whole
/// of a rule.</b> <c>command.Description is null</c> — never <c>string.IsNullOrEmpty</c> and never
/// <c>string.IsNullOrWhiteSpace</c>. The decoder underneath refuses <see langword="null" /> and the empty
/// string identically, so the distinction cannot live down there: either of the forgiving spellings folds
/// a malformed <c>""</c> into "absent" and writes NULL where a 400 was owed. That failure is silent all
/// the way down — the row is legal, the response is a 201, and the note the person typed is gone.
/// <see cref="HandleAsync_WithNoDescription_StoresNull" /> and
/// <see cref="HandleAsync_WithAnEmptyStringDescription_ThrowsValidationExceptionKeyedOnTheDescription" />
/// are the pair that tells the two spellings apart, and neither is meaningful without the other.
/// </para>
/// <para>
/// <b>Two caps, and the description's has only one owner.</b> <c>CiphertextEnvelopeTextTests</c> pins
/// the DECODER at <see cref="NarrativeFieldLimits.DescriptionBytes" /> in both directions, which is a
/// different claim from the one owed here: that this handler hands the description's cap to the
/// description's decode and the name's to the name's. The name's ceiling is stated twice — once by the
/// handler and again inside <see cref="IndexedName.Of" /> — so a number mistyped on that line is refused
/// by the domain. The description's is stated once. Three cases carry the difference:
/// <see cref="HandleAsync_WithADescriptionPastTheNameCap_IsAccepted" /> says the ceiling is not the
/// name's,
/// <see cref="HandleAsync_WithADescriptionOverItsOwnCap_ThrowsValidationExceptionKeyedOnTheDescription" />
/// says there is one at all, and
/// <see cref="HandleAsync_WithANameOverItsOwnCap_ThrowsValidationExceptionRatherThanArgumentException" />
/// says the name's refusal is the caller's 400 and not the domain's 500. The last two build their
/// envelopes by hand: <c>SealedNarrative</c> caps by construction and therefore cannot produce the values
/// that catch this.
/// </para>
/// </remarks>
public sealed class CreateCategoryGroupHandlerTests
{
    [Test]
    public async Task HandleAsync_StampsTheAmbientBudgetPersistsAndReturnsTheDto()
    {
        // Arrange — an id minted here and threaded in, so the assertion below is not "an id came back"
        // but "this one did".
        //
        // THE LABEL IS "Lifestyle" AND THAT IS NOT A COSMETIC CHOICE. This is the one case in the file
        // that pins the ENCODING of the name the DTO hands back, and it was written with "Essentials",
        // which cannot see the defect it exists for. Measured: SealedNarrative.Name("Essentials") is 39
        // bytes — a multiple of three, so standard base64 emits no padding — and none of those 39 bytes
        // produces an alphabet index of 62 or 63, so no '+' or '/' appears either. Padded standard
        // base64 and unpadded base64url therefore spell that envelope CHARACTER-IDENTICALLY, and
        // CategoryGroupDto.FromCategoryGroup encoding through Convert.ToBase64String instead of
        // PasskeyEncoding.Encode reddened NOTHING in a 47-case mutation run.
        //
        // "Lifestyle" is 38 bytes and disagrees on both counts at once — it pads, and it carries a byte
        // in the 62/63 range. A replacement label has to be checked for both: 42 bytes ("Sinking
        // Funds") pads not at all and is still caught by the alphabet alone, so length parity is not by
        // itself the test. See SealedNarrative.EncodedName for the rule.
        var id = Guid.CreateVersion7();
        Fixture fixture = Fixture.Create();

        // Act
        CategoryGroupDto dto = await fixture.Handler.HandleAsync(Command(id, "Lifestyle"));
        CategoryGroup? stored = await fixture.Groups.GetByIdAsync(dto.Id);

        // Assert — the id the CALLER sent, not one the handler or the factory minted. It is the
        // associated data both narrative members were sealed against, so a path that invented one
        // produces a row whose name and description nobody can open, with nothing else in this test
        // noticing.
        //
        // The budget and the instant come from the handler and not from the repository, and the position
        // is the server's own: sealing took no capability away from an int, so the append rule stays
        // here and Position is deliberately NOT a member of the command.
        await Assert.That(fixture.Groups.AddCallCount).IsEqualTo(1);
        await Assert.That(stored).IsNotNull();
        await Assert.That(stored!.BudgetId).IsEqualTo(fixture.BudgetId);
        await Assert.That(stored.CreatedAtUtc).IsEqualTo(Fixture.CreatedAtUtc.UtcDateTime);
        await Assert.That(dto.Id).IsEqualTo(id);
        await Assert.That(dto.Name).IsEqualTo(SealedNarrative.EncodedName("Lifestyle"));
        await Assert.That(dto.Position).IsEqualTo(0);
    }

    [Test]
    public async Task HandleAsync_AppendsTheNextPosition()
    {
        // Arrange — position is the one member of this row the server still computes, because it is the
        // one member it can still read.
        Fixture fixture = Fixture.Create();

        // Act
        CategoryGroupDto first =
            await fixture.Handler.HandleAsync(Command(Guid.CreateVersion7(), "Essentials"));
        CategoryGroupDto second =
            await fixture.Handler.HandleAsync(Command(Guid.CreateVersion7(), "Lifestyle"));

        // Assert
        await Assert.That(first.Position).IsEqualTo(0);
        await Assert.That(second.Position).IsEqualTo(1);
    }

    [Test]
    public async Task HandleAsync_StoresBothHalvesOfTheNameItWasSent()
    {
        // Arrange — the DTO returns no blind index, deliberately, so the only way to see the index the
        // handler stored is to read the entity back off the repository.
        Fixture fixture = Fixture.Create();

        // Act
        CategoryGroupDto dto =
            await fixture.Handler.HandleAsync(Command(Guid.CreateVersion7(), "Essentials"));
        CategoryGroup? stored = await fixture.Groups.GetByIdAsync(dto.Id);

        // Assert — the handler decodes two opaque members and hands them to one IndexedName. A handler
        // that decoded the name twice, or that passed the envelope where the index belongs, would still
        // return a 201 and a DTO indistinguishable from this one, because the index is on no read.
        //
        // CollectionOrdering.Matching IS PART OF THE ASSERTION. IsEqualTo over two byte[] compares
        // REFERENCES and fails even when the contents and the order agree, and TUnit's failure message
        // names IsEquivalentTo as the fix — which DEFAULTS TO CollectionOrdering.Any and would then pass
        // on any permutation of an envelope's bytes. Order is the whole of what a ciphertext is.
        await Assert.That(stored).IsNotNull();
        await Assert.That(stored!.Name.Envelope.ToArray())
            .IsEquivalentTo(
                SealedNarrative.Name("Essentials").Envelope.ToArray(), CollectionOrdering.Matching);
        await Assert.That(stored.NameKey.ToArray())
            .IsEquivalentTo(
                SealedNarrative.BlindIndex("Essentials").ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task HandleAsync_WithADescription_StoresItAndHandsItBack()
    {
        // Arrange — a description whose label differs from the name's, so that a handler crossing the two
        // members is visible. SealedNarrative's filler is position-varying, so two labels share no byte
        // at any offset.
        Fixture fixture = Fixture.Create();

        // Act
        CategoryGroupDto dto = await fixture.Handler.HandleAsync(
            Command(Guid.CreateVersion7(), "Essentials", "Required spending"));
        CategoryGroup? stored = await fixture.Groups.GetByIdAsync(dto.Id);

        // Assert — THE ONLY GUARD ON A DEFECT NOTHING ELSE IN THE STACK CAN SEE. The description column
        // is nullable, so a handler that decoded this member and then forgot to pass it on writes NULL:
        // a legal row, no constraint violated, a 201 on the wire, and a note the person typed silently
        // gone. On the NOT NULL name the same omission is a 23502 from the database. Do not weaken
        // either half of this to "the member is present" — the value has to read back.
        await Assert.That(stored).IsNotNull();
        await Assert.That(stored!.Description).IsNotNull();
        await Assert.That(stored.Description!.Envelope.ToArray())
            .IsEquivalentTo(
                SealedNarrative.Description("Required spending").Envelope.ToArray(),
                CollectionOrdering.Matching);
        await Assert.That(dto.Description)
            .IsEqualTo(SealedNarrative.EncodedDescription("Required spending"));
    }

    [Test]
    public async Task HandleAsync_WithNoDescription_StoresNull()
    {
        // Arrange — the absent member, which is what a client that has no note to file sends. Half of the
        // pair the class remarks describe: on its own this case is satisfied by IsNullOrEmpty too.
        Fixture fixture = Fixture.Create();

        // Act
        CategoryGroupDto dto =
            await fixture.Handler.HandleAsync(Command(Guid.CreateVersion7(), "Essentials"));
        CategoryGroup? stored = await fixture.Groups.GetByIdAsync(dto.Id);

        // Assert — null on the DTO too, and NOT the empty string. `""` is not a legal envelope and a
        // client cannot tell one from the other, so a `?? string.Empty` anywhere on the read side would
        // hand back a value nothing can decode.
        await Assert.That(stored).IsNotNull();
        await Assert.That(stored!.Description).IsNull();
        await Assert.That(dto.Description).IsNull();
    }

    [Test]
    public async Task HandleAsync_WithAnEmptyStringDescription_ThrowsValidationExceptionKeyedOnTheDescription()
    {
        // Arrange — THE CASE THAT TELLS `is null` FROM `IsNullOrEmpty`, and the only one that can.
        // PasskeyEncoding.TryDecode refuses null and "" identically, so the decoder underneath cannot
        // make this distinction and the handler has to. Under IsNullOrEmpty this body 201s and stores
        // NULL; under `is null` it is a malformed member and a 400 the caller can act on.
        Fixture fixture = Fixture.Create();

        // Act
        ValidationException exception = await ThrowsAsync<ValidationException>(() =>
            fixture.Handler.HandleAsync(new CreateCategoryGroupCommand(
                Guid.CreateVersion7().ToString("D"),
                SealedNarrative.EncodedName("Essentials"),
                SealedNarrative.EncodedIndex("Essentials"),
                string.Empty)));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("Description")).IsTrue();
        await Assert.That(fixture.Groups.AddCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task HandleAsync_WithADescriptionPastTheNameCap_IsAccepted()
    {
        // Arrange — an envelope longer than NarrativeFieldLimits.NameBytes and well under
        // DescriptionBytes. This is the value that separates a handler passing the description's own cap
        // to its decode from one that pasted the name's line and changed the member: the second refuses
        // this body with a 400 nobody can act on, because the client sent a description the column
        // accepts.
        Fixture fixture = Fixture.Create();
        string longLabel = new('x', NarrativeFieldLimits.NameBytes);

        // Act
        CategoryGroupDto dto = await fixture.Handler.HandleAsync(
            Command(Guid.CreateVersion7(), "Essentials", longLabel));
        CategoryGroup? stored = await fixture.Groups.GetByIdAsync(dto.Id);

        // Assert
        await Assert.That(stored).IsNotNull();
        await Assert.That(stored!.Description).IsNotNull();
        await Assert.That(stored.Description!.Envelope.Length)
            .IsGreaterThan(NarrativeFieldLimits.NameBytes);
    }

    [Test]
    public async Task HandleAsync_WithADescriptionOverItsOwnCap_ThrowsValidationExceptionKeyedOnTheDescription()
    {
        // Arrange — THE OTHER HALF OF THE CAP, AND IT HAS ONLY ONE OWNER ON THIS PATH. The name's ceiling
        // is stated twice — once here and again inside IndexedName.Of — so a mistyped name cap is refused
        // by the domain as a defect in this codebase. The description's is stated ONCE, by this handler,
        // at its decode and again at NarrativeField.SealedOrAbsent with the same constant. Widen it, or
        // hand int.MaxValue to either, and every other case in this file still passes: an over-cap
        // description travels the whole ring and lands on CK_category_groups_description_length, which
        // reaches a caller as a 23514 nothing translates rather than as the 400 it should have been.
        //
        // The envelope is built here and not by SealedNarrative, because that fixture caps at
        // DescriptionBytes by construction and therefore CANNOT produce the one value that catches this.
        // A version byte and a length is the whole of what the check reads.
        Fixture fixture = Fixture.Create();
        byte[] overCap = new byte[NarrativeFieldLimits.DescriptionBytes + 1];
        overCap[0] = CiphertextEnvelope.Version;

        // Act
        ValidationException exception = await ThrowsAsync<ValidationException>(() =>
            fixture.Handler.HandleAsync(new CreateCategoryGroupCommand(
                Guid.CreateVersion7().ToString("D"),
                SealedNarrative.EncodedName("Essentials"),
                SealedNarrative.EncodedIndex("Essentials"),
                Base64UrlText.Encode(overCap))));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("Description")).IsTrue();
        await Assert.That(fixture.Groups.AddCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task HandleAsync_WithANameOverItsOwnCap_ThrowsValidationExceptionRatherThanArgumentException()
    {
        // Arrange — the same value one column over, and the assertion is about WHICH exception. The
        // domain refuses an over-cap name too, but as an ArgumentException that becomes a 500: it is
        // written to catch a ceiling mistyped in this codebase, not a body a caller can correct. So the
        // claim here is not "an over-long name is refused" — it would be, either way — but that the
        // refusal reaches the caller as the 400 naming the member they sent.
        Fixture fixture = Fixture.Create();
        byte[] overCap = new byte[NarrativeFieldLimits.NameBytes + 1];
        overCap[0] = CiphertextEnvelope.Version;

        // Act
        ValidationException exception = await ThrowsAsync<ValidationException>(() =>
            fixture.Handler.HandleAsync(new CreateCategoryGroupCommand(
                Guid.CreateVersion7().ToString("D"),
                Base64UrlText.Encode(overCap),
                SealedNarrative.EncodedIndex("Essentials"),
                null)));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("Name")).IsTrue();
        await Assert.That(fixture.Groups.AddCallCount).IsEqualTo(0);
    }

    [Test]
    [Arguments("0199C3D4-0000-7000-8000-0000000000AA")]
    [Arguments("{0199c3d4-0000-7000-8000-0000000000aa}")]
    [Arguments("0199c3d40000700080000000000000aa")]
    [Arguments(" 0199c3d4-0000-7000-8000-0000000000aa ")]
    [Arguments("00000000-0000-0000-0000-000000000000")]
    public async Task HandleAsync_WithANonCanonicalId_ThrowsValidationExceptionKeyedOnTheId(string id)
    {
        // Arrange — five spellings a uuid parser accepts and this route must not, because the client
        // sealed both narrative members against the ONE spelling this API can reproduce. The all-zero
        // uuid rides the same check: it is a legal uuid, so left to the primary key the first such row
        // stores and the second collides under a constraint name that says nothing about the caller that
        // never chose an id at all.
        Fixture fixture = Fixture.Create();

        // Act
        ValidationException exception = await ThrowsAsync<ValidationException>(() =>
            fixture.Handler.HandleAsync(new CreateCategoryGroupCommand(
                id,
                SealedNarrative.EncodedName("Essentials"),
                SealedNarrative.EncodedIndex("Essentials"),
                null)));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("Id")).IsTrue();
        await Assert.That(fixture.Groups.AddCallCount).IsEqualTo(0);
    }

    [Test]
    [Arguments("not base64url at all")]
    [Arguments("")]
    public async Task HandleAsync_WithAMalformedName_ThrowsValidationExceptionKeyedOnTheName(string name)
    {
        // Arrange — the envelope member is opaque to this server, so the only thing it can refuse is the
        // shape: base64url text decoding to a well-framed envelope under the column's cap.
        Fixture fixture = Fixture.Create();

        // Act
        ValidationException exception = await ThrowsAsync<ValidationException>(() =>
            fixture.Handler.HandleAsync(new CreateCategoryGroupCommand(
                Guid.CreateVersion7().ToString("D"),
                name,
                SealedNarrative.EncodedIndex("Essentials"),
                null)));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("Name")).IsTrue();
        await Assert.That(fixture.Groups.AddCallCount).IsEqualTo(0);
    }

    [Test]
    [Arguments("not base64url at all")]
    [Arguments("aGVsbG8")]
    public async Task HandleAsync_WithAMalformedDescription_ThrowsValidationExceptionKeyedOnTheDescription(
        string description)
    {
        // Arrange — two ways a present description can be wrong that have nothing to do with absence: an
        // alphabet the decoder refuses, and perfectly good base64url that decodes to five bytes, which
        // is under the framing's floor. The second is what says the refusal is the ENVELOPE's rules and
        // not merely a decode.
        Fixture fixture = Fixture.Create();

        // Act
        ValidationException exception = await ThrowsAsync<ValidationException>(() =>
            fixture.Handler.HandleAsync(new CreateCategoryGroupCommand(
                Guid.CreateVersion7().ToString("D"),
                SealedNarrative.EncodedName("Essentials"),
                SealedNarrative.EncodedIndex("Essentials"),
                description)));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("Description")).IsTrue();
        await Assert.That(fixture.Groups.AddCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task HandleAsync_WithAnIndexOfTheWrongWidth_ThrowsValidationExceptionKeyedOnTheIndex()
    {
        // Arrange — a blind index is a keyed digest with no framing, so the width is the only shape check
        // this side can make. Thirty-one bytes of perfectly good base64url is the value that proves the
        // check is a width and not merely a decode: nothing here can recompute an index, so a wrong 32
        // bytes keys perfectly, never collides, and stands for a name the row does not hold.
        Fixture fixture = Fixture.Create();
        string tooShort = Base64UrlText.Encode(new byte[IndexedName.BlindIndexLength - 1]);

        // Act
        ValidationException exception = await ThrowsAsync<ValidationException>(() =>
            fixture.Handler.HandleAsync(new CreateCategoryGroupCommand(
                Guid.CreateVersion7().ToString("D"),
                SealedNarrative.EncodedName("Essentials"),
                tooShort,
                null)));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("NameKey")).IsTrue();
        await Assert.That(fixture.Groups.AddCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task HandleAsync_WithABadNameAndABadDescription_ReportsBoth()
    {
        // Arrange — THE CASE THE PAYEES ROUND SHIPPED WITHOUT, AND THE GAP WAS FOUND LATE. The
        // every-member-attempted rule is easy to keep for the members that were always there and easy to
        // break for the one added last: a handler that decoded the description inside an early-return
        // branch, or after the `errors.Count > 0` throw, reports the name alone and satisfies every other
        // case in this file.
        //
        // The two members are produced by one piece of client code and are opaque to this side in the
        // same way, so a caller that got both wrong would otherwise learn about the second only after
        // fixing the first and sending everything again.
        Fixture fixture = Fixture.Create();

        // Act
        ValidationException exception = await ThrowsAsync<ValidationException>(() =>
            fixture.Handler.HandleAsync(new CreateCategoryGroupCommand(
                Guid.CreateVersion7().ToString("D"),
                "not an envelope",
                SealedNarrative.EncodedIndex("Essentials"),
                "not an envelope either")));

        // Assert — the COUNT is what makes this impossible for a fail-fast handler to pass: it would
        // carry exactly one key, satisfy whichever ContainsKey happened to name it, and be caught by
        // nothing else here.
        await Assert.That(exception.Errors.Count).IsEqualTo(2);
        await Assert.That(exception.Errors.ContainsKey("Name")).IsTrue();
        await Assert.That(exception.Errors.ContainsKey("Description")).IsTrue();
        await Assert.That(fixture.Groups.AddCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task HandleAsync_WithEveryOpaqueMemberMalformed_ReportsAllFourAtOnce()
    {
        // Arrange — the same claim over the whole body. Four members, four keys, one refusal. Above, the
        // id is the canonical spelling upper-cased: a legal uuid this API cannot reproduce, which is the
        // one way to get the id check to fail while the value still looks like an identifier.
        Fixture fixture = Fixture.Create();

        // Act
        ValidationException exception = await ThrowsAsync<ValidationException>(() =>
            fixture.Handler.HandleAsync(new CreateCategoryGroupCommand(
                Guid.CreateVersion7().ToString("D").ToUpperInvariant(),
                "not an envelope",
                "not an index",
                "not an envelope either")));

        // Assert
        await Assert.That(exception.Errors.Count).IsEqualTo(4);
        await Assert.That(exception.Errors.ContainsKey("Id")).IsTrue();
        await Assert.That(exception.Errors.ContainsKey("Name")).IsTrue();
        await Assert.That(exception.Errors.ContainsKey("NameKey")).IsTrue();
        await Assert.That(exception.Errors.ContainsKey("Description")).IsTrue();
        await Assert.That(fixture.Groups.AddCallCount).IsEqualTo(0);
    }

    /// <summary>
    /// A well-formed body: the canonical spelling of <paramref name="id" />, both halves of the name
    /// derived from one label the way a client derives them from one text, and a description sealed from
    /// <paramref name="descriptionLabel" /> — absent when the caller names none.
    /// </summary>
    /// <remarks>
    /// Every argument here is a LABEL and is sealed on the way in, so no caller of this helper can send
    /// a malformed member by accident. The cases whose subject IS a malformed member build the command
    /// inline, where the bad value is visible at the call site rather than hidden behind a helper that
    /// decided not to encode it.
    /// </remarks>
    private static CreateCategoryGroupCommand Command(
        Guid id,
        string label,
        string? descriptionLabel = null) =>
        new(
            id.ToString("D"),
            SealedNarrative.EncodedName(label),
            SealedNarrative.EncodedIndex(label),
            descriptionLabel is null ? null : SealedNarrative.EncodedDescription(descriptionLabel));

    private static async Task<TException> ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private sealed class Fixture
    {
        public static readonly DateTimeOffset CreatedAtUtc =
            new(2026, 7, 14, 10, 0, 0, TimeSpan.Zero);

        private Fixture()
        {
        }

        public required Guid BudgetId { get; init; }
        public required InMemoryCategoryGroupRepository Groups { get; init; }
        public required CreateCategoryGroupHandler Handler { get; init; }

        public static Fixture Create()
        {
            var budgetId = Guid.CreateVersion7();
            var clock = new FakeTimeProvider(CreatedAtUtc);
            var groups = new InMemoryCategoryGroupRepository(budgetId, clock);

            return new Fixture
            {
                BudgetId = budgetId,
                Groups = groups,
                Handler = new CreateCategoryGroupHandler(
                    groups, new StubBudgetContext(budgetId), clock),
            };
        }
    }
}
