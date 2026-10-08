using System.Reflection;
using System.Runtime.ExceptionServices;
using Domain.Budgets;
using Domain.Common;
using Domain.Security;
using TestSupport;
using TUnit.Assertions.Enums;

namespace UnitTests;

/// <summary>
/// <see cref="Budget" /> is the only one of the six narrative-bearing entities with NO ordinary write
/// path, and the rule that is missing because of it is written down here rather than left as an
/// absence.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read this before adding a rename to <see cref="Budget" />.</b> Its five siblings each carry a
/// case named <c>…_ClearsTheRotationStamp</c>, pinning that an ordinary narrative write sets
/// <see cref="Budget.RotationId" /> back to <see langword="null" />. This class has no such case, and
/// the reason is not an oversight: <see cref="Budget" /> declares <see cref="Budget.Create" />,
/// <see cref="Budget.CreateDefault" /> and nothing else, so there is no path that rewrites
/// <c>budgets.name</c> after the row exists and therefore nothing that could invalidate a stamp.
/// </para>
/// <para>
/// <b>The hazard appears the day a "name your budget" screen ships, and nothing in this suite is
/// waiting for it.</b> The rule the siblings hold is that a second browser tab still holding the OLD
/// content key can rewrite a row this rotation has already stamped — old-key ciphertext under a current
/// stamp — after which completion promotes the new keys, destroys the old ones, and that value is gone
/// with nothing red anywhere. A <c>Budget.Rename</c> added without the clearing line reintroduces
/// exactly that, on the one column an account may only ever have had set once. Whoever adds that member
/// adds its <c>Rename_ClearsTheRotationStamp</c> case in the same commit, modelled on
/// <c>PayeeTests.Rename_ClearsTheRotationStamp</c>.
/// </para>
/// <para>
/// <b>The inverse rule applies too.</b> The siblings also pin that a NON-narrative write — a position,
/// an assignment — leaves the stamp standing, because clearing it there fails a rotation that genuinely
/// finished and can stop a run converging at all. If <see cref="Budget" /> ever gains a member that
/// writes <see cref="Budget.BaseCurrencyCode" /> and nothing else, that member is on the standing side,
/// not the clearing side.
/// </para>
/// </remarks>
public sealed class BudgetTests
{
    [Test]
    public async Task Create_UsesTheIdItWasGiven()
    {
        // Arrange — an id minted here and threaded in, so the assertion is not "an id came back" but
        // "this one did".
        var id = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        NarrativeField name = SealedNarrative.Name("Household");

        // Act
        Budget budget = Budget.Create(id, userId, name, UtcNow());

        // Assert — the identifier is the associated data the client sealed the name against, so a
        // factory that ignored this parameter and minted its own would satisfy every other case here
        // and produce a row whose name nobody can ever open: no constraint violated, nothing red, and
        // the symptom arriving months later as text that will not decrypt. Guid.CreateVersion7 has
        // left the production file for that reason, and this is the case that would notice it coming
        // back.
        await Assert.That(budget.Id).IsEqualTo(id);
    }

    [Test]
    public async Task Create_WithValidInput_ReturnsBudgetWithTheSealedNameAndNoBaseCurrency()
    {
        // Arrange
        var id = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        NarrativeField name = SealedNarrative.Name("Household");
        DateTime createdAtUtc = new(2026, 7, 25, 9, 30, 0, DateTimeKind.Utc);

        // Act
        Budget budget = Budget.Create(id, userId, name, createdAtUtc);

        // Assert — the name is compared as the bytes it carries rather than by reference, so a factory
        // that stored a different envelope of the same shape is caught. NarrativeField declares no
        // Equals of its own, and a reference comparison here would pass for any instance the factory
        // happened to hold on to.
        await Assert.That(budget.UserId).IsEqualTo(userId);
        await Assert.That(budget.Name).IsNotNull();
        await Assert.That(budget.Name!.Envelope.ToArray()).IsEquivalentTo(name.Envelope.ToArray());
        await Assert.That(budget.BaseCurrencyCode).IsNull();
        await Assert.That(budget.CreatedAtUtc).IsEqualTo(createdAtUtc);
    }

