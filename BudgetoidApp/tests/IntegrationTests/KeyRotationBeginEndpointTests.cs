using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Api.Infrastructure;
using Application.Passkeys;
using Domain.Accounts;
using Domain.Categories;
using Domain.CategoryGroups;
using Domain.Payees;
using Domain.Security;
using Domain.Sessions;
using Domain.Transactions;
using Domain.Users;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// The begin leg of a content-key rotation — <c>POST /api/me/key-rotation</c> — driven over real HTTP
/// with a real authenticator against a real account.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>BeginKeyRotationHandler</c> exists, is registered and is reachable by nothing.</b>
/// <c>RepositoryAttributionCensusTests</c> records that absence as the only reason the staging race
/// below cannot be started twice, and says in as many words that the commit making a begin route
/// reachable is what turns the missing constraint-name narrowing in <c>KeyRotationRepository</c> from an
/// absence into a gap. This file is that commit's evidence.
/// </para>
/// <para>
/// <b>Every test here posts the wire shape rather than naming a request record</b>, so nothing in this
/// file depends on a type the endpoint has not been written with yet — the red is a 404 from a route the
/// application does not serve, and turns into an assertion the moment one exists. It also means the
/// <b>five assertion members are pinned by use</b>: <c>credentialId</c>, <c>clientDataJson</c>,
/// <c>authenticatorData</c>, <c>signature</c> and <c>userHandle</c>, spelled exactly as
/// <c>ErasureRequest</c> and <c>RevocationRequest</c> spell them. An endpoint that renamed one would bind
/// it to <see langword="null" />, the gate's own decode would refuse, and the happy path below would
/// answer 401 instead of 200.
/// </para>
/// <para>
/// <b>Every row is counted on <see cref="PostgresTestHost.ConnectionString" /></b> — the container
/// superuser — and never on the application role. <c>key_rotations</c> and <c>key_rotation_seals</c> both
/// carry <c>user_isolation</c>, which is <c>FOR ALL</c>, so a policed connection reports zero rows for a
/// row that is there exactly as it does for one that is not: read on the app role, "the refused request
/// staged nothing" could not fail.
/// </para>
/// <para>
/// <b>Most accounts here are registered through the real ceremony rather than seeded.</b> Registration
/// files <b>eleven</b> factors in one save — one passkey and the ten of a recovery-code card — and this
/// route's whole subject is a value staged per factor, so a seeded account holding one factor would let
/// an implementation that wrapped "the presented factor" pass every case below.
/// </para>
/// </remarks>
public sealed class KeyRotationBeginEndpointTests
{
    private const string BeginPath = "/api/me/key-rotation";
    private const string RegistrationOptionsPath = "/api/passkeys/registration/options";
    private const string RegistrationPath = "/api/passkeys/registration";
    private const string ReauthenticationOptionsPath = "/api/passkeys/reauthentication/options";

    private const string Subject = "google-rotating";

    /// <summary>
    /// The budget the chunk that follows a begin may spend, written out rather than read off
    /// <c>BeginKeyRotationHandler.MaxChunkBytes</c>.
    /// </summary>
    /// <remarks>
    /// A test that read the number from the code under test would assert only that the code agrees with
    /// itself, and would stay green through a change that halved a client's chunk without anybody
    /// deciding to. <c>CredentialRevocationTests</c> makes the same argument about
    /// <c>ConflictKindSpelling</c>.
    /// </remarks>
    private const int PublishedMaxChunkBytes = 32 * 1024;

    /// <summary>The minor unit of the currency every seeded account and transaction is denominated in.</summary>
    private const int UsdMinorUnit = 2;

    /// <summary>
    /// Fixed UTC instant for every seeded row. PostgreSQL <c>timestamptz</c> rejects a non-UTC
    /// <see cref="DateTime" />, so <see cref="DateTimeKind.Utc" /> is load-bearing rather than decoration.
    /// </summary>
    private static readonly DateTime SeedInstant = new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>
    /// The instant the two <c>startedAtUtc</c> cases start their clock at: <c>.1234567</c> seconds, a
    /// value <c>timestamptz</c> cannot hold, so an answer taken from memory rather than from what was
    /// stored differs from the row in its seventh digit.
    /// </summary>
    private static readonly DateTimeOffset SubMicrosecondStartInstant =
        new DateTimeOffset(2026, 6, 12, 13, 14, 15, TimeSpan.Zero).AddTicks(1_234_567);

    /// <summary>
    /// <b>The case that matters more than its status code.</b> A request carrying no proof at all, but a
    /// payload that is correct in every other respect, is refused <b>and stages nothing</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The payload is complete on purpose, and that is the whole design of this test.</b> The seals
    /// name exactly the account's live factors, the manifest is well framed, the epoch is the successor
    /// the account reports — so a route that forgot to call the gate, or called it after staging, would
    /// answer 200 and leave a staged generation behind. A body that was merely empty would be refused by
    /// the gate <em>and</em> by the seal-set comparison behind it, and the test would be green over an
    /// endpoint with no gate in it.
    /// </para>
    /// <para>
    /// <b>Both tables are counted, not one.</b> The parent and its children go in one save, and the two
    /// failures they record are different: a row without seals is a run that cannot be completed, and
    /// seals without a row are key material filed against nothing.
    /// </para>
    /// <para>
    /// The five assertion members are omitted entirely rather than sent empty, which is what a body of
    /// <c>{}</c> does to them — the request record declares none of them <c>required</c> and nothing
    /// registers model validation, so they bind to <see langword="null" /> and the gate's own decode is
    /// the first thing to see it. That is the 401 every other refusal on this route answers, rather than
    /// a framework 400 telling a caller holding a stolen handle that its proof was the thing found
    /// wanting.
    /// </para>
    /// </remarks>
    [Test]
    public async Task BeginKeyRotation_WithoutAFreshAssertion_Answers401AndStagesNothing()
    {
        // Arrange — a whole account, a second passkey beside the one registration wrote, and a payload
        // that would be accepted if only it carried a proof.
        await using PostgresTestHost host = await StartRegisteringHostAsync();
        ApiFactory.SignedInClient signedIn = await host.Factory.RegisterAccountAsync(Subject);
        await RegisterPasskeyAsync(signedIn.Client, SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId));

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        IReadOnlyList<Guid> factorIds = await FactorIdsAsync(admin, signedIn.UserId);
        Generation generation = MintGeneration(factorIds, await FactorGeneration.NextAsync(signedIn.Client));

        // The arrangement, or every zero below is a zero this file produced rather than one the route
        // declined to write. Eleven from registration and one from the passkey above.
        await Assert.That(factorIds.Count).IsEqualTo(12);

        // Act — the payload whole, the proof absent.
        HttpResponseMessage response = await signedIn.Client.PostAsJsonAsync(BeginPath, new
        {
            rotationId = generation.RotationId,
            manifest = generation.Manifest.Text,
            rotationEpoch = generation.Epoch,
            seals = SealsBodyOf(generation),
        });

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

