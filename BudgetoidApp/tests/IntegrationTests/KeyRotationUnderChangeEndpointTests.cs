using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Application.Passkeys;
using Domain.Accounts;
using Domain.Payees;
using Domain.Security;
using Domain.Sessions;
using Domain.Transactions;
using Domain.Users;
using Infrastructure.Persistence;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// A rotation running while a second, stale tab keeps writing names under the index key the run is
/// replacing — the chunk leg meeting the four <c>(budget_id, name_key)</c> unique indexes.
/// </summary>
/// <remarks>
/// <para>
/// <b>The hazard.</b> A run re-seals row X "Foo" under the incoming index key, so X's index becomes
/// <c>new:Foo</c> and <c>old:Foo</c> is free again. A stale tab still holding the outgoing key then
/// creates row Y "Foo" under <c>old:Foo</c>, which collides with nothing. The next chunk re-seals Y to
/// <c>new:Foo</c>, and that <c>UPDATE</c> is the one that breaks the index — inside
/// <c>NarrativeResealRepository.SaveAsync</c>, which narrows that 23505 on the four index names and
/// translates it; both halves of that filter are held in <c>RepositoryConstraintAttributionTests</c>.
/// </para>
/// <para>
/// <b>The contract these cases pin</b>: the chunk answers <b>409</b> with <c>conflictKind</c>
/// <c>rotation_name_collision</c>, the whole chunk rolls back — asserted here over the two arms these
/// cases drive, an account beside the payee that collided — and the body names <b>no row identifier</b>.
/// The run then has a way forward that needs no new route: rename the colliding row through the ordinary
/// route, re-seal it, and complete.
/// </para>
/// <para>
/// <b>The wire token is asserted as text, never as the enum member</b>, because the token is what a
/// client branches on and a member rename must not move it silently.
/// </para>
/// <para>
/// <b>Index values come from labels that say which generation they stand for</b> —
/// <c>SealedNarrative.EncodedIndex("old:Foo")</c> against <c>("new:Foo")</c>. The server holds no index
/// key and cannot tell the two apart; the labels are for the reader.
/// </para>
/// <para>
/// <b>Rotations are staged on the container superuser in every case but one</b>, through
/// <see cref="KeyRotation.Begin" /> and the real <see cref="KeyRotationRepository" />, with one seal for
/// the one factor the account holds — so the completion case can promote. The begin's WebAuthn gate is
/// <c>KeyRotationBeginEndpointTests</c>' subject. The exception is the criterion-3 case, whose subject is
/// a run begun twice: it drives the real begin route, WebAuthn gate included, both times, and reads the
/// promoted factor row and manifest back to see which begin's bytes won.
/// </para>
/// <para>
/// <b>Every read-back runs on the superuser connection</b>, because half of what is asserted is that a
/// column did not move, and a policed connection reports a row it cannot see exactly as one that did not
/// change.
/// </para>
/// </remarks>
public sealed class KeyRotationUnderChangeEndpointTests
{
    private const string ChunkPath = "/api/me/key-rotation/chunks";
    private const string CompletionPath = "/api/me/key-rotation/completion";
    private const string PayeesPath = "/api/payees";
    private const string AccountsPath = "/api/accounts";
    private const string TransactionsPath = "/api/transactions";
    private const string BeginPath = "/api/me/key-rotation";
    private const string RegistrationOptionsPath = "/api/passkeys/registration/options";
    private const string RegistrationPath = "/api/passkeys/registration";
    private const string ReauthenticationOptionsPath = "/api/passkeys/reauthentication/options";

    /// <summary>The token the completion answers a row still owing a stamp with.</summary>
    private const string RotationIncomplete = "rotation_incomplete";

    private const string PayeeTable = "payees";
    private const string PayeeNameColumn = "name";

    /// <summary>The token a client branches on. Written out, never read off the spelling table.</summary>
    private const string RotationNameCollision = "rotation_name_collision";

    /// <summary>The token the ordinary create answers a taken blind index with today.</summary>
    private const string DuplicateName = "duplicate_name";

    private const int UsdMinorUnit = 2;

    /// <summary>Above the floor, so the staged row is one a begin could really have written.</summary>
    private const int StagedRotationEpoch = FactorManifest.MinimumRotationEpoch + 1;

    private const byte SealFiller = 0x21;