    [Test]
    public async Task Create_WithEmptyId_ThrowsValidationExceptionForId()
    {
        // Act — the empty Guid became reachable the day the identifier stopped being minted here: it
        // is what a caller that threaded a default through hands over.
        ValidationException exception = ThrowsValidationException(() =>
            Budget.Create(Guid.Empty, Guid.CreateVersion7(), SealedNarrative.Name("Household"), UtcNow()));

        // Assert — keyed on the id, not merely thrown. Left to the primary key instead, all-zero is a
        // legal uuid: the first such row stores and the second collides under a constraint name that
        // says nothing about the caller that never chose an id at all.
        await Assert.That(exception.Errors.ContainsKey("Id")).IsTrue();
    }

    [Test]
    public async Task Create_WithEmptyUserId_ThrowsValidationExceptionForUserId()
    {
        // Act
        ValidationException exception = ThrowsValidationException(() =>
            Budget.Create(Guid.CreateVersion7(), Guid.Empty, SealedNarrative.Name("Household"), UtcNow()));

        // Assert — the one rule that survived the column becoming ciphertext, asserted so that the
        // rules which did not cannot take it with them.
        await Assert.That(exception.Errors.ContainsKey("UserId")).IsTrue();
    }

    [Test]
    public async Task Create_WithABlankName_IsAccepted()
    {
        // Arrange — the shortest envelope the format can produce: a version, a nonce and a tag over an
        // empty plaintext. This is exactly what a client sealing a blank name, or a name of nothing but
        // spaces, sends.
        var id = Guid.CreateVersion7();
        NarrativeField blank = SealedNarrative.Name();

        // Act
        Budget budget = Budget.Create(id, Guid.CreateVersion7(), blank, UtcNow());

        // Assert — this server used to refuse a blank name with a ValidationException keyed on Name,
        // and it no longer can. The capability moved to the client, which is the only side that holds a
        // key; it was not quietly dropped. THE SERVER CANNOT MEASURE CHARACTERS IN AN ENVELOPE AND
        // NEVER WILL — it holds ciphertext over text it has never seen, so "is this just whitespace?"
        // is a question about a plaintext that exists nowhere on this side. A future reader who reads
        // the absence as an oversight and restores the check has nothing to check it against; this case
        // is what stops that edit.
        await Assert.That(budget.Name).IsNotNull();
        await Assert.That(budget.Name!.Envelope.Length).IsEqualTo(CiphertextEnvelope.MinimumLength);
    }

    [Test]
    public async Task Create_WithANameFarPastTheOldCharacterLimit_IsAccepted()
    {
        // Arrange — an envelope well past the two hundred characters this factory used to refuse, and
        // well under NarrativeFieldLimits.NameBytes, which is the only ceiling left and is measured in
        // stored bytes by NarrativeField before the value ever reaches Budget.
        var id = Guid.CreateVersion7();
        NarrativeField longName = SealedNarrative.Name(new string('x', 400));

        // Act
        Budget budget = Budget.Create(id, Guid.CreateVersion7(), longName, UtcNow());

        // Assert — the same relocation as the blank case, in the other direction. A length rule here
        // would be a rule about characters, and the server counts none: AES-GCM ciphertext is exactly
        // as long as its plaintext, but the framing and the encoding sit on top of it, so bytes stored
        // and characters typed are different questions and only the first is answerable here.
        await Assert.That(budget.Name).IsNotNull();
        await Assert.That(budget.Name!.Envelope.Length).IsGreaterThan(200);
    }

