using Application.Abstractions;
using Domain.Accounts;
using Domain.Categories;
using Domain.CategoryGroups;
using Domain.Payees;
using Domain.Security;
using Domain.Transactions;
using Domain.Users;
using NotFoundException = Domain.Common.NotFoundException;
using ValidationException = Domain.Common.ValidationException;

namespace Application.KeyRotations.ResealRows;

/// <summary>
/// One chunk of a content-key rotation: re-seals the rows a client names under the next generation's
/// keys and stamps each with the run that rewrote it, in one unit of work.
/// </summary>
/// <remarks>
/// <para>
/// <b>TWO RULES BELOW ARE INVISIBLE FROM THE CODE THAT OBEYS THEM, AND BOTH HAVE A SHORTER SPELLING
/// THAT READS PERFECTLY WELL. They are stated here because a later reader will otherwise simplify
/// either one, and neither simplification fails anywhere near where it was made.</b>
/// </para>
/// <para>
/// <b>ONE — RESOLVE EVERY ROW ACROSS ALL FIVE ARMS, THEN MUTATE.</b> The natural shape is the other
/// one: load an arm, apply it, move to the next. It is wrong, and the failure names all four arms it
/// had already rewritten before refusing on the fifth. The reason cannot be seen from any line of this
/// handler: the context a reseal lands in is <b>request-scoped</b>, so an applied reseal is a pending
/// <c>UPDATE</c> that outlives this method. A refusal on the fifth arm stops <em>this</em> save and
/// nothing more — the next thing in the request that saves anything flushes the four arms already
/// rewritten, which then carry new ciphertext under a stamp naming a chunk that was refused, and the
/// stamp is the one signal the destructive completion step trusts. <c>SaveCallCount == 0</c> is
/// therefore <em>true of the wrong shape as well</em>, which is why
/// <c>ResealRowsHandlerTests.ResealChunk_WhenTheLastArmNamesAnInvisibleRow_RefusesBeforeMutatingAnything</c>
/// reads the entities and not the save count. What that rule reaches is exactly the miss a lookup can
/// see; a refusal the <em>Domain</em> raises — the presence rule, an empty rotation identifier — still
/// happens mid-application, because the only way to provoke it is to call the reseal member that also
/// writes. That residue is bounded by the same rollback the unit of work already owes.
/// </para>
/// <para>
/// <b>TWO — DRIVE EACH ARM FROM THE COMMAND, NEVER FROM WHAT THE PORT RETURNED.</b>
/// <c>foreach (var loaded in accounts.Values)</c> is the shorter line, it reads fine, and it is the
/// defect: it re-seals every row the port happened to answer with, which on a query that lost its
/// identifier predicate is the whole budget. Each such row would need a value invented for it — every
/// reseal member takes the row's <em>new</em> value and there is no overload that writes a stamp alone,
/// which is what keeps the residue small — and the invented value is a claim the client never made,
/// under a stamp the completion step trusts. The adapter <em>is</em> expected to scope its query and
/// <c>ResealChunkTests.ResealChunk_LoadsOnlyTheRowsTheChunkNames</c> is what says it does; this is
/// defence in depth on the one path whose product is a stamp, and it is <em>held</em> rather than
/// assumed because <c>InMemoryNarrativeResealRepository.OverAnswers</c> makes a port over-answer and
/// one case turns it on.
/// </para>
/// <para>
/// <b>The presence rule is not restated here.</b> Whether a reseal may change what a nullable narrative
/// column holds belongs to <see cref="NarrativeReseal" />, and the five reseal members already route
/// through it — a copy in this handler could only agree redundantly or drift silently, which is the
/// argument that rule makes about itself.
/// </para>
/// <para>
/// <b>Five arms and no budget arm, and the sixth is refused rather than forgotten.</b> It would need
/// <c>rotation_id</c> on the <c>budgets</c> <c>GRANT UPDATE</c> column list, and FR-099 requires the
/// application role to hold <c>UPDATE</c> on <c>budgets.name</c> and on no other column.
/// <c>Budget.ResealName</c> is <see langword="internal" /> and <c>Domain.csproj</c> grants Domain's
/// internals to <c>Infrastructure</c> alone, so a sixth arm here is a compile error rather than a
/// runtime <c>42501</c>. Every budget row is nameless today and the completeness gate is
/// presence-aware, so none is ever outstanding. <c>docs/business-logic/key-rotation.md</c> argues it.
/// </para>
/// <para>
/// <b>No route reaches this handler yet</b>, so nothing a browser can do writes a <c>rotation_id</c>.
/// The route that carries a chunk owes the request shape, the <c>MaxChunkBytes</c> budget
/// <c>BeginKeyRotationHandler</c> publishes, and nothing this handler does not already hold.
/// </para>
/// </remarks>
public sealed class ResealRowsHandler(
    INarrativeResealRepository rows,
    IKeyRotationRepository keyRotations,
    IUserContext userContext,
    ITransactionalExecutor transactionalExecutor)
    : ICommandHandler<ResealRowsCommand>
{
    public Task HandleAsync(ResealRowsCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Read before the delegate is entered, for the reason the sibling handlers read their clock
        // there: the unit of work is replayed under a retrying execution strategy, and an accessor that
        // threw would then throw once per attempt for a request that never had an identity at all.
        Guid userId = userContext.UserId;

        return transactionalExecutor.ExecuteAsync(
            async token =>
            {
                // "IS THIS THE STAGED RUN?" IS ASKED, AND IT IS ASKED OF IKeyRotationRepository RATHER
                // THAN OF A READ SERVICE WRITTEN FOR IT. FindStagedRotationAsync's own doc says it
                // exists for exactly the steps after a begin — a chunk saying which run it is
                // continuing, and a completion saying which generation it is promoting — each of which
                // arrives holding an identifier to check the answer against.
                //
                // LEFT UNASKED, a caller stamps rows with an abandoned run's identifier, or with one no
                // key_rotations row has ever matched: the rotation identifier is minted by the client
                // and nothing about it makes it this account's run. The completeness gate at the far
                // end then answers COMPLETE for a generation nobody staged, and the promotion
                // overwrites the only wrapped copies of the key those rows are still sealed under.
                //
                // INSIDE THE DELEGATE, so the run this chunk is judged against and the rows it stamps
                // are read and written in one transaction. Outside it, a begin racing this request
                // could replace the staged generation between the check and the write, and the rows
                // would commit stamped with a run that is no longer in flight. It costs one SELECT on a
                // table keyed by user_id.
                KeyRotation? staged = await keyRotations.FindStagedRotationAsync(userId, token);

                // staged is null and staged.RotationId differing are ONE refusal on purpose. Splitting
                // them would put "this account has no rotation in flight" in a 400 body, which is a
                // fact about the account handed to a caller whose request was already wrong; and the
                // client's next act is the same either way, which is to begin a run and quote what it
                // staged.
                if (staged is null || staged.RotationId != command.RotationId)
                {
                    throw Refused(
                        nameof(ResealRowsCommand.RotationId),
                        "This chunk names a rotation that is not the one in flight. Begin a rotation, "
                        + "then send its chunks under the identifier that begin staged.");
                }

                // RULE ONE, FIRST HALF: EVERY ARM IS RESOLVED BEFORE ANY ARM IS MUTATED. Read the class
                // remarks before collapsing these ten statements into five — the shape that loads and
                // applies one arm at a time is the natural one and is the defect, and nothing below
                // this line can observe the difference.
                IReadOnlyList<(ResealedAccount Entry, Account Row)> accounts = await ResolveAsync(
                    command.Accounts, entry => entry.Id, rows.ListAccountsAsync, "Account", token);
                IReadOnlyList<(ResealedPayee Entry, Payee Row)> payees = await ResolveAsync(
                    command.Payees, entry => entry.Id, rows.ListPayeesAsync, "Payee", token);
                IReadOnlyList<(ResealedCategoryGroup Entry, CategoryGroup Row)> categoryGroups =
                    await ResolveAsync(
                        command.CategoryGroups,
                        entry => entry.Id,
                        rows.ListCategoryGroupsAsync,
                        "Category group",
                        token);
                IReadOnlyList<(ResealedCategory Entry, Category Row)> categories = await ResolveAsync(
                    command.Categories, entry => entry.Id, rows.ListCategoriesAsync, "Category", token);
                IReadOnlyList<(ResealedTransaction Entry, Transaction Row)> transactions =
                    await ResolveAsync(
                        command.Transactions,
                        entry => entry.Id,
                        rows.ListTransactionsAsync,
                        "Transaction",
                        token);

                // RULE ONE, SECOND HALF, AND RULE TWO. Every loop walks the resolved entries — which
                // are the COMMAND's, in the command's order, one per entry it carried — and never the
                // dictionary the port answered with.
                //
                // A replay of this delegate re-applies the same values under the same stamp to the same
                // tracked instances, which is why this handler holds no IPersistenceState: a second
                // pass over a row the first pass already rewrote agrees with itself on presence and
                // writes the bytes it wrote before. That convergence is a property of the values being
                // the client's rather than derived here, and it is the reason the command is the only
                // thing iterated.
                foreach ((ResealedAccount entry, Account row) in accounts)
                {
                    row.Reseal(entry.Name, command.RotationId);
                }

                foreach ((ResealedPayee entry, Payee row) in payees)
                {
                    row.Reseal(entry.Name, command.RotationId);
                }

                foreach ((ResealedCategoryGroup entry, CategoryGroup row) in categoryGroups)
                {
                    row.Reseal(entry.Name, entry.Description, command.RotationId);
                }

                foreach ((ResealedCategory entry, Category row) in categories)
                {
                    row.Reseal(entry.Name, entry.Description, command.RotationId);
                }

                foreach ((ResealedTransaction entry, Transaction row) in transactions)
                {
                    row.ResealDescription(entry.Description, command.RotationId);
                }

                // ONE SAVE FOR ALL FIVE ARMS, AND IT IS INSIDE THE UNIT OF WORK. A handler that entered
                // the executor, did nothing in the delegate and saved afterwards satisfies every count
                // a test can take — the rows moved, the save happened once — while being wrong in the
                // one way that matters here: a write outside the transaction is a write nothing rolls
                // back, and on this path that is an account left part under one content key and part
                // under the next, every half stamped as though the whole run had rewritten it.
                await rows.SaveAsync(token);
            },
            cancellationToken);
    }

    /// <summary>
    /// Loads the rows one arm names and pairs each entry with the row it re-seals, refusing an entry no
    /// read of this budget can answer for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It mutates nothing, which is the whole of why it is separate from the loops that do.</b> Every
    /// arm passes through here before any arm is applied — see the class remarks for what that buys and
    /// why no assertion about the database can see it.
    /// </para>
    /// <para>
    /// <b><see cref="NotFoundException" /> rather than a validation error, because the row is
    /// <em>invisible</em> and not forbidden.</b> The five sets carry the <c>BudgetIsolation</c> query
    /// filter, so a row of another budget is missing from the answer rather than refused. A caller that
    /// read a missing key as "nothing to do for this one" would answer success to a chunk that re-sealed
    /// nothing, after which the client counts those rows as done and the completeness gate refuses a run
    /// the client believes it finished.
    /// </para>
    /// <para>
    /// <b>An empty arm asks the port nothing.</b> A rotation is chunked, so most chunks carry rows in
    /// one or two arms and a call per empty arm would be four round trips per request that answer
    /// nothing. The skip is not a guard against a broken port: an empty identifier list is a perfectly
    /// well-formed read, and a port that answered rows for one would be caught by the loops above
    /// iterating the command rather than the answer.
    /// </para>
    /// <para>
    /// <b>A null arm is tolerated the way <c>BeginKeyRotationHandler</c> tolerates a missing seal
    /// array.</b> The declaration says the list is present, and a missing JSON array arrives as
    /// <see langword="null" /> anyway on the day a route reaches this handler — reading it as "this
    /// chunk named no rows of that kind" is what that request means, and the alternative is a
    /// <see cref="NullReferenceException" /> rendered as a 500.
    /// </para>
    /// </remarks>
    private static async Task<IReadOnlyList<(TEntry Entry, TRow Row)>> ResolveAsync<TEntry, TRow>(
        IReadOnlyList<TEntry>? entries,
        Func<TEntry, Guid> identify,
        Func<IReadOnlyList<Guid>, CancellationToken, Task<IReadOnlyDictionary<Guid, TRow>>> list,
        string subject,
        CancellationToken cancellationToken)
        where TRow : class
    {
        IReadOnlyList<TEntry> named = entries ?? [];

        if (named.Count == 0)
        {
            return [];
        }

        Guid[] ids = [.. named.Select(identify)];
        IReadOnlyDictionary<Guid, TRow> loaded = await list(ids, cancellationToken);

        List<(TEntry Entry, TRow Row)> resolved = new(named.Count);

        foreach (TEntry entry in named)
        {
            if (!loaded.TryGetValue(identify(entry), out TRow? row))
            {
                // NO IDENTIFIER IN THE MESSAGE, AND THE MECHANISM IS MEASURED RATHER THAN ASSUMED:
                // NotFoundExceptionHandler copies Message verbatim into ProblemDetails.Detail and
                // contains no environment check of any kind, so what is written here is what a
                // Production client reads. Naming the row would confirm to a caller that reached into
                // another budget precisely which of its guesses landed nowhere — the enumeration this
                // server refuses to be elsewhere. The spelling is the house one, RenamePayeeHandler's
                // and its siblings'.
                throw new NotFoundException($"{subject} was not found.");
            }

            resolved.Add((entry, row));
        }

        return resolved;
    }

    // Domain.Common.ValidationException by name, because both layers declare one and only that one is
    // what ValidationExceptionHandler turns into a 400 with the field errors on it. Keyed on the member
    // the caller can correct, the shape BeginKeyRotationHandler, RevokePasskeyHandler and
    // GenerateRecoveryCodesHandler raise their own refusals through.
    //
    // THE MESSAGE IS RENDERED INTO THE RESPONSE BODY UNCONDITIONALLY, which is why it names no
    // identifier and no count drawn from what the server holds. ValidationExceptionHandler sets 400 and
    // copies Errors straight into a ValidationProblemDetails with no environment check anywhere in it,
    // so what is written at the call site is what a Production client reads.
    private static ValidationException Refused(string field, string message) =>
        new(new Dictionary<string, string[]> { [field] = [message] });
}