    private static readonly DateTime SeedInstant = new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>Any 8-4-4-4-12 identifier. A trace id is not one — its segments are 2-32-16-2.</summary>
    private static readonly Regex AnyIdentifier = new(
        "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// <b>The defect itself.</b> A stale tab's payee re-sealed onto a name the run already re-sealed onto
    /// another row is refused 409 <c>rotation_name_collision</c>, and nothing in the chunk is written —
    /// including the account the same chunk carried in another arm.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The account in the account arm is valid on its own</b>: its new index collides with nothing. So
    /// "it did not move" is a statement about the chunk rolling back as one unit, not about the account
    /// having been refused for a reason of its own.
    /// </para>
    /// <para>
    /// <b>The chunk is sent twice, byte for byte.</b> A client whose chunk was refused retries it before it
    /// knows why, and the retry must earn the same answer rather than a 500 or a 204 that wrote half.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ResealChunk_ForANameAlreadyResealedOnAnotherRow_Answers409AndRewritesNothing()
    {
        // Arrange — X "Foo" and an account, both under the outgoing key, and a run staged over them.
        await using PostgresTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = await OpenAdminAsync(host);
        ApiFactory.SignedInClient owner = await SignInWithOneFactorAsync(host, "google-under-change-payee");
        Guid resealedId = await SeedPayeeAsync(host, owner, "old:Foo");
        Guid accountId = await SeedAccountAsync(host, owner, "old:Checking", AccountType.Checking);
        Guid rotationId = await StageRotationAsync(host, owner);

        // X re-sealed under the incoming key, which frees old:Foo.
        HttpResponseMessage first = await owner.Client.PostAsJsonAsync(
            ChunkPath, Chunk(rotationId, payees: [NameEntry(resealedId, "new:Foo")]));
        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        // The stale tab creates Y "Foo" under the outgoing key. The premise: nothing refuses it.
        Guid staleId = Guid.CreateVersion7();
        HttpResponseMessage created = await owner.Client.PostAsJsonAsync(
            PayeesPath, PayeeCreateBody(staleId, "old:Foo"));
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);

        RowState staleBefore = await RowOfAsync(admin, "payees", staleId);
        RowState accountBefore = await RowOfAsync(admin, "accounts", accountId);
        RowState resealedBefore = await RowOfAsync(admin, "payees", resealedId);

        object colliding = Chunk(
            rotationId,
            accounts: [NameEntry(accountId, "new:Checking")],
            payees: [NameEntry(staleId, "new:Foo")]);

        // Act — the chunk, then the very same chunk again.
        HttpResponseMessage refused = await owner.Client.PostAsJsonAsync(ChunkPath, colliding);
        string refusedBody = await refused.Content.ReadAsStringAsync();
        HttpResponseMessage resent = await owner.Client.PostAsJsonAsync(ChunkPath, colliding);
        string resentBody = await resent.Content.ReadAsStringAsync();

        // Assert — a conflict and never a fault. The 500 line names what an untranslated save answers, so
        // a lost translation fails on the status it would really produce.
        await Assert.That(refused.StatusCode).IsNotEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(ConflictKindOf(refusedBody)).IsEqualTo(RotationNameCollision);

        // No row identifier anywhere in the body — not the row refused, not the row holding the name.
        await Assert.That(AnyIdentifier.IsMatch(refusedBody)).IsFalse();
        await Assert.That(refusedBody).DoesNotContain(staleId.ToString("N"));
        await Assert.That(refusedBody).DoesNotContain(resealedId.ToString("N"));

        // The retry earns the same answer.
        await Assert.That(resent.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(ConflictKindOf(resentBody)).IsEqualTo(RotationNameCollision);

        // And nothing moved: not Y, not the account from the other arm, not X.
        await Assert.That(await RowOfAsync(admin, "payees", staleId)).IsEqualTo(staleBefore);
        await Assert.That(await RowOfAsync(admin, "accounts", accountId)).IsEqualTo(accountBefore);
        await Assert.That(await RowOfAsync(admin, "payees", resealedId)).IsEqualTo(resealedBefore);

        // Stated against labels too, so an arrangement that never wrote what it meant cannot pass.
        await Assert.That(staleBefore).IsEqualTo(Unstamped("old:Foo"));
        await Assert.That(accountBefore).IsEqualTo(Unstamped("old:Checking"));
    }

    /// <summary>
    /// The same collision reached by a <b>rename</b> on the account route rather than a create on the
    /// payee one — the second of the four indexes, and the second ordinary write path.
    /// </summary>
    [Test]
    public async Task ResealChunk_ForARowRenamedOntoAResealedName_Answers409()
    {
        // Arrange — two accounts under the outgoing key, and a run staged over both.
        await using PostgresTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = await OpenAdminAsync(host);
        ApiFactory.SignedInClient owner = await SignInWithOneFactorAsync(host, "google-under-change-account");
        Guid resealedId = await SeedAccountAsync(host, owner, "old:Checking", AccountType.Checking);
        Guid renamedId = await SeedAccountAsync(host, owner, "old:Savings", AccountType.Savings);
        Guid rotationId = await StageRotationAsync(host, owner);

        HttpResponseMessage first = await owner.Client.PostAsJsonAsync(
            ChunkPath, Chunk(rotationId, accounts: [NameEntry(resealedId, "new:Checking")]));
        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        // The stale tab renames the second account onto the name the first one just left.
        HttpResponseMessage renamed = await owner.Client.PutAsJsonAsync(
            $"{AccountsPath}/{renamedId}", AccountUpdateBody("old:Checking", "Savings"));
        await Assert.That(renamed.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        RowState renamedBefore = await RowOfAsync(admin, "accounts", renamedId);

        // Act
        HttpResponseMessage response = await owner.Client.PostAsJsonAsync(
            ChunkPath, Chunk(rotationId, accounts: [NameEntry(renamedId, "new:Checking")]));
        string body = await response.Content.ReadAsStringAsync();

        // Assert
        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(ConflictKindOf(body)).IsEqualTo(RotationNameCollision);
        await Assert.That(AnyIdentifier.IsMatch(body)).IsFalse();

        await Assert.That(await RowOfAsync(admin, "accounts", renamedId)).IsEqualTo(renamedBefore);
        await Assert.That(renamedBefore).IsEqualTo(Unstamped("old:Checking"));
    }

    /// <summary>
    /// One chunk naming two rows under one new index is refused 409 the same way, and neither row is
    /// written — the collision is inside the chunk rather than against a row already on disk.
    /// </summary>
    /// <remarks>
    /// This is the shape no second tab is needed for, and it separates a fix that pre-checks the chunk's
    /// names against the database from one that answers the constraint itself: a pre-check reading only
    /// what is on disk finds nothing to refuse here.
    /// </remarks>
    [Test]
    public async Task ResealChunk_NamingTwoRowsUnderOneName_Answers409()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = await OpenAdminAsync(host);
        ApiFactory.SignedInClient owner = await SignInWithOneFactorAsync(host, "google-under-change-pair");
        Guid firstId = await SeedPayeeAsync(host, owner, "old:Foo");
        Guid secondId = await SeedPayeeAsync(host, owner, "old:Bar");
        Guid rotationId = await StageRotationAsync(host, owner);

        RowState firstBefore = await RowOfAsync(admin, "payees", firstId);
        RowState secondBefore = await RowOfAsync(admin, "payees", secondId);

        // Act
        HttpResponseMessage response = await owner.Client.PostAsJsonAsync(
            ChunkPath,
            Chunk(rotationId, payees: [NameEntry(firstId, "new:Foo"), NameEntry(secondId, "new:Foo")]));
        string body = await response.Content.ReadAsStringAsync();

        // Assert
        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(ConflictKindOf(body)).IsEqualTo(RotationNameCollision);
        await Assert.That(AnyIdentifier.IsMatch(body)).IsFalse();

        await Assert.That(await RowOfAsync(admin, "payees", firstId)).IsEqualTo(firstBefore);
        await Assert.That(await RowOfAsync(admin, "payees", secondId)).IsEqualTo(secondBefore);
    }

    /// <summary>
    /// The way forward: after the 409, the colliding row is renamed through the ordinary route, re-sealed,
    /// and the completion answers <b>204</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The 409 is asserted as arrangement</b>, so this is a statement about a run that was refused and
    /// then recovered — not about a run that never collided. A refusal that poisoned the run — deleted
    /// the staged row or its seals, or left anything else that stops it finishing — shows here as a completion that will not go
    /// through. It cannot show a half-written chunk, because the colliding chunk carries one row, nor a
    /// stamp left on the refused row, because the rename that follows clears it either way; the first
    /// case holds both.
    /// </para>
    /// <para>
    /// <b>The account holds exactly these two payees</b>, so the completeness gate has nothing else to
    /// count: both carry the run's stamp at the end or the completion is refused
    /// <c>rotation_incomplete</c>.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Rotation_AfterTheCollidingRowIsRenamed_Completes()
    {
        // Arrange — the collision, reached exactly as in the first case.
        await using PostgresTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = await OpenAdminAsync(host);
        ApiFactory.SignedInClient owner = await SignInWithOneFactorAsync(host, "google-under-change-recovery");
        Guid resealedId = await SeedPayeeAsync(host, owner, "old:Foo");
        Guid rotationId = await StageRotationAsync(host, owner);

        HttpResponseMessage first = await owner.Client.PostAsJsonAsync(
            ChunkPath, Chunk(rotationId, payees: [NameEntry(resealedId, "new:Foo")]));
        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        Guid staleId = Guid.CreateVersion7();
        HttpResponseMessage created = await owner.Client.PostAsJsonAsync(
            PayeesPath, PayeeCreateBody(staleId, "old:Foo"));
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);

        HttpResponseMessage collided = await owner.Client.PostAsJsonAsync(
            ChunkPath, Chunk(rotationId, payees: [NameEntry(staleId, "new:Foo")]));
        await Assert.That(collided.StatusCode).IsEqualTo(HttpStatusCode.Conflict);

        // The person resolves it: the second "Foo" becomes "Baz" on the ordinary route.
        HttpResponseMessage renamed = await owner.Client.PatchAsJsonAsync(
            $"{PayeesPath}/{staleId}", PayeeRenameBody("old:Baz"));
        await Assert.That(renamed.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        HttpResponseMessage resealed = await owner.Client.PostAsJsonAsync(
            ChunkPath, Chunk(rotationId, payees: [NameEntry(staleId, "new:Baz")]));
        await Assert.That(resealed.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        // Act
        HttpResponseMessage completion = await owner.Client.PostAsJsonAsync(
            CompletionPath, new { rotationId });

        // Assert
        await Assert.That(completion.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(await RowOfAsync(admin, "payees", staleId))
            .IsEqualTo(Stamped("new:Baz", rotationId));
        await Assert.That(await RowOfAsync(admin, "payees", resealedId))
            .IsEqualTo(Stamped("new:Foo", rotationId));
    }

    /// <summary>
    /// <b>The control, in the reverse order.</b> While X still carries the outgoing index, a stale create
    /// of the same name meets the create's own refusal — 409 <c>duplicate_name</c> — and the rotation has
    /// nothing to do with it.
    /// </summary>
    /// <remarks>
    /// Present so the new token cannot spread over the old one: a fix that answered every collision during
    /// a rotation as <c>rotation_name_collision</c> would send a person whose list is merely stale down the
    /// rename path instead of "adopt the payee you already have".
    /// </remarks>
    [Test]
    public async Task CreatePayee_ForANameStillUnderTheOutgoingKey_AnswersTheCreatesOwnDuplicateRefusal()
    {
        // Arrange — X "Foo" under the outgoing key, a run staged, and no chunk sent yet.
        await using PostgresTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = await OpenAdminAsync(host);
        ApiFactory.SignedInClient owner = await SignInWithOneFactorAsync(host, "google-under-change-control");
        Guid heldId = await SeedPayeeAsync(host, owner, "old:Foo");
        await StageRotationAsync(host, owner);
        Guid staleId = Guid.CreateVersion7();

        // Act
        HttpResponseMessage response = await owner.Client.PostAsJsonAsync(
            PayeesPath, PayeeCreateBody(staleId, "old:Foo"));
        string body = await response.Content.ReadAsStringAsync();

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(ConflictKindOf(body)).IsEqualTo(DuplicateName);
        await Assert.That(await RowOfAsync(admin, "payees", heldId)).IsEqualTo(Unstamped("old:Foo"));
        await Assert.That(await CountOfAsync(admin, "payees", staleId)).IsEqualTo(0L);
    }

    /// <summary>
    /// <b>Criterion 1, the create arm.</b> A payee created through the ordinary route after the begin —
    /// after every row that existed at the begin has been re-sealed — is outstanding at the gate: the
    /// completion answers 409 <c>rotation_incomplete</c> and promotes nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The row arrives with no stamp at all, and that is the whole case.</b> The gate's predicate has
    /// to put a never-stamped row on the outstanding side; a spelling that reads as a null guard
    /// (<c>RotationId.HasValue &amp;&amp; RotationId.Value != rotationId</c>) drops it, answers
    /// "complete", and the promotion destroys the only wrapped copies of the generation this payee's name
    /// is sealed under.
    /// </para>
    /// <para>
    /// <b>The run is staged rather than begun</b>, this file's convention: the begin's WebAuthn gate is
    /// <c>KeyRotationBeginEndpointTests</c>' subject, and the claim here is about the rows written after
    /// the staged row exists. The payee create, the chunk and the completion are the real routes.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Completion_WithAPayeeCreatedAfterTheBegin_Answers409RotationIncomplete()
    {
        // Arrange — one payee, a run staged over it, and that payee re-sealed: the run was complete.
        await using PostgresTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = await OpenAdminAsync(host);
        ApiFactory.SignedInClient owner = await SignInWithOneFactorAsync(host, "google-under-change-late-payee");
        Guid resealedId = await SeedPayeeAsync(host, owner, "old:Foo");
        Guid rotationId = await StageRotationAsync(host, owner);

        HttpResponseMessage chunk = await owner.Client.PostAsJsonAsync(
            ChunkPath, Chunk(rotationId, payees: [NameEntry(resealedId, "new:Foo")]));
        await Assert.That(chunk.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        // A second tab files a new payee under the outgoing key.
        Guid lateId = Guid.CreateVersion7();
        HttpResponseMessage created = await owner.Client.PostAsJsonAsync(
            PayeesPath, PayeeCreateBody(lateId, "old:Bar"));
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);

        // The premises: the late row really carries no stamp, and a promotion would really move something.
        await Assert.That(await RowOfAsync(admin, "payees", lateId)).IsEqualTo(Unstamped("old:Bar"));
        KeyState before = await SnapshotAsync(host, owner.UserId);
        await Assert.That(before.RotationEpoch).IsNotEqualTo(StagedRotationEpoch);

        // Act
        HttpResponseMessage completion = await owner.Client.PostAsJsonAsync(
            CompletionPath, new { rotationId });
        string body = await completion.Content.ReadAsStringAsync();

        // Assert — refused as outstanding, and nothing promoted.
        await Assert.That(completion.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(ConflictKindOf(body)).IsEqualTo(RotationIncomplete);
        await Assert.That(await SnapshotAsync(host, owner.UserId)).IsEqualTo(before);
    }

    /// <summary>
    /// <b>Criterion 1, the presence arm.</b> A note added through the ordinary route to a transaction
    /// that had none at the begin makes that transaction outstanding: 409 <c>rotation_incomplete</c>,
    /// nothing promoted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The transaction is never visited by a chunk, and that is faithful to a client.</b> The begin's
    /// inventory counts a note-less transaction as owing nothing, so a client following it re-seals the
    /// account and stops. The row then gains narrative with no stamp beside it — a transition the
    /// presence-aware arm of the gate has to see, since the population it counted at the begin did not
    /// include this row. A transaction created with a note after the begin is another, and is not staged
    /// here.
    /// </para>
    /// <para>
    /// The account is re-sealed so the transaction is the only thing outstanding.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Completion_WithANoteAddedToANotelessTransactionAfterTheBegin_Answers409RotationIncomplete()
    {
        // Arrange — an account and a note-less transaction on it, a run staged, the account re-sealed.
        await using PostgresTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = await OpenAdminAsync(host);
        ApiFactory.SignedInClient owner = await SignInWithOneFactorAsync(host, "google-under-change-late-note");
        Guid accountId = await SeedAccountAsync(host, owner, "old:Checking", AccountType.Checking);
        Guid transactionId = await SeedTransactionAsync(host, owner, accountId, noteLabel: null);
        Guid rotationId = await StageRotationAsync(host, owner);

        HttpResponseMessage chunk = await owner.Client.PostAsJsonAsync(
            ChunkPath, Chunk(rotationId, accounts: [NameEntry(accountId, "new:Checking")]));
        await Assert.That(chunk.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        // A second tab gives the transaction a note, under the outgoing key.
        HttpResponseMessage edited = await owner.Client.PatchAsJsonAsync(
            $"{TransactionsPath}/{transactionId}", new { description = SealedNarrative.EncodedDescription("old:Note") });
        await Assert.That(edited.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        // The premises: the note landed and no stamp came with it.
        TransactionState noted = await TransactionOfAsync(admin, transactionId);
        await Assert.That(noted.HasDescription).IsTrue()
            .Because("the ordinary route must have written the note the case is about");
        await Assert.That(noted.RotationId).IsNull();
        KeyState before = await SnapshotAsync(host, owner.UserId);
        await Assert.That(before.RotationEpoch).IsNotEqualTo(StagedRotationEpoch);

        // Act
        HttpResponseMessage completion = await owner.Client.PostAsJsonAsync(
            CompletionPath, new { rotationId });
        string body = await completion.Content.ReadAsStringAsync();

        // Assert
        await Assert.That(completion.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(ConflictKindOf(body)).IsEqualTo(RotationIncomplete);
        await Assert.That(await SnapshotAsync(host, owner.UserId)).IsEqualTo(before);
    }

    /// <summary>
    /// <b>Criterion 1, the disowning arm.</b> A transaction whose note a chunk already re-sealed and
    /// stamped, then edited through the ordinary route, is outstanding again: 409
    /// <c>rotation_incomplete</c>, nothing promoted.
    /// </summary>
    /// <remarks>
    /// <b>The stamp is asserted as arrangement before the edit</b>, so the refusal is a statement about
    /// the edit clearing it rather than about a chunk that never landed. The edit writes old-key
    /// ciphertext; left stamped, the row would read as rewritten while its note is sealed under the
    /// generation the promotion retires, and the note would stop opening with nothing red anywhere.
    /// </remarks>
    [Test]
    public async Task Completion_WithAResealedTransactionsNoteEditedAfterItsChunk_Answers409RotationIncomplete()
    {
        // Arrange — an account and a noted transaction, a run staged, both re-sealed in one chunk.
        await using PostgresTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = await OpenAdminAsync(host);
        ApiFactory.SignedInClient owner = await SignInWithOneFactorAsync(host, "google-under-change-edited-note");
        Guid accountId = await SeedAccountAsync(host, owner, "old:Checking", AccountType.Checking);
        Guid transactionId = await SeedTransactionAsync(host, owner, accountId, noteLabel: "old:Note");
        Guid rotationId = await StageRotationAsync(host, owner);

        HttpResponseMessage chunk = await owner.Client.PostAsJsonAsync(
            ChunkPath,
            Chunk(
                rotationId,
                accounts: [NameEntry(accountId, "new:Checking")],
                transactions: [NoteEntry(transactionId, "new:Note")]));
        await Assert.That(chunk.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That((await TransactionOfAsync(admin, transactionId)).RotationId).IsEqualTo(rotationId);

        // A second tab edits the note, under the outgoing key.
        HttpResponseMessage edited = await owner.Client.PatchAsJsonAsync(
            $"{TransactionsPath}/{transactionId}", new { description = SealedNarrative.EncodedDescription("old:Edited") });
        await Assert.That(edited.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        // The premise: the edit disowned the stamp.
        await Assert.That((await TransactionOfAsync(admin, transactionId)).RotationId).IsNull();
        KeyState before = await SnapshotAsync(host, owner.UserId);
        await Assert.That(before.RotationEpoch).IsNotEqualTo(StagedRotationEpoch);

        // Act
        HttpResponseMessage completion = await owner.Client.PostAsJsonAsync(
            CompletionPath, new { rotationId });
        string body = await completion.Content.ReadAsStringAsync();

        // Assert
        await Assert.That(completion.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(ConflictKindOf(body)).IsEqualTo(RotationIncomplete);
        await Assert.That(await SnapshotAsync(host, owner.UserId)).IsEqualTo(before);
    }

    /// <summary>
    /// <b>Criterion 3.</b> A run begun again under the same rotation id and the same generation after a
    /// chunk has landed completes, and every row re-sealed across both begins opens under the promoted
    /// generation — which is what a real factor hands back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Real cryptography end to end, and every key is one a factor or the API handed back.</b> The
    /// account's factor is registered through the real passkey route carrying an
    /// <see cref="AccountKeyFixture" /> factor; both begins go through the real WebAuthn gate carrying
    /// the next generation encapsulated to that factor's public key and a manifest sealed under the next
    /// content key; the payees are created and re-sealed as a browser would seal them. After the
    /// completion the factor's <em>stored</em> row is opened with its own key-encryption key, and each
    /// payee's name is opened, as served, under the content key that open produced.
    /// </para>
    /// <para>
    /// <b>The second begin is a fresh encapsulation of the same generation</b> — a new ephemeral key and
    /// nonce, so its seal is different bytes — and a freshly sealed manifest at the same epoch. That is
    /// what a resuming client sends when it has lost its staged state: the same run, restaged. Payee A's
    /// stamp, written between the two begins, is what the completion has to still honour.
    /// </para>
    /// <para>
    /// <b>Which begin's bytes were promoted is asserted, not only that they open.</b> Both seals
    /// encapsulate the same generation, so a second begin whose staging left the first seal in place
    /// would still open to the right keys — the factor row and the manifest are therefore compared, as
    /// text, against the second begin's own seal and manifest.
    /// </para>
    /// <para>
    /// <b>The blind indexes are labels, not HMACs under the next index key.</b> The server holds no
    /// index key and cannot tell the two apart, and this suite carries no transcription of the client's
    /// normalization table to compute a faithful one with — so a real index would add a second copy of
    /// that table for a column nothing here opens. What is opened is the name.
    /// </para>
    /// <para>
    /// <b>The account holds exactly these two narrative rows</b>: the seeded sign-in files a budget with
    /// no name and nothing else, so the completion's full house is A and B.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Rotation_BegunAgainAfterChunksLanded_CompletesAndEveryResealedRowOpens()
    {
        // Arrange — an account whose one factor carries real keys, and two payees sealed under them.
        await using PostgresTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = await OpenAdminAsync(host);
        ApiFactory.SignedInClient owner = await host.Factory.CreateSignedInClientAsync("google-under-change-rebegin");
        AccountKeyFixture current = AccountKeyFixture.Mint();
        AccountKeyFixture.Factor factor = current.MintFactor();
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        (await RegisterPasskeyAsync(owner.Client, device, factor)).EnsureSuccessStatusCode();

        Guid payeeA = await CreateSealedPayeeAsync(owner, current, "Corner Store", "old:Corner Store");
        Guid payeeB = await CreateSealedPayeeAsync(owner, current, "Bakery", "old:Bakery");

        AccountKeyFixture next = AccountKeyFixture.Mint();
        Guid rotationId = Guid.CreateVersion7();
        int epoch = await FactorGeneration.NextAsync(owner.Client);

        // The first begin, and A re-sealed under it.
        (HttpResponseMessage begun, byte[] firstSeal, byte[] firstManifest) =
            await BeginWithAsync(owner, device, factor, next, rotationId, epoch, signCount: 1);
        await Assert.That(begun.StatusCode).IsEqualTo(HttpStatusCode.OK);

        HttpResponseMessage chunkA = await owner.Client.PostAsJsonAsync(
            ChunkPath, Chunk(rotationId, payees: [SealedNameEntry(next, payeeA, "Corner Store", "new:Corner Store")]));
        await Assert.That(chunkA.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        // Act — begun again under the same run and generation, then B re-sealed, then completed.
        (HttpResponseMessage rebegun, byte[] secondSeal, byte[] secondManifest) =
            await BeginWithAsync(owner, device, factor, next, rotationId, epoch, signCount: 2);
        await Assert.That(rebegun.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // The premise: the second begin really carried different bytes from the first.
        await Assert.That(Base64UrlText.Encode(secondSeal)).IsNotEqualTo(Base64UrlText.Encode(firstSeal));
        await Assert.That(Base64UrlText.Encode(secondManifest))
            .IsNotEqualTo(Base64UrlText.Encode(firstManifest));
        await Assert.That((await RowOfAsync(admin, "payees", payeeA)).RotationId).IsEqualTo(rotationId);

        HttpResponseMessage chunkB = await owner.Client.PostAsJsonAsync(
            ChunkPath, Chunk(rotationId, payees: [SealedNameEntry(next, payeeB, "Bakery", "new:Bakery")]));
        await Assert.That(chunkB.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        HttpResponseMessage completion = await owner.Client.PostAsJsonAsync(CompletionPath, new { rotationId });

        // Assert — the run completed.
        await Assert.That(completion.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        // The factor's stored row opens, under its own key-encryption key, to the next generation.
        (byte[] wrappedPrivateKey, byte[] encapsulatedAccountKeys) = await StoredFactorAsync(admin, factor.Id);
        bool opened = factor.TryOpen(wrappedPrivateKey, encapsulatedAccountKeys, out byte[] contentKey, out byte[] indexKey);
        await Assert.That(opened).IsTrue()
            .Because("the promoted factor row must open under the factor's own key-encryption key");
        await Assert.That(Base64UrlText.Encode(contentKey)).IsEqualTo(Base64UrlText.Encode(next.ContentKey));
        await Assert.That(Base64UrlText.Encode(indexKey)).IsEqualTo(Base64UrlText.Encode(next.IndexKey));

        // And the bytes promoted are the second begin's, not the first's.
        await Assert.That(Base64UrlText.Encode(encapsulatedAccountKeys))
            .IsEqualTo(Base64UrlText.Encode(secondSeal));
        await Assert.That(Base64UrlText.Encode(await StoredManifestAsync(admin, owner.UserId)))
            .IsEqualTo(Base64UrlText.Encode(secondManifest));

        // Both payees, as served, open under the content key that factor handed back.
        foreach ((Guid payeeId, string plaintext) in new[] { (payeeA, "Corner Store"), (payeeB, "Bakery") })
        {
            (Guid servedId, string wire) = await ServedPayeeNameAsync(owner, payeeId);
            bool readable = AccountKeyFixture.TryOpenNarrative(
                contentKey, PayeeTable, PayeeNameColumn, servedId, wire, out string text);

            await Assert.That(readable).IsTrue()
                .Because($"payee {payeeId}'s name must open under the promoted content key");
            await Assert.That(text).IsEqualTo(plaintext);
        }
    }

    /// <summary>
    /// The columns a reseal may write on a named row, as the database holds them.
    /// </summary>
    /// <remarks>
    /// Carried as base64url text rather than byte arrays: a record compares arrays by reference, and
    /// TUnit's <c>IsEquivalentTo</c> ignores order, so one string equality is the comparison that can be
    /// satisfied neither way — and a failure prints two values a reader can line up.
    /// </remarks>
    private sealed record RowState(string Name, string NameKey, Guid? RotationId);

    /// <summary>What a row sealed under <paramref name="label" /> and never stamped holds.</summary>
    private static RowState Unstamped(string label) =>
        new(SealedNarrative.EncodedName(label), SealedNarrative.EncodedIndex(label), null);

    /// <summary>What a row re-sealed to <paramref name="label" /> by <paramref name="rotationId" /> holds.</summary>
    private static RowState Stamped(string label, Guid rotationId) =>
        new(SealedNarrative.EncodedName(label), SealedNarrative.EncodedIndex(label), rotationId);

    /// <summary>
    /// A chunk carrying the given arms and empty arrays for the rest — the wire shape, not a request
    /// record.
    /// </summary>
    private static object Chunk(
        Guid rotationId,
        object[]? accounts = null,
        object[]? payees = null,
        object[]? transactions = null) => new
        {
            rotationId,
            accounts = accounts ?? [],
            payees = payees ?? [],
            categoryGroups = Array.Empty<object>(),
            categories = Array.Empty<object>(),
            transactions = transactions ?? [],
        };

    /// <summary>One entry of the transaction arm: a note over a label.</summary>
    private static object NoteEntry(Guid id, string label) => new
    {
        id,
        description = SealedNarrative.EncodedDescription(label),
    };

    /// <summary>
    /// One entry of the payee arm carrying a name really sealed under <paramref name="generation" />'s
    /// content key, and an index that is a label — see the criterion-3 case for why.
    /// </summary>
    private static object SealedNameEntry(
        AccountKeyFixture generation,
        Guid id,
        string plaintext,
        string indexLabel) => new
        {
            id,
            name = generation.SealNarrative(PayeeTable, PayeeNameColumn, id, plaintext),
            nameKey = SealedNarrative.EncodedIndex(indexLabel),
        };

    /// <summary>One entry of the account or payee arm: a name and its index over the same label.</summary>
    private static object NameEntry(Guid id, string label) => new
    {
        id,
        name = SealedNarrative.EncodedName(label),
        nameKey = SealedNarrative.EncodedIndex(label),
    };

    private static object PayeeCreateBody(Guid id, string label) => new
    {
        id = id.ToString("D"),
        name = SealedNarrative.EncodedName(label),
        nameKey = SealedNarrative.EncodedIndex(label),
    };

    private static object PayeeRenameBody(string label) => new
    {
        name = SealedNarrative.EncodedName(label),
        nameKey = SealedNarrative.EncodedIndex(label),
    };

    private static object AccountUpdateBody(string label, string type) => new
    {
        name = SealedNarrative.EncodedName(label),
        nameKey = SealedNarrative.EncodedIndex(label),
        type,
        openingBalance = 0m,
    };

    /// <summary>
    /// The <c>conflictKind</c> member of a problem body, or <see langword="null" /> when the body is not
    /// one — so a 500 reads as "no kind" in the failure rather than as a parse error.
    /// </summary>
    private static string? ConflictKindOf(string body)
    {
        try
        {
            return JsonNode.Parse(body)?["conflictKind"]?.GetValue<string>();
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>Signs an account in and hangs one factor off the passkey its session was opened with.</summary>
    /// <remarks>
    /// One factor so a staged run can carry the one seal the completion promotes. The seeded sign-in files
    /// a passkey and no <c>wrapped_account_keys</c> row, so without this the account has nothing to seal.
    /// </remarks>
    private static async Task<ApiFactory.SignedInClient> SignInWithOneFactorAsync(
        PostgresTestHost host,
        string subject)
    {
        ApiFactory.SignedInClient signedIn = await host.Factory.CreateSignedInClientAsync(
            subject, kind: SessionKind.Full);

        Guid passkeyId;
        await using (BudgetoidDbContext db = SuperuserDb(host))
        {
            passkeyId = (await db.Credentials.SingleAsync(credential =>
                credential.UserId == signedIn.UserId && credential.Type == CredentialType.Passkey)).Id;
        }

        await RepositoryTestHost.SeedWrappedAccountKeysOnAsync(
            host.ConnectionString, passkeyId, Guid.CreateVersion7());

        return signedIn;
    }

    /// <summary>Stages one run with a seal for every factor, and returns the identifier a chunk quotes.</summary>
    private static async Task<Guid> StageRotationAsync(PostgresTestHost host, ApiFactory.SignedInClient owner)
    {
        await using BudgetoidDbContext db = SuperuserDb(host);
        KeyRotationRepository repository = new(db);

        Credential passkey = await db.Credentials.SingleAsync(credential =>
            credential.UserId == owner.UserId && credential.Type == CredentialType.Passkey);
        IReadOnlyDictionary<Guid, WrappedAccountKeys> factors =
            await repository.ListFactorsAsync(owner.UserId);

        KeyRotation rotation = KeyRotation.Begin(
            passkey, Guid.CreateVersion7(), ManifestFixture.Mint().Manifest, StagedRotationEpoch, SeedInstant);
        KeyRotationSeal[] seals =
        [
            .. factors.Values.Select(factor => KeyRotationSeal.For(
                rotation, factor, RepositoryTestHost.EncapsulatedAccountKeysPayload(SealFiller))),
        ];

        await repository.StageAsync(rotation, seals);

        return rotation.RotationId;
    }

    /// <summary>Files one payee under <paramref name="label" />, through the domain's own factory.</summary>
    private static async Task<Guid> SeedPayeeAsync(
        PostgresTestHost host,
        ApiFactory.SignedInClient owner,
        string label)
    {
        await using BudgetoidDbContext db = BudgetDb(host, owner.BudgetId);
        Payee payee = Payee.Create(
            Guid.CreateVersion7(), owner.BudgetId, SealedNarrative.Indexed(label), SeedInstant);
        db.Payees.Add(payee);
        await db.SaveChangesAsync();

        return payee.Id;
    }

    /// <summary>Files one USD account under <paramref name="label" />, through the domain's own factory.</summary>
    private static async Task<Guid> SeedAccountAsync(
        PostgresTestHost host,
        ApiFactory.SignedInClient owner,
        string label,
        AccountType type)
    {
        await using BudgetoidDbContext db = BudgetDb(host, owner.BudgetId);
        Account account = Account.Create(
            Guid.CreateVersion7(),
            owner.BudgetId,
            SealedNarrative.Indexed(label),
            type,
            0m,
            "USD",
            UsdMinorUnit,
            SeedInstant);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        return account.Id;
    }

    /// <summary>One named row of <c>accounts</c> or <c>payees</c>, read raw on the superuser.</summary>
    private static async Task<RowState> RowOfAsync(NpgsqlConnection admin, string table, Guid rowId)
    {
        if (table is not ("accounts" or "payees"))
        {
            throw new ArgumentOutOfRangeException(nameof(table), table, "Only the two named arms are read.");
        }

        // The table name is interpolated because an identifier cannot be a parameter; it is never
        // caller-supplied text, and the guard above holds it to two literals.
        await using NpgsqlCommand command = new(
            $"select name, name_key, rotation_id from {table} where id = @id", admin);
        command.Parameters.AddWithValue("id", rowId);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException($"No row of '{table}' is filed under {rowId}.");
        }

        return new RowState(
            Base64UrlText.Encode((byte[])reader.GetValue(0)),
            Base64UrlText.Encode((byte[])reader.GetValue(1)),
            reader.IsDBNull(2) ? null : reader.GetGuid(2));
    }

    private static async Task<long> CountOfAsync(NpgsqlConnection admin, string table, Guid rowId)
    {
        await using NpgsqlCommand command = new($"select count(*) from {table} where id = @id", admin);
        command.Parameters.AddWithValue("id", rowId);

        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// The whole of an account's key material as the database holds it — the manifest's generation, its
    /// bytes, and one encapsulated value per factor — for "the refused completion promoted nothing".
    /// </summary>
    /// <remarks>
    /// Text rather than byte arrays, and sorted, for the reason <see cref="RowState" /> gives; the shape
    /// is <c>KeyRotationCompletionEndpointTests</c>' own, copied rather than shared as this suite does.
    /// Compared with <c>IsEqualTo</c> on the record, whose generated equality over a
    /// <see cref="SortedDictionary{TKey,TValue}" /> is reference equality — so it is flattened to a
    /// string first.
    /// </remarks>
    private sealed record KeyState(int RotationEpoch, string Manifest, string FactorKeys);

    private static async Task<KeyState> SnapshotAsync(PostgresTestHost host, Guid userId)
    {
        await using BudgetoidDbContext db = SuperuserDb(host);

        FactorManifest manifest = await db.FactorManifests.SingleAsync(row => row.UserId == userId);
        List<WrappedAccountKeys> factors = await db.WrappedAccountKeys
            .Where(row => row.UserId == userId)
            .ToListAsync();

        string factorKeys = string.Join(
            ", ",
            factors
                .OrderBy(row => row.FactorId)
                .Select(row => $"{row.FactorId:D}={Base64UrlText.Encode(row.EncapsulatedAccountKeys.ToArray())}"));

        return new KeyState(
            manifest.RotationEpoch,
            Base64UrlText.Encode(manifest.Manifest.ToArray()),
            factorKeys);
    }

    /// <summary>A transaction's stamp, and whether it holds a note, read raw on the superuser.</summary>
    private sealed record TransactionState(bool HasDescription, Guid? RotationId);

    private static async Task<TransactionState> TransactionOfAsync(NpgsqlConnection admin, Guid transactionId)
    {
        await using NpgsqlCommand command = new(
            "select description is not null, rotation_id from transactions where id = @id", admin);
        command.Parameters.AddWithValue("id", transactionId);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException($"No transaction is filed under {transactionId}.");
        }

        return new TransactionState(reader.GetBoolean(0), reader.IsDBNull(1) ? null : reader.GetGuid(1));
    }

    /// <summary>Files one transaction on <paramref name="accountId" />, with a note or without one.</summary>
    private static async Task<Guid> SeedTransactionAsync(
        PostgresTestHost host,
        ApiFactory.SignedInClient owner,
        Guid accountId,
        string? noteLabel)
    {
        await using BudgetoidDbContext db = BudgetDb(host, owner.BudgetId);
        Transaction transaction = Transaction.Create(
            Guid.CreateVersion7(),
            owner.BudgetId,
            accountId,
            -1m,
            UsdMinorUnit,
            new DateOnly(2026, 6, 1),
            noteLabel is null ? null : SealedNarrative.Description(noteLabel),
            SeedInstant);
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync();

        return transaction.Id;
    }

    /// <summary>
    /// Creates one payee through the ordinary route, its name really sealed under
    /// <paramref name="generation" />'s content key.
    /// </summary>
    private static async Task<Guid> CreateSealedPayeeAsync(
        ApiFactory.SignedInClient owner,
        AccountKeyFixture generation,
        string plaintext,
        string indexLabel)
    {
        Guid id = Guid.CreateVersion7();
        HttpResponseMessage created = await owner.Client.PostAsJsonAsync(PayeesPath, new
        {
            id = id.ToString("D"),
            name = generation.SealNarrative(PayeeTable, PayeeNameColumn, id, plaintext),
            nameKey = SealedNarrative.EncodedIndex(indexLabel),
        });
        created.EnsureSuccessStatusCode();

        return id;
    }

    /// <summary>A payee as the API serves it: the id it names and the name's wire value.</summary>
    private static async Task<(Guid ServedId, string Wire)> ServedPayeeNameAsync(
        ApiFactory.SignedInClient owner,
        Guid payeeId)
    {
        HttpResponseMessage read = await owner.Client.GetAsync($"{PayeesPath}/{payeeId:D}");
        read.EnsureSuccessStatusCode();
        JsonNode payee = (await JsonNode.ParseAsync(await read.Content.ReadAsStreamAsync()))!;

        return (Guid.Parse(payee["id"]!.GetValue<string>()), payee["name"]!.GetValue<string>());
    }

    /// <summary>One factor's stored pair, read raw on the superuser.</summary>
    private static async Task<(byte[] WrappedPrivateKey, byte[] EncapsulatedAccountKeys)> StoredFactorAsync(
        NpgsqlConnection admin,
        Guid factorId)
    {
        await using NpgsqlCommand command = new(
            "select wrapped_private_key, encapsulated_account_keys from wrapped_account_keys where factor_id = @id",
            admin);
        command.Parameters.AddWithValue("id", factorId);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException($"No factor is filed under {factorId}.");
        }

        return (reader.GetFieldValue<byte[]>(0), reader.GetFieldValue<byte[]>(1));
    }

    /// <summary>The account's stored manifest, read raw on the superuser.</summary>
    private static async Task<byte[]> StoredManifestAsync(NpgsqlConnection admin, Guid userId)
    {
        await using NpgsqlCommand command = new(
            "select manifest from factor_manifests where user_id = @id", admin);
        command.Parameters.AddWithValue("id", userId);

        return (byte[])(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException($"No manifest is filed for {userId}."));
    }

    /// <summary>
    /// Begins <paramref name="rotationId" /> through the real route: the re-authentication options leg,
    /// <paramref name="device" /> answering it, and a body carrying <paramref name="generation" />
    /// encapsulated afresh to <paramref name="factor" />'s public key and a manifest naming that factor,
    /// sealed afresh under the generation's content key at <paramref name="epoch" />.
    /// </summary>
    private static async Task<(HttpResponseMessage Response, byte[] Seal, byte[] Manifest)> BeginWithAsync(
        ApiFactory.SignedInClient owner,
        SyntheticAuthenticator device,
        AccountKeyFixture.Factor factor,
        AccountKeyFixture generation,
        Guid rotationId,
        int epoch,
        uint signCount)
    {
        byte[] seal;
        byte[] manifest;

        using (ECDiffieHellman factorPair = FactorKeyPairOf(factor))
        {
            using ECDiffieHellmanPublicKey publicKey = factorPair.PublicKey;
            seal = ClientKeyCustody.EncapsulateAccountKeys(
                publicKey, generation.ContentKey, generation.IndexKey, factor.Id);
            manifest = ClientKeyCustody.SealManifest(
                generation.ContentKey,
                [new ClientKeyCustody.FactorPublicKey(factor.Id, ClientKeyCustody.UncompressedPoint(publicKey))],
                epoch);
        }

        byte[] challenge = await BeginCeremonyAsync(owner.Client, ReauthenticationOptionsPath);
        AssertionResult assertion = device.Authenticate(
            challenge,
            ApiFactory.PasskeyOrigin,
            PasskeyEncoding.ToUserHandle(owner.UserId),
            signCount);

        HttpResponseMessage response = await owner.Client.PostAsJsonAsync(BeginPath, new
        {
            rotationId,
            manifest = Base64UrlText.Encode(manifest),
            rotationEpoch = epoch,
            seals = new[]
            {
                new { factorId = factor.FactorId, encapsulatedAccountKeys = Base64UrlText.Encode(seal) },
            },
            credentialId = assertion.CredentialIdBase64Url,
            clientDataJson = assertion.ClientDataJsonBase64Url,
            authenticatorData = assertion.AuthenticatorDataBase64Url,
            signature = assertion.SignatureBase64Url,
            userHandle = assertion.UserHandleBase64Url,
        });

        return (response, seal, manifest);
    }

    /// <summary>
    /// The factor's key pair, recovered from its wrapped private key under its own key-encryption key —
    /// the public half is what a client encapsulates the next generation to.
    /// </summary>
    private static ECDiffieHellman FactorKeyPairOf(AccountKeyFixture.Factor factor)
    {
        if (!ClientKeyCustody.TryOpen(
                factor.KeyEncryptionKey,
                factor.PrivateKeyEnvelope,
                ClientKeyCustody.WrappedPrivateKeyAssociatedData(factor.Id),
                out byte[] pkcs8))
        {
            throw new InvalidOperationException("The factor's own wrapped private key did not open.");
        }

        ECDiffieHellman pair = ClientKeyCustody.CreateFactorKeyPair();
        pair.ImportPkcs8PrivateKey(pkcs8, out _);

        return pair;
    }

    /// <summary>
    /// Runs both authenticated legs of a passkey registration carrying <paramref name="factor" />'s real
    /// key material — copied from <c>AccountKeyContinuityTests</c>, as this suite copies helpers.
    /// </summary>
    private static async Task<HttpResponseMessage> RegisterPasskeyAsync(
        HttpClient client,
        SyntheticAuthenticator device,
        AccountKeyFixture.Factor factor)
    {
        byte[] challenge = await BeginCeremonyAsync(client, RegistrationOptionsPath);
        AttestationResult attestation = device.Register(
            challenge,
            ApiFactory.PasskeyOrigin,
            signCount: 0,
            prfEnabled: true);
        int rotationEpoch = await FactorGeneration.NextAsync(client);

        return await client.PostAsJsonAsync(RegistrationPath, new
        {
            clientDataJson = attestation.ClientDataJsonBase64Url,
            attestationObject = attestation.AttestationObjectBase64Url,
            clientExtensionResults = new { prf = new { enabled = true } },
            factorId = factor.FactorId,
            wrappedPrivateKey = factor.WrappedPrivateKey,
            encapsulatedAccountKeys = factor.EncapsulatedAccountKeys,
            manifest = ManifestFixture.Mint().Text,
            rotationEpoch,
        });
    }

    /// <summary>Runs an options leg and returns the challenge bytes it issued.</summary>
    private static async Task<byte[]> BeginCeremonyAsync(HttpClient client, string path)
    {
        HttpResponseMessage response = await client.PostAsync(path, content: null);
        response.EnsureSuccessStatusCode();
        JsonNode options = (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!;

        return Base64UrlText.Decode(options["challenge"]!.GetValue<string>());
    }

    /// <summary>A superuser context with no ambient budget, for the user-policed tables.</summary>
    private static BudgetoidDbContext SuperuserDb(PostgresTestHost host) => new(
        new DbContextOptionsBuilder<BudgetoidDbContext>()
            .UseNpgsql(host.ConnectionString)
            .Options);

    /// <summary>A superuser context bound to one budget, so the budget-owned query filters resolve.</summary>
    private static BudgetoidDbContext BudgetDb(PostgresTestHost host, Guid budgetId) => new(
        new DbContextOptionsBuilder<BudgetoidDbContext>()
            .UseNpgsql(host.ConnectionString)
            .Options,
        new TestBudgetContext(budgetId));

    private static async Task<NpgsqlConnection> OpenAdminAsync(PostgresTestHost host)
    {
        NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();

        return connection;
    }

    private static async Task<PostgresTestHost> StartHostAsync()
    {
        PostgresTestHost host = new(usesApplicationAuthentication: true);
        await host.StartAsync();

        return host;
    }
}