    [Test]
    public async Task CreateDefault_LeavesTheBudgetWithoutAName()
    {
        // Arrange
        var id = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        DateTime createdAtUtc = UtcNow();

        // Act
        Budget budget = Budget.CreateDefault(id, userId, createdAtUtc);

        // Assert — the budget a user never asked for carries no name at all. The string a client
        // shows for it is presentation, so it lives in the client; storing a literal here would put
        // a display decision in the database and give provisioning a constant that can drift — and it
        // is now unwritable besides, since nothing on this side can seal one.
        await Assert.That(budget.Name).IsNull();
        await Assert.That(budget.Id).IsEqualTo(id);
        await Assert.That(budget.UserId).IsEqualTo(userId);
        await Assert.That(budget.CreatedAtUtc).IsEqualTo(createdAtUtc);
    }

    [Test]
    public async Task CreateDefault_WithEmptyUserId_ThrowsValidationExceptionForUserId()
    {
        // Act — pins the check that has to survive CreateDefault no longer delegating to Create.
        // An ownerless budget is still nonsense, and dropping this guard would push a null user_id
        // down to a foreign key violation.
        ValidationException exception = ThrowsValidationException(() =>
            Budget.CreateDefault(Guid.CreateVersion7(), Guid.Empty, UtcNow()));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("UserId")).IsTrue();
    }

    /// <summary>
    /// <see cref="Budget" />'s reseal member is <see langword="internal" />, and every case below
    /// reaches it by reflection because of that.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Read this before "fixing" any red below by making the member public.</b>
    /// <c>Domain.csproj</c> grants its internals to <c>Infrastructure</c> and to nothing else — the
    /// grant's own comment argues at length why it names one assembly — so this project cannot bind to
    /// <c>Budget.ResealName</c> at compile time. Two edits would make these cases compile, and both are
    /// wrong: widening the grant to <c>UnitTests</c> reddens
    /// <see cref="ProjectReferenceGraphTests" />'s pinned edge set by name, and making the member
    /// public hands the Application ring — where the rotation's command handlers live — a way to rename
    /// a budget outside <c>POST /api/registration</c>, which is the one path that may create one.
    /// </para>
    /// <para>
    /// <b>What reflection costs, stated rather than hidden.</b> There is no compile-time binding here,
    /// so a signature that changes shape fails at run time instead of at build. The lookup below names
    /// the parameter types exactly and refuses with a sentence rather than a
    /// <see cref="NullReferenceException" />, which is the most that can be bought back.
    /// <see cref="ResealName_IsInternalToTheDomain" /> holds the other half: it pins the access level
    /// itself, so a member promoted to public to make these cases easier reddens on its own line rather
    /// than quietly turning the reflection into decoration.
    /// </para>
    /// </remarks>
    private static MethodInfo ResealNameMethod =>
        typeof(Budget).GetMethod(
            "ResealName",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(NarrativeField), typeof(Guid)],
            modifiers: null)
        ?? throw new InvalidOperationException(
            "Budget.ResealName(NarrativeField?, Guid) is not declared as an instance member.");

    /// <summary>
    /// Invokes <c>Budget.ResealName</c>, re-raising whatever it threw rather than the reflection
    /// wrapper around it.
    /// </summary>
    /// <remarks>
    /// Without the unwrap every refusal below arrives as a <see cref="TargetInvocationException" />, so
    /// the cases could not tell a <see cref="ValidationException" /> from any other fault and would
    /// pass against a member that threw for the wrong reason. The stack is preserved rather than
    /// re-thrown bare, so a failure still names the line inside the entity.
    /// </remarks>
    private static void ResealName(Budget budget, NarrativeField? name, Guid rotationId)
    {
        try
        {
            ResealNameMethod.Invoke(budget, [name, rotationId]);
        }
        catch (TargetInvocationException invocation) when (invocation.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(invocation.InnerException).Throw();
        }
    }

    /// <summary>
    /// The reseal member stays <see langword="internal" />, and is not to be widened to get a test to
    /// bind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The access level is the rule, not an implementation detail.</b> Application is where the
    /// rotation's command handlers live and it cannot see Domain's internals, so an
    /// <see langword="internal" /> member is one the ring holding the rotation literally cannot call —
    /// which is what keeps <c>POST /api/registration</c> the only path that writes a budget's name
    /// outside the persistence layer. Public, the member becomes a rename door on the one entity whose
    /// creation is a single consented act, reachable from any handler anybody adds, with nothing red.
    /// </para>
    /// <para>
    /// <b>This case exists because the obvious way to green the rest of this file is to break the
    /// rule.</b> Every other case here goes through reflection precisely because the member cannot be
    /// bound; a reader who tires of that and promotes it to public would turn all of them green at once
    /// and get no signal at all. This one goes red instead, on its own line, saying what was widened.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ResealName_IsInternalToTheDomain()
    {
        // Act — IsAssembly is true for `internal` and false for public, protected and private alike, so
        // one assertion covers every direction the accessibility could move.
        MethodInfo method = ResealNameMethod;

        // Assert
        await Assert.That(method.IsAssembly).IsTrue();
    }

    /// <summary>
    /// A content-key rotation replaces a named budget's name with the envelope sealed under the new key.
    /// </summary>
    /// <remarks>
    /// <b>The second label models the same text under two keys, not new text.</b> A rotation
    /// re-encrypts what the row already holds, so nothing about the budget changes except the bytes —
    /// but <see cref="SealedNarrative" /> derives the envelope from the label it is handed, so "the
    /// same text under a new key" has no other spelling here than a second label. There is no blind
    /// index on this column and no second half to pair with: sealing surrendered uniqueness on
    /// <c>budgets.name</c>, which is a decision rather than a gap, and it is why this member takes a
    /// bare <see cref="NarrativeField" /> where its four siblings take an <see cref="IndexedName" />.
    /// </remarks>
    [Test]
    public async Task ResealName_ReplacesTheName()
    {
        // Arrange
        Budget budget = Budget.Create(
            Guid.CreateVersion7(), Guid.CreateVersion7(), SealedNarrative.Name("Household"), UtcNow());

        // Act
        ResealName(budget, SealedNarrative.Name("Household resealed"), Guid.CreateVersion7());

        // Assert — CollectionOrdering.Matching on the positive assertion: IsEqualTo over two byte[]
        // compares references, and TUnit's failure message steers to IsEquivalentTo, whose default
        // ordering passes on every permutation of an envelope's bytes. Order is the whole of what a
        // ciphertext is. The negative assertion keeps the default, which is the stronger claim.
        await Assert.That(budget.Name).IsNotNull();
        await Assert.That(budget.Name!.Envelope.ToArray()).IsEquivalentTo(
            SealedNarrative.Name("Household resealed").Envelope.ToArray(),
            CollectionOrdering.Matching);
        await Assert.That(budget.Name.Envelope.ToArray())
            .IsNotEquivalentTo(SealedNarrative.Name("Household").Envelope.ToArray());
    }

    /// <summary>
    /// A reseal stamps the row with the id of the rotation that rewrote it.
    /// </summary>
    /// <remarks>
    /// The stamp is the whole reason the column exists, and <see cref="Budget.RotationId" /> is where
    /// that argument lives for all six entities: a re-sealed envelope and an untouched one are
    /// byte-for-byte indistinguishable to a server holding no key, so the completion step — which
    /// destroys the only copies of the old keys — can only know the rewrite finished by being told, in
    /// the same transaction as the ciphertext. A reseal that replaced the envelope and left this column
    /// null passes every other accepting case in this file and makes the account un-completable.
    /// </remarks>
    [Test]
    public async Task ResealName_StampsTheRotationItWasGiven()
    {
        // Arrange — the id minted here and threaded in, so the assertion is not "a stamp appeared" but
        // "this rotation's did". A member that minted its own would leave the row stamped with an id no
        // completion step is looking for.
        Budget budget = Budget.Create(
            Guid.CreateVersion7(), Guid.CreateVersion7(), SealedNarrative.Name("Household"), UtcNow());
        var rotationId = Guid.CreateVersion7();

        // Act
        ResealName(budget, SealedNarrative.Name("Household resealed"), rotationId);

        // Assert
        await Assert.That(budget.RotationId).IsEqualTo(rotationId);
    }

    /// <summary>
    /// A reseal moves the name and the stamp and touches nothing else.
    /// </summary>
    /// <remarks>
    /// <b>The columns held here are the ones a table-wide grant would have reopened.</b>
    /// <c>budgets</c> is granted UPDATE on <c>name</c> alone, and the reason given for the narrowness
    /// is that <c>user_id</c>, <c>base_currency_code</c>, <c>created_at_utc</c> and <c>id</c> must not
    /// travel with it — UserId above all, because a budget is the unit of tenancy and a row that
    /// changed owner carries a whole ledger with it. That is the database's half; this case is the
    /// domain's, and it is the one that would notice a reseal built by copying a factory, which assigns
    /// every column it can name.
    /// </remarks>
    [Test]
    public async Task ResealName_LeavesUserIdBaseCurrencyCodeAndIdentityUnchanged()
    {
        // Arrange — BaseCurrencyCode is null on every budget the product creates today, so what the
        // assertion below holds is "still null" rather than "still what it was". It is here because the
        // column is writable-looking and a reseal that assigned a default to every member it could
        // reach would pass without it, and because the day a base currency can be chosen this line
        // already says the rotation may not choose it.
        var id = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        DateTime createdAtUtc = UtcNow();
        Budget budget = Budget.Create(id, userId, SealedNarrative.Name("Household"), createdAtUtc);

        // Act
        ResealName(budget, SealedNarrative.Name("Household resealed"), Guid.CreateVersion7());

        // Assert
        await Assert.That(budget.Id).IsEqualTo(id);
        await Assert.That(budget.UserId).IsEqualTo(userId);
        await Assert.That(budget.BaseCurrencyCode).IsNull();
        await Assert.That(budget.CreatedAtUtc).IsEqualTo(createdAtUtc);
    }

    /// <summary>
    /// A rotation that supplies a name for the nameless budget is refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the case ASM-004 makes specific to this entity, and it has its own name because of
    /// that.</b> A budget is provisioned NAMELESS by <c>POST /api/registration</c> — the one path that
    /// creates an account — so the empty <c>budgets.name</c> is not an edge case here, it is the state
    /// every account starts in and most stay in. The refused arm is therefore the arm a real rotation
    /// hits, on a real account, the first time the feature ships.
    /// </para>
    /// <para>
    /// <b>It is refused even though filling in an empty name harms no data</b>, because presence is the
    /// only property this side can check at all: an arm admitting a change of presence gives up the
    /// whole of what the rule is made of. What would land in that column is text the server cannot
    /// read, attributed to a person who never wrote it, in a run they authorised as "re-encrypt what I
    /// have" — and on this column it would be a budget silently acquiring a name nobody typed. The
    /// remedy is the sentence the rule already carries: leave it out, then name the budget as a
    /// separate edit.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ResealName_OnANamelessBudget_RefusesAnEnvelope()
    {
        // Arrange — the budget every account begins with.
        Budget budget = Budget.CreateDefault(Guid.CreateVersion7(), Guid.CreateVersion7(), UtcNow());

        // Act
        ValidationException exception = ThrowsValidationException(() =>
            ResealName(budget, SealedNarrative.Name("Household"), Guid.CreateVersion7()));

        // Assert — keyed on the member the request carries. A refusal filed under a word invented by
        // the entity reaches the client verbatim as a 400 naming a member no request has.
        await Assert.That(exception.Errors.ContainsKey(nameof(Budget.Name))).IsTrue();
        await Assert.That(budget.Name).IsNull();
    }

    /// <summary>
    /// A rotation that supplies no name for a budget that has one is refused.
    /// </summary>
    /// <remarks>
    /// The clearing arm, and the one carrying the data loss: the column is nullable, so writing the
    /// absence through produces a legal row that violates no constraint and is byte-identical to the
    /// nameless budget every account is provisioned with. Nothing in the schema can tell that bug from
    /// an operation — which is exactly why the nameless budget exists at all — so the refusal has to be
    /// in the domain.
    /// </remarks>
    [Test]
    public async Task ResealName_WithNoNameOverANamedBudget_IsRefused()
    {
        // Arrange
        Budget budget = Budget.Create(
            Guid.CreateVersion7(), Guid.CreateVersion7(), SealedNarrative.Name("Household"), UtcNow());

        // Act
        ValidationException exception = ThrowsValidationException(() =>
            ResealName(budget, null, Guid.CreateVersion7()));

        // Assert
        await Assert.That(exception.Errors.ContainsKey(nameof(Budget.Name))).IsTrue();
    }

    /// <summary>
    /// The nameless budget is stamped anyway.
    /// </summary>
    /// <remarks>
    /// <b>The control for both refusals, and on this entity it is the ordinary case rather than the
    /// exotic one.</b> Without it, a reseal that threw whenever either side of the name was null would
    /// pass both cases above and would make EVERY account un-rotatable, because every account is
    /// provisioned with a nameless budget and most never name it. The stamp is the half that matters:
    /// there is nothing to re-encrypt on this row, and it must still be accounted for or completion is
    /// permanently one short. A member that returned early on a null name would look correct on every
    /// other case in this file and would stop the feature dead on the first account that tried it.
    /// </remarks>
    [Test]
    public async Task ResealName_OnANamelessBudget_WithNoName_IsAcceptedAndStillStamps()
    {
        // Arrange
        Budget budget = Budget.CreateDefault(Guid.CreateVersion7(), Guid.CreateVersion7(), UtcNow());
        var rotationId = Guid.CreateVersion7();

        // Act
        ResealName(budget, null, rotationId);

        // Assert
        await Assert.That(budget.Name).IsNull();
        await Assert.That(budget.RotationId).IsEqualTo(rotationId);
    }

    /// <summary>
    /// A refused reseal writes nothing at all — not the name, and above all not the stamp.
    /// </summary>
    /// <remarks>
    /// <b>The stamp is the assertion that matters here.</b> A reseal that stamped before it judged the
    /// name leaves a row marked as rotated that was not — and the stamp is the one signal completion
    /// trusts, so the destructive step would promote the new keys over a row still sealed under the old
    /// one. That is the exact loss the column was added to prevent, produced by the member that writes
    /// it. The previous rotation's id is the fixture rather than <see langword="null" /> so that a
    /// member which cleared the stamp on refusal also reddens.
    /// </remarks>
    [Test]
    public async Task ResealName_WithARefusedName_LeavesTheNameAndTheStampAsTheyWere()
    {
        // Arrange — a budget already carried through one rotation, now handed a chunk that drops its
        // name.
        Budget budget = Budget.Create(
            Guid.CreateVersion7(), Guid.CreateVersion7(), SealedNarrative.Name("Household"), UtcNow());
        var firstRotationId = Guid.CreateVersion7();
        ResealName(budget, SealedNarrative.Name("Household resealed"), firstRotationId);

        // Act
        ThrowsValidationException(() => ResealName(budget, null, Guid.CreateVersion7()));

        // Assert — CollectionOrdering.Matching for the reason ResealName_ReplacesTheName states.
        await Assert.That(budget.Name!.Envelope.ToArray()).IsEquivalentTo(
            SealedNarrative.Name("Household resealed").Envelope.ToArray(),
            CollectionOrdering.Matching);
        await Assert.That(budget.RotationId).IsEqualTo(firstRotationId);
    }

    /// <summary>
    /// A reseal quoting the empty rotation id is refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The rule is <see cref="Domain.Users.KeyRotation.Begin" />'s, restated where the stamp is
    /// written rather than invented here.</b> That factory already refuses <see cref="Guid.Empty" /> for
    /// this identifier, keyed on the same member name, and says why: all-zeros is what a client that has
    /// not begun a run sends, and it is the one value two accounts reach independently.
    /// </para>
    /// <para>
    /// <b>What an accepting version produces.</b> A storable uuid in every row's stamp, matching no
    /// <c>key_rotations</c> row — so completion reads a house that is full of a rotation nobody started.
    /// Whether that promotes the wrong generation or refuses forever depends on a lookup nothing in this
    /// entity can see, which is the reason the value is refused at the point it is written rather than
    /// argued about at the point it is read.
    /// </para>
    /// <para>
    /// <b>The nameless budget is the fixture on purpose.</b> It is the row every account is provisioned
    /// with, so this case also says that the empty rotation id is refused on the arm where there is
    /// nothing to re-encrypt — a member that judged the identifier only on the path that writes an
    /// envelope would be green everywhere else and would stamp all-zeros onto most accounts in the
    /// product.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ResealName_WithTheEmptyRotationId_IsRefused()
    {
        // Arrange
        Budget budget = Budget.CreateDefault(Guid.CreateVersion7(), Guid.CreateVersion7(), UtcNow());

        // Act
        ValidationException exception = ThrowsValidationException(() =>
            ResealName(budget, null, Guid.Empty));

        // Assert — keyed on the member the stamp lands in, as KeyRotation.Begin keys its own.
        await Assert.That(exception.Errors.ContainsKey(nameof(Budget.RotationId))).IsTrue();
        await Assert.That(budget.RotationId).IsNull();
    }

    /// <summary>
    /// A chunk re-sent under the rotation id it already carried is accepted, and leaves the name and the
    /// stamp where the first arrival put them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A re-sent chunk is the ordinary case and not an anomaly.</b> A rotation is cut into chunks
    /// because an account can hold more rows than one request should carry, and every chunk of one run
    /// quotes the same rotation id — that is what the id is for. A request that timed out on the wire, a
    /// retry, or a client that never saw the response sends the same rows under the same id again.
    /// </para>
    /// <para>
    /// <b>What this case is here to refuse.</b> A member that additionally rejected a rotation id equal
    /// to the stamp the row already carries reads as sensible idempotence protection and passes every
    /// other case in this file, because they all mint a fresh id. It would fail exactly the long runs
    /// that need chunking most, and it protects against nothing: a reseal is a whole-value write, so the
    /// same chunk applied twice lands the same bytes and the same stamp.
    /// </para>
    /// <para>
    /// <b>The budget is one row, so its own chunk is never re-sent for being too large</b> — and it is
    /// still the row a retry is likeliest to carry twice, because a run that times out part-way is
    /// restarted from a leg the client cannot know it completed. The rule being pinned is the same one
    /// the five siblings pin, and the reason it belongs here as well is that this member is the one
    /// written separately, against a nullable column, by whoever reaches for the shortest arm.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ResealName_RepeatedUnderTheSameRotationId_IsAccepted()
    {
        // Arrange — the chunk that landed, and the id its run is quoting throughout.
        Budget budget = Budget.Create(
            Guid.CreateVersion7(), Guid.CreateVersion7(), SealedNarrative.Name("Household"), UtcNow());
        var rotationId = Guid.CreateVersion7();
        ResealName(budget, SealedNarrative.Name("Household resealed"), rotationId);

        // Act — the same chunk again, under the same id, as a re-sent request carries it.
        ResealName(budget, SealedNarrative.Name("Household resealed"), rotationId);

        // Assert — the name still sealed under the new key, and the stamp still standing, so a member
        // that refused would redden on the call and one that cleared on a repeat would redden here.
        await Assert.That(budget.Name).IsNotNull();
        await Assert.That(budget.Name!.Envelope.ToArray()).IsEquivalentTo(
            SealedNarrative.Name("Household resealed").Envelope.ToArray(),
            CollectionOrdering.Matching);
        await Assert.That(budget.RotationId).IsEqualTo(rotationId);
    }

    private static DateTime UtcNow() =>
        new(2026, 7, 25, 9, 30, 0, DateTimeKind.Utc);

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