        // And nothing was written on the way to saying no — the half a status assertion cannot see.
        await Assert.That(await CountRotationsAsync(admin, signedIn.UserId)).IsEqualTo(0L);
        await Assert.That(await CountSealsAsync(admin, signedIn.UserId)).IsEqualTo(0L);
    }

    /// <summary>
    /// Two begins of one account in flight at once, and the account ends holding <b>one</b> staged
    /// rotation — neither request answering 500.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the race <c>RepositoryAttributionCensusTests</c> already records as owed, and it is as
    /// much the reason this commit exists as the route is.</b> <c>KeyRotationRepository.StageAsync</c>
    /// reads the account's staged row and then adds one, and reads the account's seals and then adds the
    /// ones it found no row for. At <c>READ COMMITTED</c> two requests can both see nothing and both
    /// <c>Add</c>, and the loser meets <c>23505</c> on <b>either</b> <c>PK_key_rotations</c> or
    /// <c>PK_key_rotation_seals</c> — the parent and its children go in one batch, so which constraint
    /// names the violation depends on statement order inside it and is not a thing a caller can predict.
    /// Nothing stands between that violation and <c>GlobalExceptionHandler</c>, which has no case for a
    /// <c>DbUpdateException</c>, so today it is a 500 titled about an unexpected error rather than a
    /// second begin that simply replaced the first.
    /// </para>
    /// <para>
    /// <b>A narrowing written against <c>PK_key_rotations</c> alone translates half these races and
    /// passes the rest through as a 500</b>, which is why the assertion is over both requests rather than
    /// over one.
    /// </para>
    /// <para>
    /// <b>What survives has to be one whole generation and not a blend</b>, which is the second half of
    /// the assertion and the one a retry written at the wrong ring would fail: a loser re-run after the
    /// winner committed must rewrite <em>every</em> seal, not only the keys its own attempt had not
    /// reached. So the surviving seal set is compared against each generation whole, and the surviving
    /// <c>rotation_id</c> against the same generation's.
    /// </para>
    /// <para>
    /// <b>Two devices, one proof each.</b> A successful gate advances a device's stored signature counter
    /// to the 1 the synthetic authenticator reports, so two concurrent proofs from one device would race
    /// on that counter and one of them would be refused as a regression — a 401 that has nothing to say
    /// about staging. Both assertions are minted before either request is posted, so the only thing the
    /// two requests overlap on is the write.
    /// </para>
    /// <para>
    /// <b>Its limit, stated rather than left for somebody to discover.</b> Nothing here forces the two
    /// requests to interleave: if the first completes before the second reads, the second is an ordinary
    /// replacement and this test is green over a repository that has no narrowing at all. It is a race
    /// this test can lose in the direction of a false pass, never in the direction of a false failure.
    /// </para>
    /// </remarks>
    [Test]
    public async Task BeginKeyRotation_FromTwoConcurrentRequests_ConvergesOnOneStagedRotation()
    {
        // Arrange — one account, two authenticators, two whole begins built to the last byte.
        await using PostgresTestHost host = await StartRegisteringHostAsync();
        ApiFactory.SignedInClient signedIn = await host.Factory.RegisterAccountAsync(Subject);
        SyntheticAuthenticator first = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        SyntheticAuthenticator second = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(signedIn.Client, first);
        await RegisterPasskeyAsync(signedIn.Client, second);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        IReadOnlyList<Guid> factorIds = await FactorIdsAsync(admin, signedIn.UserId);
        int epoch = await FactorGeneration.NextAsync(signedIn.Client);
        Generation left = MintGeneration(factorIds, epoch);
        Generation right = MintGeneration(factorIds, epoch);

        // Both nonces are spent before either request is posted, so the two requests overlap on the
        // write and on nothing else.
        object leftBody = await BeginBodyAsync(signedIn.Client, first, signedIn.UserId, left);
        object rightBody = await BeginBodyAsync(signedIn.Client, second, signedIn.UserId, right);

        // Act
        HttpResponseMessage[] responses = await Task.WhenAll(
            signedIn.Client.PostAsJsonAsync(BeginPath, leftBody),
            signedIn.Client.PostAsJsonAsync(BeginPath, rightBody));

        // Assert — 500 named first, because a bare equality check reports the very answer this test
        // exists to refuse as "not OK".
        await Assert.That(responses[0].StatusCode).IsNotEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(responses[1].StatusCode).IsNotEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(responses[0].StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(responses[1].StatusCode).IsEqualTo(HttpStatusCode.OK);

        // One staged rotation, and its seals are one generation's rather than a blend of two.
        await Assert.That(await CountRotationsAsync(admin, signedIn.UserId)).IsEqualTo(1L);

        StagedRotationRow staged = await StagedRotationAsync(admin, signedIn.UserId);
        Generation survivor = staged.RotationId == left.RotationId ? left : right;
        await Assert.That(staged.RotationId).IsEqualTo(survivor.RotationId);
        await Assert.That(Base64UrlText.Encode(staged.StagedManifest)).IsEqualTo(survivor.Manifest.Text);
        await Assert.That(staged.StagedRotationEpoch).IsEqualTo(survivor.Epoch);
        await AssertSealsAreAsync(admin, signedIn.UserId, survivor);
    }

    /// <summary>
    /// A second begin <b>replaces</b> the first, on both tables — the row rewritten in place and every
    /// seal rewritten one by one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Sequential rather than concurrent, and it is a different claim from the race above.</b>
    /// <c>IKeyRotationRepository.StageAsync</c> promises replacement because begin is the repair path: a
    /// completion that refuses because the account's live factor set moved is answered by a begin
    /// carrying the corrected set, so a 409 here would leave a client holding a staged row it cannot
    /// replace and a rotation it cannot finish, with no route that removes either.
    /// </para>
    /// <para>
    /// <b>The seals are the half a reader will assume the database handles.</b> It does not:
    /// <c>key_rotations</c> is granted no <c>DELETE</c> of any shape and a second begin <em>updates</em>
    /// that row in place, so <c>FK_key_rotation_seals_key_rotations</c> never fires and nothing clears
    /// the previous run's children. They are rewritten per key or they keep the superseded generation's
    /// bytes — a staged set that opens under a content key the client has already thrown away.
    /// </para>
    /// <para>
    /// <b>Every member of the row is compared, not only the identifier.</b> A replacement that copied the
    /// rotation id and left the manifest or the epoch behind stores a generation whose authenticated
    /// name-set disagrees with the keys staged beside it, and the promotion would file it.
    /// </para>
    /// <para>
    /// One device proves both, with the second assertion reporting a strictly higher signature counter.
    /// <c>PasskeySignatureCounter.Accept</c> refuses a repeat, so a second proof at the same count is a
    /// 401 that would read as the route being broken.
    /// </para>
    /// </remarks>
    [Test]
    public async Task BeginKeyRotation_Twice_ReplacesTheStagedGenerationAndItsSeals()
    {
        // Arrange
        await using PostgresTestHost host = await StartRegisteringHostAsync();
        ApiFactory.SignedInClient signedIn = await host.Factory.RegisterAccountAsync(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(signedIn.Client, device);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        IReadOnlyList<Guid> factorIds = await FactorIdsAsync(admin, signedIn.UserId);
        int epoch = await FactorGeneration.NextAsync(signedIn.Client);
        Generation superseded = MintGeneration(factorIds, epoch);
        Generation surviving = MintGeneration(factorIds, epoch + 1);

        // Act — the first begin is arrangement and is asserted as such, or "the second one's bytes
        // survived" is a claim about a table the first call never wrote.
        HttpResponseMessage opened = await BeginAsync(signedIn.Client, device, signedIn.UserId, superseded);
        await Assert.That(opened.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await AssertSealsAreAsync(admin, signedIn.UserId, superseded);

        HttpResponseMessage replaced = await BeginAsync(
            signedIn.Client, device, signedIn.UserId, surviving, signCount: 2);

        // Assert
        await Assert.That(replaced.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await CountRotationsAsync(admin, signedIn.UserId)).IsEqualTo(1L);

        StagedRotationRow staged = await StagedRotationAsync(admin, signedIn.UserId);
        await Assert.That(staged.RotationId).IsEqualTo(surviving.RotationId);
        await Assert.That(Base64UrlText.Encode(staged.StagedManifest)).IsEqualTo(surviving.Manifest.Text);
        await Assert.That(staged.StagedRotationEpoch).IsEqualTo(surviving.Epoch);

        // The children, one by one: the same factors, every one of them carrying the second
        // generation's bytes rather than the first's.
        await Assert.That(await CountSealsAsync(admin, signedIn.UserId)).IsEqualTo((long)factorIds.Count);
        await AssertSealsAreAsync(admin, signedIn.UserId, surviving);
    }

    /// <summary>
    /// The begin answers the <c>startedAtUtc</c> it staged: the stored <c>started_at_utc</c>, spelled as
    /// <c>GET /api/me/key-rotation</c> spells it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The clock is a fixed instant with a non-zero seventh fractional digit, and that digit is the
    /// point.</b> <c>timestamptz</c> keeps microseconds, so the digit never reaches the row (measured: it
    /// stores <c>.123456</c>). A begin that answered the in-memory instant it handed the domain would
    /// answer <c>.1234567</c> while the row and the resume read both say <c>.123456</c> — two statements of one run's start that disagree. Under
    /// the wall clock that mismatch appears on some runs and not others.
    /// </para>
    /// <para>
    /// <b>Compared two ways.</b> Against the stored row as an instant, because that is the value the
    /// answer claims to report. Against the resume read as <b>text</b>, because the two routes owe a
    /// client one spelling, and a client comparing the two answers byte for byte must not see a
    /// difference the server made up.
    /// </para>
    /// <para>
    /// <b>The clock moves a millisecond on every reading during the begin.</b> A fixed clock would let a
    /// begin answer a second reading taken after the save, cut the same way, and still match the row.
    /// </para>
    /// </remarks>
    [Test]
    public async Task BeginKeyRotation_AnswersTheStartedAtItStaged()
    {
        // Arrange
        await using PostgresTestHost host = await StartRegisteringHostAsync();
        FakeTimeProvider clock = new(SubMicrosecondStartInstant);
        await using ApiFactory factory = host.CreateFactory(
            configureServices: services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock)));
        ApiFactory.SignedInClient signedIn = await factory.RegisterAccountAsync(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(signedIn.Client, device);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        IReadOnlyList<Guid> factorIds = await FactorIdsAsync(admin, signedIn.UserId);
        Generation generation = MintGeneration(factorIds, await FactorGeneration.NextAsync(signedIn.Client));

        // From here every reading moves the clock a whole millisecond, so the one reading the row was
        // stamped from is the only one the answer may equal: an answer taken from a second reading after
        // the save, cut the same way, lands at least a millisecond later. Whole milliseconds keep the
        // seventh digit of every reading at 7, so the truncation case keeps its teeth.
        clock.AutoAdvanceAmount = TimeSpan.FromMilliseconds(1);
        DateTime before = clock.GetUtcNow().UtcDateTime;

        // Act
        HttpResponseMessage response = await BeginAsync(signedIn.Client, device, signedIn.UserId, generation);

        // Assert — the status first, so a missing member on a refused request reads as the refusal.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        DateTime after = clock.GetUtcNow().UtcDateTime;

        JsonObject body = await ReadJsonObjectAsync(response);
        await Assert.That(body.ContainsKey("startedAtUtc")).IsTrue();
        string answered = body["startedAtUtc"]!.GetValue<string>();

        // The arrangement held: the stamp came off the substituted clock, inside this request, rather
        // than off the wall clock — or the seventh digit below proves nothing.
        DateTime stored = await StartedAtUtcAsync(admin, signedIn.UserId);
        await Assert.That(stored).IsGreaterThan(before);
        await Assert.That(stored).IsLessThan(after);

        await Assert.That(ParseUtc(answered)).IsEqualTo(stored);
        await Assert.That(answered).IsEqualTo(await ResumedStartedAtTextAsync(signedIn.Client));
    }

    /// <summary>
    /// A second begin answers the <b>second</b> <c>startedAtUtc</c>, the one its replacement stored.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A re-begin overwrites the staged row in place, stamp included</b>, so the value a client
    /// holds after it is the second one. A begin that answered a start read before the replacement, or
    /// one that kept the first run's stamp in the answer, would hand the client an instant the row no
    /// longer carries.
    /// </para>
    /// <para>
    /// The clock moves seven minutes between the two begins, so the two stamps differ by construction and
    /// "the answer changed" is a claim rather than an accident of timing.
    /// </para>
    /// </remarks>
    [Test]
    public async Task BeginKeyRotation_Twice_AnswersTheSecondStartedAt()
    {
        // Arrange
        await using PostgresTestHost host = await StartRegisteringHostAsync();
        FakeTimeProvider clock = new(SubMicrosecondStartInstant);
        await using ApiFactory factory = host.CreateFactory(
            configureServices: services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock)));
        ApiFactory.SignedInClient signedIn = await factory.RegisterAccountAsync(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(signedIn.Client, device);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        IReadOnlyList<Guid> factorIds = await FactorIdsAsync(admin, signedIn.UserId);
        int epoch = await FactorGeneration.NextAsync(signedIn.Client);
        Generation superseded = MintGeneration(factorIds, epoch);
        Generation surviving = MintGeneration(factorIds, epoch + 1);

        HttpResponseMessage opened = await BeginAsync(signedIn.Client, device, signedIn.UserId, superseded);
        await Assert.That(opened.StatusCode).IsEqualTo(HttpStatusCode.OK);
        DateTime firstStored = await StartedAtUtcAsync(admin, signedIn.UserId);

        clock.Advance(TimeSpan.FromMinutes(7));

        // Every reading from here moves the clock a millisecond, so an answer taken from a second reading
        // after the replacement's save cannot equal the stamp the replacement stored.
        clock.AutoAdvanceAmount = TimeSpan.FromMilliseconds(1);

        // Act
        HttpResponseMessage replaced = await BeginAsync(
            signedIn.Client, device, signedIn.UserId, surviving, signCount: 2);

        // Assert
        await Assert.That(replaced.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonObject body = await ReadJsonObjectAsync(replaced);
        await Assert.That(body.ContainsKey("startedAtUtc")).IsTrue();
        string answered = body["startedAtUtc"]!.GetValue<string>();

        DateTime secondStored = await StartedAtUtcAsync(admin, signedIn.UserId);
        await Assert.That(secondStored).IsNotEqualTo(firstStored);

        await Assert.That(ParseUtc(answered)).IsEqualTo(secondStored);
        await Assert.That(answered).IsEqualTo(await ResumedStartedAtTextAsync(signedIn.Client));
    }

    /// <summary>
    /// An account owning a budget this request is not inside is answered <b>500</b>, and that status is
    /// the claim.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It pins <c>RotationScopeException</c> as deliberately unmapped.</b> That type's own remarks
    /// reject 404, 400 and 409 with reasons — the account exists, the request is not correctable by
    /// editing it, and there is no conflicting state a client can resolve — so the answer is a fault, and
    /// this test is what stops somebody "fixing" it into a status a client would act on. A rotation that
    /// cannot be completed is better not begun: at this point the client has re-encrypted nothing, while
    /// at completion it has rewritten the account and the promotion it is asking for is the step that
    /// destroys the only wrapped copies of the keys the rest of it is sealed under.
    /// </para>
    /// <para>
    /// <b>The proof is genuine and the payload is whole</b>, so the 500 is positive evidence that the
    /// gate <em>passed</em> and the scope refusal then fired. A 401 here would be this test measuring the
    /// gate instead of the refusal it is named for, and it would stay green over a handler with no scope
    /// rule in it at all.
    /// </para>
    /// <para>
    /// <b>The second budget is seeded rather than created through a route</b>, because no route creates
    /// one: registration writes exactly one budget and the product offers no second. The message is
    /// deliberately not pinned — it names counts and never budget ids, and the Development branch of
    /// <c>GlobalExceptionHandler</c> echoes it into the body, so asserting its wording here would make
    /// that constraint read as a phrasing test. <c>RotationCompletenessTests</c> says the same beside its
    /// own.
    /// </para>
    /// </remarks>
    [Test]
    public async Task BeginKeyRotation_ForAnAccountOwningASecondBudget_Answers500()
    {
        // Arrange — one account, two budgets, and a begin that is correct in every other respect.
        await using PostgresTestHost host = await StartRegisteringHostAsync();
        ApiFactory.SignedInClient signedIn = await host.Factory.RegisterAccountAsync(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(signedIn.Client, device);
        Guid secondBudgetId = await RepositoryTestHost.SeedAdditionalBudgetOnAsync(
            host.ConnectionString, signedIn.UserId, "Cabin");

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        IReadOnlyList<Guid> factorIds = await FactorIdsAsync(admin, signedIn.UserId);
        Generation generation = MintGeneration(factorIds, await FactorGeneration.NextAsync(signedIn.Client));

        // The arrangement, or this case is an ordinary happy path answering 200 for want of a second
        // budget the seeder quietly failed to add.
        await Assert.That(secondBudgetId).IsNotEqualTo(signedIn.BudgetId);
        await Assert.That(await CountOwnedBudgetsAsync(admin, signedIn.UserId)).IsEqualTo(2L);

        // Act
        HttpResponseMessage response = await BeginAsync(signedIn.Client, device, signedIn.UserId, generation);

        // Assert — the fault, and never a status a client could act on.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.NotFound);
        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.Conflict);

        // And the refusal reached the database for nothing.
        await Assert.That(await CountRotationsAsync(admin, signedIn.UserId)).IsEqualTo(0L);
        await Assert.That(await CountSealsAsync(admin, signedIn.UserId)).IsEqualTo(0L);
    }

    /// <summary>
    /// A session opened by the account's federated credential is refused this route with <b>403</b>, and
    /// it is the locked-session gate's 403 rather than the CSRF control's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The route declares no <c>AllowsLockedSessionAttribute</c>, which is the whole of what this
    /// test asks about.</b> <c>FullSessionRequirement</c> rides the fallback policy and the opted-out set
    /// is exactly <c>POST /api/me/session/revocation</c>; a rotation reseals every narrative column in
    /// the account, so a provider sign-in reaching it would be a caller who cannot hold the account's
    /// keys asking for the generation they are sealed under to move.
    /// </para>
    /// <para>
    /// <b>Both refusals on this path are 403</b>, which is the trap <c>LockedSessionTests</c> is written
    /// around: <see cref="FirstPartyRequestMiddleware" /> answers 403 to a request without the client
    /// header, before anything looks at a cookie. So the title is compared as well as the status — a 403
    /// carrying <see cref="FirstPartyRequestMiddleware.Title" /> would leave this test green against an
    /// application with no gate in it.
    /// </para>
    /// <para>
    /// <b>THE FULL-SESSION ARM IS NOT A CONTROL, IT IS WHAT MAKES THIS TEST ABOUT A ROUTE AT ALL, and it
    /// was added because the locked half passed before the route existed.</b> <c>AuthorizationMiddleware</c>
    /// applies the fallback policy to a request that matched <b>no endpoint</b> as readily as to one that
    /// matched an unmarked one, so <c>POST</c> to a path this application does not serve is answered 403
    /// to a locked session and 404 to everybody else — measured, on this file's first run. A test asserting
    /// the 403 alone is therefore green against an application in which this route was never mapped, which
    /// is the whole thing it was written to hold. The full session's 401 is what separates the two: no
    /// endpoint answers 404, an endpoint the policy refused answers 403, and only an endpoint that ran
    /// answers the gate's 401.
    /// </para>
    /// <para>
    /// <b>A 401 rather than a 200 as that arm</b>, deliberately: it needs no registered factor, no
    /// ceremony and no seal set, and it already establishes everything this case needs — the route is
    /// mapped, a full session reaches it, and the body got as far as the re-authentication gate. The 200
    /// is the happy path's to own, and paying for a second registration ceremony here would buy nothing
    /// this assertion does not already say.
    /// </para>
    /// <para>
    /// The bodies are well shaped and mean nothing. Authorization runs on the endpoint before the delegate
    /// is entered, so a locked session never reaches the gate, the scope refusal or the seal comparison —
    /// and the epoch is written out rather than read, because <c>GET /api/me/account-keys</c> rides the
    /// same fallback policy and would answer the locked client 403 too.
    /// </para>
    /// </remarks>
    [Test]
    public async Task BeginKeyRotation_FromALockedSession_Answers403()
    {
        // Arrange — one host, two accounts, two sessions: one opened by a federated credential, which is
        // the only kind that derives SessionKind.Locked, and one opened by a passkey.
        await using PostgresTestHost host = new(usesApplicationAuthentication: true);
        await host.StartAsync();
        ApiFactory.SignedInClient locked = await host.Factory.CreateSignedInClientAsync(
            Subject, kind: SessionKind.Locked);
        ApiFactory.SignedInClient full = await host.Factory.CreateSignedInClientAsync(
            "google-rotating-bystander", kind: SessionKind.Full);

        // Act
        HttpResponseMessage refused = await locked.Client.PostAsJsonAsync(BeginPath, ProoflessBody());
        HttpResponseMessage admitted = await full.Client.PostAsJsonAsync(BeginPath, ProoflessBody());

        // Assert — the admitted arm first, so a route that is not mapped at all reads as the 404 it is
        // rather than as a locked session being refused.
        await Assert.That(admitted.StatusCode).IsNotEqualTo(HttpStatusCode.NotFound);
        await Assert.That(admitted.StatusCode).IsNotEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(admitted.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

        // The refusal, and that it is this gate's refusal rather than the CSRF control's.
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(await TitleOfAsync(refused)).IsNotEqualTo(FirstPartyRequestMiddleware.Title);
    }

    /// <summary>
    /// A begin whose payload is well shaped and whose proof is absent — the body both arms of the
    /// locked-session case post, so the only thing that differs between them is the session.
    /// </summary>
    private static object ProoflessBody()
    {
        WrappedKeyFixture seal = WrappedKeyFixture.Mint();

        return new
        {
            rotationId = Guid.CreateVersion7(),
            manifest = ManifestFixture.Mint().Text,
            rotationEpoch = 1,
            seals = new object[]
            {
                new { factorId = seal.FactorId, encapsulatedAccountKeys = seal.EncapsulatedAccountKeys },
            },
        };
    }

    /// <summary>
    /// The happy path: <b>200</b>, the six counts the client drives its progress by, and one staged seal
    /// for every factor the account holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The account holds twelve factors, and the number is the point.</b> Registration files eleven —
    /// one passkey and the ten of a recovery-code card — and a second passkey is registered beside them,
    /// so the seal set is genuinely larger than one and larger than the set of <em>passkey</em> factors.
    /// An implementation that staged a value for "the factor that proved the request", or for the
    /// account's passkeys alone, would leave ten cards holding a copy of a content key that opens
    /// nothing — still enrolled, still in a wallet, and reported by nothing. That is the orphaning this
    /// route's gate exists to prevent, and only a many-factor account can see it.
    /// </para>
    /// <para>
    /// <b>The six counts are seeded to six different numbers.</b> Five ints in a row is the call shape
    /// where a transposed pair compiles, stores and is discovered later as a progress bar that finishes
    /// early, so no two of them may be equal: one account, two payees, three groups, four categories,
    /// five described transactions, and zero named budgets — registration's budget carries no name, which
    /// is what makes <c>budgets</c> a real presence test here rather than a constant.
    /// </para>
    /// <para>
    /// <b>Two note-less transactions are seeded and must not be counted.</b> The population is the
    /// completeness gate's population — rows that <em>carry</em> a narrative value — so a count that
    /// included them would hand the client a denominator it can never reach, and the rotation would never
    /// report itself finished.
    /// </para>
    /// <para>
    /// <b>200 and never 201, asserted both ways.</b> A begin creates no resource this API exposes at an
    /// address: there is no rotation to <c>GET</c>, no <c>Location</c> to carry, and the identifier was
    /// minted by the caller — so a 201 would promise a resource that does not exist.
    /// </para>
    /// <para>
    /// <b><see cref="PublishedMaxChunkBytes" /> is written out rather than read off the handler</b>, for
    /// the reason stated where it is declared: a client cannot size its first chunk without it, and a
    /// test reading the constant from the code under test asserts only that the code agrees with itself.
    /// </para>
    /// </remarks>
    [Test]
    public async Task BeginKeyRotation_WithAFreshAssertion_StagesEveryFactorAndAnswersTheInventory()
    {
        // Arrange — a registered account, a second passkey, and a budget furnished so that no two of the
        // six counts are the same number.
        await using PostgresTestHost host = await StartRegisteringHostAsync();
        ApiFactory.SignedInClient signedIn = await host.Factory.RegisterAccountAsync(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(signedIn.Client, device);
        await FurnishBudgetAsync(host, signedIn.BudgetId);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        IReadOnlyList<Guid> factorIds = await FactorIdsAsync(admin, signedIn.UserId);
        Generation generation = MintGeneration(factorIds, await FactorGeneration.NextAsync(signedIn.Client));

        // The arrangement: eleven factors from registration and one from the passkey above, and nothing
        // staged before the act.
        await Assert.That(factorIds.Count).IsEqualTo(12);
        await Assert.That(await CountRotationsAsync(admin, signedIn.UserId)).IsEqualTo(0L);

        // Act
        HttpResponseMessage response = await BeginAsync(signedIn.Client, device, signedIn.UserId, generation);

        // Assert — the status first, so a body missing because the request was refused reads as the
        // refusal it is rather than as an inventory nobody would recognise as a 401.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.Created);

        JsonObject body = await ReadJsonObjectAsync(response);
        await Assert.That(body["maxChunkBytes"]!.GetValue<int>()).IsEqualTo(PublishedMaxChunkBytes);

        JsonObject inventory = body["inventory"]!.AsObject();
        await Assert.That(inventory["accounts"]!.GetValue<int>()).IsEqualTo(1);
        await Assert.That(inventory["payees"]!.GetValue<int>()).IsEqualTo(2);
        await Assert.That(inventory["categoryGroups"]!.GetValue<int>()).IsEqualTo(3);
        await Assert.That(inventory["categories"]!.GetValue<int>()).IsEqualTo(4);
        await Assert.That(inventory["transactions"]!.GetValue<int>()).IsEqualTo(5);
        await Assert.That(inventory["budgets"]!.GetValue<int>()).IsEqualTo(0);

        // The staged row is what was sent, member by member.
        StagedRotationRow staged = await StagedRotationAsync(admin, signedIn.UserId);
        await Assert.That(staged.RotationId).IsEqualTo(generation.RotationId);
        await Assert.That(Base64UrlText.Encode(staged.StagedManifest)).IsEqualTo(generation.Manifest.Text);
        await Assert.That(staged.StagedRotationEpoch).IsEqualTo(generation.Epoch);

        // One seal per live factor, and the bytes filed against each are that factor's own — not one
        // value copied twelve times, and not eleven of twelve.
        await Assert.That(await CountSealsAsync(admin, signedIn.UserId)).IsEqualTo((long)factorIds.Count);
        await AssertSealsAreAsync(admin, signedIn.UserId, generation);
    }

    // ==================================================================================
    // THE THREE BELOW ARE ONE FAMILY: A PROVEN CALLER, A PAYLOAD THIS ROUTE MUST REFUSE, AND A RULE
    // WITH NO HOLDER BELOW THE ENDPOINT.
    //
    // Every other refusal in this file is held somewhere a test could reach without a route — the
    // gate by PasskeyReauthentication, the scope refusal by the handler, the widths and versions of a
    // seal by KeyRotationSeal.For. These three are not. Each asserts the status and that nothing was
    // staged, and deliberately asserts no message: what is being held is that the request is REFUSED
    // rather than stored, and the sentence is the endpoint's to word.
    //
    // 500 IS NAMED SEPARATELY IN ALL THREE, because on the two manifest cases it is the specific wrong
    // answer. A strict decoder reached without a Try shape in front of it throws FormatException out
    // of the endpoint, which GlobalExceptionHandler answers as an unexpected error — a fault for a
    // request that is merely malformed, and a bare equality check would report it as "not 400".
    // ==================================================================================

    /// <summary>
    /// A manifest spelled in <b>padded standard base64</b> is refused, not decoded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>BeginKeyRotationCommand.StagedManifest</c> is <c>ReadOnlyMemory&lt;byte&gt;</c> where all
    /// three sibling commands carry text, and that one difference is what this test exists for.</b>
    /// <c>RevokePasskeyCommand.Manifest</c> and <c>GenerateRecoveryCodesCommand.Manifest</c> are
    /// <see cref="string" />, so their handlers run <c>FactorManifestEnvelope.TryDecode</c> and the
    /// alphabet is judged inside the Application ring. Here the value arrives as bytes, so the decode is
    /// the <b>endpoint's</b> — and there is nothing underneath it: <c>KeyRotation.Begin</c> judges only
    /// emptiness and <c>FactorManifest.MaximumBytes</c>, and the column's own rule is
    /// <c>length(staged_manifest) between 1 and 4096</c>. A lenient decoder here is refused by no layer
    /// in the product.
    /// </para>
    /// <para>
    /// <b>The two alphabets are asserted to differ for this fixture, in the Arrange.</b> Unpadded
    /// base64url and padded standard base64 spell a payload identically when its length is a multiple of
    /// three and none of its six-bit groups lands on 62 or 63 — <c>SealedNarrative</c> writes that out at
    /// length, and a manifest's width <em>is</em> a multiple of three. Without the guard this case would
    /// be green under either decoder for any fixture that happened to be blind, and the greenness would
    /// vary run to run because the bytes are random.
    /// </para>
    /// </remarks>
    [Test]
    public async Task BeginKeyRotation_WithAManifestInPaddedStandardBase64_Answers400AndStagesNothing()
    {
        // Arrange
        await using PostgresTestHost host = await StartRegisteringHostAsync();
        ApiFactory.SignedInClient signedIn = await host.Factory.RegisterAccountAsync(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(signedIn.Client, device);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        IReadOnlyList<Guid> factorIds = await FactorIdsAsync(admin, signedIn.UserId);
        Generation generation = MintGeneration(factorIds, await FactorGeneration.NextAsync(signedIn.Client));
        string padded = Convert.ToBase64String(generation.Manifest.Manifest);

        // The two spellings really are different for this fixture, or the case asserts nothing at all.
        await Assert.That(padded).IsNotEqualTo(generation.Manifest.Text);

        // Act — everything else about the request is correct, including the proof.
        HttpResponseMessage response = await BeginAsync(
            signedIn.Client, device, signedIn.UserId, generation, manifestText: padded);

        // Assert — refused rather than faulted, and refused rather than stored.
        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await CountRotationsAsync(admin, signedIn.UserId)).IsEqualTo(0L);
        await Assert.That(await CountSealsAsync(admin, signedIn.UserId)).IsEqualTo(0L);
    }

    /// <summary>
    /// A manifest one byte <b>below the AEAD framing's floor</b> is refused — the bound nothing beneath
    /// this endpoint holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The width is chosen so that only one rule in the product can answer.</b> It is not empty, so
    /// <c>KeyRotation.Begin</c>'s emptiness refusal cannot fire; it is far under
    /// <c>FactorManifest.MaximumBytes</c>, so neither that refusal nor the column's
    /// <c>CHECK</c> can; and it carries <c>CiphertextEnvelope.Version</c>, so a version test cannot. Both
    /// of those are asserted in the Arrange rather than assumed. What is left is
    /// <c>CiphertextEnvelope.MinimumLength</c> — a version, a nonce and a tag with no ciphertext between
    /// them — which lives in <c>FactorManifestEnvelope</c> and nowhere else on this path.
    /// </para>
    /// <para>
    /// <b>The database's own lower bound is vacuous beside it, deliberately.</b>
    /// <c>length(staged_manifest) between 1 and 4096</c> is a fact about a <c>bytea</c> not being empty;
    /// it is not widened or narrowed to match, and it is not what refuses this. A twenty-eight-byte
    /// manifest stores perfectly well and promotes perfectly well — which is the whole reason the floor
    /// has to be applied before anything is written.
    /// </para>
    /// </remarks>
    [Test]
    public async Task BeginKeyRotation_WithAManifestBelowTheFramingFloor_Answers400AndStagesNothing()
    {
        // Arrange
        await using PostgresTestHost host = await StartRegisteringHostAsync();
        ApiFactory.SignedInClient signedIn = await host.Factory.RegisterAccountAsync(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(signedIn.Client, device);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        IReadOnlyList<Guid> factorIds = await FactorIdsAsync(admin, signedIn.UserId);
        Generation generation = MintGeneration(factorIds, await FactorGeneration.NextAsync(signedIn.Client));
        ManifestFixture tooNarrow = ManifestFixture.Of(CiphertextEnvelope.MinimumLength - 1);

        // The value clears every OTHER rule in the product, so a 400 can only be the framing's floor.
        await Assert.That(tooNarrow.Manifest.Length).IsGreaterThan(0);
        await Assert.That(tooNarrow.Manifest.Length).IsLessThan(FactorManifest.MaximumBytes);
        await Assert.That(tooNarrow.Manifest[0]).IsEqualTo(CiphertextEnvelope.Version);

        // Act
        HttpResponseMessage response = await BeginAsync(
            signedIn.Client, device, signedIn.UserId, generation, manifestText: tooNarrow.Text);

        // Assert
        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await CountRotationsAsync(admin, signedIn.UserId)).IsEqualTo(0L);
        await Assert.That(await CountSealsAsync(admin, signedIn.UserId)).IsEqualTo(0L);
    }

    /// <summary>
    /// Thirteen seals naming twelve factors are refused — the repeat survives to the handler rather than
    /// being absorbed on the way.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the case that makes <c>Seals</c> a list rather than a dictionary mean something.</b>
    /// <c>BeginKeyRotationCommand</c> argues at length that a dictionary keyed on the factor makes a
    /// duplicate <em>unconstructible</em>, which reads like a guarantee and is the opposite of one on the
    /// wire: JSON deserialisation into a dictionary drops a repeat silently, last wins — measured, and
    /// the count comes back one short with nothing anywhere saying so. Kept as a list, the duplicate
    /// survives into the handler, which counts distinct factor ids against the number of seals and
    /// refuses first.
    /// </para>
    /// <para>
    /// <b>What it actually catches is not the wire type — that is refused by binding — but the
    /// de-duplication a reader adds while mapping.</b> <c>.DistinctBy(seal =&gt; seal.FactorId)</c> or a
    /// <c>.ToDictionary(…)</c> between the request record and <c>RotationSeal</c> reads like tidying and
    /// does exactly what the dictionary would: twelve distinct factors, set equality satisfied, a 200,
    /// and an account one seal short of what its client believed it sent. No other test in this file
    /// sends a duplicate, so without this one that mapping passes everything.
    /// </para>
    /// <para>
    /// <b>The repeat carries <em>different bytes</em> from the entry it repeats</b>, so a de-duplication
    /// that kept either one is equally silent and equally wrong — there is no reading of the request
    /// under which one of the two is the caller's real intention.
    /// </para>
    /// <para>
    /// It is the distinct-count refusal that answers rather than the set comparison, and the two are
    /// separate rules for this reason: a <c>HashSet</c> absorbs the repeat, so thirteen seals naming
    /// twelve factors satisfy set equality against twelve factors perfectly.
    /// </para>
    /// </remarks>
    [Test]
    public async Task BeginKeyRotation_WithARepeatedFactorInTheSeals_Answers400AndStagesNothing()
    {
        // Arrange
        await using PostgresTestHost host = await StartRegisteringHostAsync();
        ApiFactory.SignedInClient signedIn = await host.Factory.RegisterAccountAsync(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(signedIn.Client, device);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        IReadOnlyList<Guid> factorIds = await FactorIdsAsync(admin, signedIn.UserId);
        Generation generation = MintGeneration(factorIds, await FactorGeneration.NextAsync(signedIn.Client));

        // One whole, correct seal set, and a fourteenth entry naming a factor it already names — with
        // bytes of its own, so neither of the two is the one a de-duplication could be said to keep.
        Guid repeated = factorIds[0];
        object[] withARepeat =
        [
            .. SealsBodyOf(generation),
            new
            {
                factorId = repeated,
                encapsulatedAccountKeys = WrappedKeyFixture.MintFor(repeated).EncapsulatedAccountKeys,
            },
        ];

        // The arrangement, or this is the happy path wearing a different name: one more seal than the
        // account has factors, naming one fewer distinct factor than it has seals.
        await Assert.That(withARepeat.Length).IsEqualTo(factorIds.Count + 1);
        await Assert.That(factorIds.Count).IsEqualTo(12);

        // Act
        HttpResponseMessage response = await signedIn.Client.PostAsJsonAsync(
            BeginPath,
            await BeginBodyAsync(signedIn.Client, device, signedIn.UserId, generation, seals: withARepeat));

        // Assert
        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await CountRotationsAsync(admin, signedIn.UserId)).IsEqualTo(0L);
        await Assert.That(await CountSealsAsync(admin, signedIn.UserId)).IsEqualTo(0L);
    }

    /// <summary>
    /// One whole begin as a client assembles it: which run, which generation's manifest and epoch, and
    /// one copy of the new account keys per factor.
    /// </summary>
    /// <remarks>
    /// The seals are keyed on the factor so a read-back can be compared per factor rather than as a bag —
    /// a value copied to every row satisfies a count and a set of bytes, and satisfies this comparison
    /// only if it really was staged against the factor it names.
    /// </remarks>
    private sealed record Generation(
        Guid RotationId,
        ManifestFixture Manifest,
        int Epoch,
        IReadOnlyDictionary<Guid, WrappedKeyFixture> Seals);

    /// <summary>The staged row as the database holds it.</summary>
    private sealed record StagedRotationRow(Guid RotationId, byte[] StagedManifest, int StagedRotationEpoch);

    /// <summary>
    /// A fresh run over <paramref name="factorIds" />: a client-minted identifier, a manifest of a width
    /// a real one naming eleven factors would have, and a distinct encapsulated value per factor.
    /// </summary>
    /// <remarks>
    /// <see cref="WrappedKeyFixture.MintFor" /> rather than <see cref="WrappedKeyFixture.Mint" />, so the
    /// factor the fixture names is the factor the account really holds — the widths and the version bytes
    /// still come off <c>WrappedAccountKeys</c>, which is the whole reason the fixture is used here rather
    /// than 158 bytes assembled by hand.
    /// </remarks>
    private static Generation MintGeneration(IReadOnlyList<Guid> factorIds, int epoch) =>
        new(
            Guid.CreateVersion7(),
            ManifestFixture.Mint(),
            epoch,
            factorIds.ToDictionary(factorId => factorId, WrappedKeyFixture.MintFor));

    /// <summary>The seals of <paramref name="generation" /> as the wire carries them.</summary>
    private static object[] SealsBodyOf(Generation generation) =>
    [
        .. generation.Seals.Select(seal => (object)new
        {
            factorId = seal.Key,
            encapsulatedAccountKeys = seal.Value.EncapsulatedAccountKeys,
        }),
    ];

    /// <summary>
    /// That the account's staged seals are exactly <paramref name="generation" />'s: the same factors,
    /// and each one carrying the value that generation encapsulated <em>to that factor</em>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Per factor rather than as one comparison of two maps</b>, so that a failure names the factor
    /// and prints two base64 strings a reader can line up against the request. A single equality over a
    /// dictionary reports "these two objects differ" and leaves the reader to find which of twelve rows
    /// it was — and a sequence comparison over byte arrays is the one shape whose default ignores order
    /// as well.
    /// </para>
    /// <para>
    /// <b>Presence is asserted separately from value</b>, because the two failures read very differently:
    /// a factor with no seal at all is the orphaning this route exists to prevent, and a factor carrying
    /// another generation's bytes is a replacement that stopped half-way.
    /// </para>
    /// <para>
    /// Compared as text rather than as bytes for the reason above, and in the alphabet the wire carried
    /// the values in.
    /// </para>
    /// </remarks>
    private static async Task AssertSealsAreAsync(
        NpgsqlConnection admin,
        Guid userId,
        Generation generation)
    {
        SortedDictionary<Guid, string> staged = await SealTextByFactorAsync(admin, userId);

        await Assert.That(staged.Count).IsEqualTo(generation.Seals.Count);

        foreach ((Guid factorId, WrappedKeyFixture seal) in generation.Seals.OrderBy(entry => entry.Key))
        {
            await Assert.That(staged.ContainsKey(factorId)).IsTrue();
            await Assert.That(staged.TryGetValue(factorId, out string? stored) ? stored : null)
                .IsEqualTo(seal.EncapsulatedAccountKeys);
        }
    }

    /// <summary>
    /// Runs the re-authentication options leg, has <paramref name="device" /> answer the nonce it issued,
    /// and posts the begin.
    /// </summary>
    /// <param name="manifestText">
    /// The spelling of the manifest to send, for the cases whose subject is the manifest. Omitted, it is
    /// <paramref name="generation" />'s own — which is the base64url
    /// <see cref="ManifestFixture.Text" /> every other case wants, and the one spelling this API carries
    /// binary in.
    /// </param>
    private static async Task<HttpResponseMessage> BeginAsync(
        HttpClient client,
        SyntheticAuthenticator device,
        Guid userId,
        Generation generation,
        uint signCount = 1,
        string? manifestText = null) =>
        await client.PostAsJsonAsync(
            BeginPath,
            await BeginBodyAsync(client, device, userId, generation, signCount, manifestText));

    /// <summary>
    /// The same request, built but not sent, for the case that has to have two of them in hand before
    /// either is posted.
    /// </summary>
    /// <remarks>
    /// <b>The five assertion members are <c>ErasureRequest</c>'s and <c>RevocationRequest</c>'s, members
    /// and all</b>, so a caller comparing the three gates learns nothing from the difference between
    /// them. <c>rotationId</c> and <c>factorId</c> travel as the hyphenated 36-character form, and every
    /// binary member as unpadded base64url — which is how binary crosses JSON everywhere in this API.
    /// </remarks>
    /// <param name="manifestText">
    /// The spelling of the manifest to send, or <see langword="null" /> for
    /// <paramref name="generation" />'s own.
    /// </param>
    /// <param name="seals">
    /// The seal array to send, or <see langword="null" /> for <paramref name="generation" />'s own. The
    /// one case that overrides it sends a <b>repeat</b>, which is a shape <see cref="Generation" /> is
    /// keyed not to be able to hold — a dictionary cannot carry one, which is the same argument
    /// <c>BeginKeyRotationCommand</c> makes about the wire.
    /// </param>
    private static async Task<object> BeginBodyAsync(
        HttpClient client,
        SyntheticAuthenticator device,
        Guid userId,
        Generation generation,
        uint signCount = 1,
        string? manifestText = null,
        object[]? seals = null)
    {
        byte[] challenge = await BeginCeremonyAsync(client, ReauthenticationOptionsPath);
        AssertionResult assertion = device.Authenticate(
            challenge,
            ApiFactory.PasskeyOrigin,
            PasskeyEncoding.ToUserHandle(userId),
            signCount);

        return new
        {
            rotationId = generation.RotationId,
            manifest = manifestText ?? generation.Manifest.Text,
            rotationEpoch = generation.Epoch,
            seals = seals ?? SealsBodyOf(generation),
            credentialId = assertion.CredentialIdBase64Url,
            clientDataJson = assertion.ClientDataJsonBase64Url,
            authenticatorData = assertion.AuthenticatorDataBase64Url,
            signature = assertion.SignatureBase64Url,
            userHandle = assertion.UserHandleBase64Url,
        };
    }

    /// <summary>
    /// Runs both authenticated legs of a passkey registration, so the account really holds a passkey a
    /// signature answers to — and a twelfth factor beside the eleven registration wrote.
    /// </summary>
    /// <remarks>
    /// The generation is read off the running API rather than written out, for the reason
    /// <c>FactorGeneration</c> gives: a file that registers a second passkey has to send a different
    /// number from the first, and an epoch that is not exactly one greater than the stored one is a 400
    /// naming <c>rotationEpoch</c> — which reads as the ceremony being broken.
    /// </remarks>
    private static async Task RegisterPasskeyAsync(HttpClient client, SyntheticAuthenticator device)
    {
        byte[] challenge = await BeginCeremonyAsync(client, RegistrationOptionsPath);
        AttestationResult attestation = device.Register(
            challenge,
            ApiFactory.PasskeyOrigin,
            signCount: 0,
            prfEnabled: true);
        WrappedKeyFixture keys = WrappedKeyFixture.Mint();
        int rotationEpoch = await FactorGeneration.NextAsync(client);

        HttpResponseMessage response = await client.PostAsJsonAsync(RegistrationPath, new
        {
            clientDataJson = attestation.ClientDataJsonBase64Url,
            attestationObject = attestation.AttestationObjectBase64Url,
            clientExtensionResults = new { prf = new { enabled = true } },
            factorId = keys.FactorId,
            wrappedPrivateKey = keys.WrappedPrivateKey,
            encapsulatedAccountKeys = keys.EncapsulatedAccountKeys,
            manifest = ManifestFixture.Mint().Text,
            rotationEpoch,
        });
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Runs an options leg and returns the challenge bytes it issued.</summary>
    private static async Task<byte[]> BeginCeremonyAsync(HttpClient client, string path)
    {
        HttpResponseMessage response = await client.PostAsync(path, content: null);
        response.EnsureSuccessStatusCode();
        JsonNode options = (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!;
        return Base64UrlText.Decode(options["challenge"]!.GetValue<string>());
    }

    /// <summary>
    /// Puts one row carrying a narrative value into five of the six counted tables, at five different
    /// counts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Written through the domain's own factories over the container superuser connection</b>, the way
    /// every other arrangement of narrative rows in this suite is: the product offers no route that
    /// creates a category group or a payee in bulk, and rows written by raw SQL would carry envelopes no
    /// production write path could have produced.
    /// </para>
    /// <para>
    /// <b>The two note-less transactions are the presence test.</b> They carry a narrative column that is
    /// the whole of their row's narrative and is absent, so they are rows a chunk never visits and a
    /// count must not include.
    /// </para>
    /// <para>
    /// Every label is distinct because each of these tables carries a blind-index uniqueness rule over
    /// one budget, and two rows sharing a label collide on it rather than seeding a second row.
    /// </para>
    /// </remarks>
    private static async Task FurnishBudgetAsync(PostgresTestHost host, Guid budgetId)
    {
        await using BudgetoidDbContext db = CreateDb(host, budgetId);

        Account account = Account.Create(
            Guid.CreateVersion7(),
            budgetId,
            SealedNarrative.Indexed("Current"),
            AccountType.Checking,
            0m,
            "USD",
            UsdMinorUnit,
            SeedInstant);
        db.Accounts.Add(account);

        foreach (string label in new[] { "Grocer", "Baker" })
        {
            db.Payees.Add(Payee.Create(
                Guid.CreateVersion7(), budgetId, SealedNarrative.Indexed(label), SeedInstant));
        }

        List<CategoryGroup> groups = [];
        foreach ((string label, int position) in new[] { ("Everyday", 0), ("Bills", 1), ("Saving", 2) })
        {
            CategoryGroup group = CategoryGroup.Create(
                Guid.CreateVersion7(),
                budgetId,
                SealedNarrative.Indexed(label),
                SealedNarrative.Description($"{label} note"),
                position,
                SeedInstant);
            groups.Add(group);
            db.CategoryGroups.Add(group);
        }

        await db.SaveChangesAsync();

        // A save of its own: a category names its group by foreign key, so the group has to be on the
        // database before it can be filed under.
        foreach ((string label, int position) in new[]
                 {
                     ("Groceries", 0), ("Dining", 1), ("Electricity", 2), ("Holiday", 3),
                 })
        {
            db.Categories.Add(Category.Create(
                Guid.CreateVersion7(),
                budgetId,
                groups[position % groups.Count].Id,
                SealedNarrative.Indexed(label),
                SealedNarrative.Description($"{label} note"),
                position,
                SeedInstant));
        }

        // Five carrying a description and two carrying none. Transactions have no name and no unique
        // index but the key, so the labels repeat harmlessly and the dates only have to differ from
        // nothing at all.
        for (int index = 0; index < 7; index++)
        {
            db.Transactions.Add(Transaction.Create(
                Guid.CreateVersion7(),
                budgetId,
                account.Id,
                -1m * (index + 1),
                UsdMinorUnit,
                new DateOnly(2026, 6, 1).AddDays(index),
                index < 5 ? SealedNarrative.Description($"Spend {index}") : (NarrativeField?)null,
                SeedInstant));
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// A context on the container superuser connection bound to one ambient budget, so the five
    /// budget-owned tables' query filters resolve while the rows are written.
    /// </summary>
    private static BudgetoidDbContext CreateDb(PostgresTestHost host, Guid budgetId) => new(
        new DbContextOptionsBuilder<BudgetoidDbContext>()
            .UseNpgsql(host.ConnectionString)
            .Options,
        new TestBudgetContext(budgetId));

    /// <summary>
    /// Every factor the account holds, ordered, read on the superuser connection.
    /// </summary>
    /// <remarks>
    /// <c>wrapped_account_keys</c> carries <c>user_isolation</c>, so a policed read reports no row for a
    /// factor that is there exactly as it does for one that is not — and this listing is what every seal
    /// set in the file is built from.
    /// </remarks>
    private static async Task<IReadOnlyList<Guid>> FactorIdsAsync(NpgsqlConnection admin, Guid userId)
    {
        await using NpgsqlCommand command = new(
            "select factor_id from wrapped_account_keys where user_id = @id order by factor_id", admin);
        command.Parameters.AddWithValue("id", userId);

        List<Guid> factorIds = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            factorIds.Add(reader.GetGuid(0));
        }

        return factorIds;
    }

    /// <summary>The account's one staged rotation, or a failure saying how many there were.</summary>
    /// <remarks>
    /// Sole rather than first, for the reason <c>KeyRotationRepository.FindStagedRotationAsync</c> reads
    /// its own that way: <c>user_id</c> is the primary key of <c>key_rotations</c>, so a second row is a
    /// database that has lost the rule making two concurrent runs unstorable — not a row to choose
    /// between.
    /// </remarks>
    private static async Task<StagedRotationRow> StagedRotationAsync(NpgsqlConnection admin, Guid userId)
    {
        await using NpgsqlCommand command = new(
            "select rotation_id, staged_manifest, staged_rotation_epoch from key_rotations "
            + "where user_id = @id",
            admin);
        command.Parameters.AddWithValue("id", userId);

        List<StagedRotationRow> rows = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new StagedRotationRow(
                reader.GetGuid(0), reader.GetFieldValue<byte[]>(1), reader.GetInt32(2)));
        }

        return rows.Count == 1
            ? rows[0]
            : throw new InvalidOperationException($"Expected exactly one staged rotation, found {rows.Count}.");
    }

    /// <summary>
    /// The account's staged seals, factor by factor, in the alphabet the request carried them in.
    /// </summary>
    private static async Task<SortedDictionary<Guid, string>> SealTextByFactorAsync(
        NpgsqlConnection admin,
        Guid userId)
    {
        await using NpgsqlCommand command = new(
            "select factor_id, encapsulated_account_keys from key_rotation_seals where user_id = @id",
            admin);
        command.Parameters.AddWithValue("id", userId);

        SortedDictionary<Guid, string> seals = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            seals[reader.GetGuid(0)] = Base64UrlText.Encode(reader.GetFieldValue<byte[]>(1));
        }

        return seals;
    }

    /// <summary>The staged row's <c>started_at_utc</c>, read on the superuser connection.</summary>
    private static async Task<DateTime> StartedAtUtcAsync(NpgsqlConnection admin, Guid userId)
    {
        await using NpgsqlCommand command = new(
            "select started_at_utc from key_rotations where user_id = @id", admin);
        command.Parameters.AddWithValue("id", userId);

        return (DateTime)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("Expected a staged rotation, found none."));
    }

    /// <summary>
    /// <c>startedAtUtc</c> as <c>GET /api/me/key-rotation</c> spells it, for comparing the begin's answer
    /// against as text.
    /// </summary>
    private static async Task<string> ResumedStartedAtTextAsync(HttpClient client)
    {
        HttpResponseMessage response = await client.GetAsync(BeginPath);
        response.EnsureSuccessStatusCode();
        JsonObject body = await ReadJsonObjectAsync(response);
        return body["rotation"]!["startedAtUtc"]!.GetValue<string>();
    }

    /// <summary>A wire instant as UTC, refusing a spelling that carries no zone.</summary>
    private static DateTime ParseUtc(string text)
    {
        DateTime parsed = DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        return parsed.Kind == DateTimeKind.Utc
            ? parsed
            : throw new FormatException($"'{text}' is not a UTC instant.");
    }

    private static Task<long> CountRotationsAsync(NpgsqlConnection admin, Guid userId) =>
        CountAsync(admin, "select count(*) from key_rotations where user_id = @id", userId);

    private static Task<long> CountSealsAsync(NpgsqlConnection admin, Guid userId) =>
        CountAsync(admin, "select count(*) from key_rotation_seals where user_id = @id", userId);

    private static Task<long> CountOwnedBudgetsAsync(NpgsqlConnection admin, Guid userId) =>
        CountAsync(admin, "select count(*) from budgets where user_id = @id", userId);

    private static async Task<long> CountAsync(NpgsqlConnection admin, string sql, Guid userId)
    {
        await using NpgsqlCommand command = new(sql, admin);
        command.Parameters.AddWithValue("id", userId);

        return await command.ExecuteScalarAsync() switch
        {
            long count => count,
            var unexpected => throw new InvalidOperationException(
                $"Expected a count, got '{unexpected ?? "null"}'."),
        };
    }

    /// <summary>The response body as an object, so a member can be asked for by name.</summary>
    private static async Task<JsonObject> ReadJsonObjectAsync(HttpResponseMessage response) =>
        (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!.AsObject();

    /// <summary>
    /// The <c>title</c> of a problem-details body — the only member that says which of two 403s on this
    /// path answered.
    /// </summary>
    private static async Task<string> TitleOfAsync(HttpResponseMessage response) =>
        (await ReadJsonObjectAsync(response))["title"]!.GetValue<string>();

    /// <summary>
    /// A host that can run a whole registration: a test principal on the provider scheme for the options
    /// leg, and the application's own cookie handler left standing to read the session the finish leg
    /// sets.
    /// </summary>
    private static async Task<PostgresTestHost> StartRegisteringHostAsync()
    {
        PostgresTestHost host = new(
            usesApplicationAuthentication: true, repointsProviderSchemeToTestHandler: true);
        await host.StartAsync();
        return host;
    }
}
