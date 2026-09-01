using Domain.Common;
using Domain.Payees;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.Repositories;

/// <summary>
/// The payee writes this server can still perform, which no longer include finding one by name.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>FindByNameAsync</c> and the <c>GetOrCreateAsync</c> built on it are deleted, and their absence is
/// the load-bearing change of this whole slice.</b> A lookup by name is not a member somebody has yet to
/// write here — it is a question nothing on this side can answer. <c>payees.name</c> holds an AEAD
/// envelope drawn under a fresh nonce every time, so two seals of one name are different bytes and an
/// equality comparison over the column finds nothing; the digest that <em>is</em> stable is taken under
/// the account's index key, which lives in a browser this server never sees. And the case folding the old
/// lookup leant on left with the column's <c>case_insensitive</c> collation, because <c>bytea</c> is not
/// collatable.
/// </para>
/// <para>
/// <b>A future reader must not restore either member.</b> Any restoration could only compare ciphertext
/// against ciphertext, which answers "no such payee" for a payee that is sitting in the table — and it
/// would answer it to a create path, so the row would be inserted a second time and refused by
/// <see cref="PayeeConfiguration.NameIndexName"/> with the caller believing it had checked. What replaced
/// find-or-create is that the client, which already holds the decrypted list and the index key, resolves
/// the name locally and posts <c>POST /api/payees</c> for a payee it decided is new — the request that
/// reaches <see cref="AddAsync"/> below; the 409 it can answer with is what tells the client its list was
/// stale.
/// </para>
/// </remarks>
public sealed class PayeeRepository(BudgetoidDbContext dbContext) : IPayeeRepository
{
    /// <summary>
    /// Inserts a payee the caller minted, sealed and indexed, translating a collision on this budget's
    /// payee-name index into a conflict the caller is asked to resolve by re-reading its list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This member answers 409 and <see cref="UpdateAsync"/> answers 400 on the very same index, and
    /// the asymmetry is a decision rather than an oversight.</b> One constraint, one table, two statuses —
    /// a reviewer will find that and try to collapse it, so the argument is written at both members.
    /// </para>
    /// <para>
    /// What differs is the REMEDY, not the constraint. A create that collides means a payee already
    /// carries this name in this budget and the client's list was stale; the resolution is to adopt the
    /// row that already exists, which is not something a person can correct by editing a field, so there
    /// is no member for a validation problem document to be keyed on. A rename that collides means a
    /// person chose a name another row holds, and the resolution is to choose a different one — a
    /// statement about <c>Name</c> in the request, which is exactly what a 400 carries and a bare 409 has
    /// nowhere to put.
    /// </para>
    /// <para>
    /// Both readings are wrong in the corner cases — a rename can lose a race between two tabs, and a
    /// create can be a person deliberately making a second payee — and each status follows its dominant
    /// case. If this is ever overruled, the fallback that keeps one status is 409 on both, and its cost is
    /// that the rename loses the field-keyed 400.
    /// </para>
    /// <para>
    /// <b>A collision on <see cref="PayeeConfiguration.PrimaryKeyName"/> is a conflict too, and a
    /// different one.</b> It used to be left uncaught, which answered 500 for the most ordinary thing an
    /// HTTP client does: the id arrives minted by the caller, so a POST retried after a network timeout
    /// carries a byte-identical body and lands on the key rather than on the name index. Measured on
    /// postgres:17.10, a row violating both is reported under the key — see that constant for the
    /// ordering probe.
    /// </para>
    /// <para>
    /// <b>The two 409s must not share a sentence.</b> A duplicate name says somebody already holds this
    /// name and the remedy is to adopt the row that already exists; a duplicate id says nothing about
    /// names at all, and the row wearing that id may hold a different one — or, in a budget the caller
    /// cannot read, no name it will ever see. Sending that caller to re-read its payee list would send it
    /// looking for a name that is not there. <c>ConflictExceptionHandler</c> renders the message as the
    /// whole of <c>ProblemDetails.Detail</c> beside a title fixed for every conflict in the product, so
    /// the sentence is the only place the difference can live.
    /// </para>
    /// <para>
    /// <b>The route stays non-idempotent, deliberately.</b> Answering 200 with the row that already
    /// exists would mean reading it back and deciding whether it is the same payee — a comparison over
    /// AEAD envelopes this server cannot open, so it could only compare a blind index, and it would still
    /// have to choose an answer for the case where the id matches and the index does not. That is a
    /// decision with its own failure modes and it is not this one. A 409 that says what happened is
    /// honest and costs the client one <c>GET /api/payees/{id}</c>.
    /// </para>
    /// </remarks>
    public async Task AddAsync(Payee payee, CancellationToken cancellationToken = default)
    {
        dbContext.Payees.Add(payee);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        // FIRST because it is the one PostgreSQL reports first, not because the runtime cares: the two
        // filters are mutually exclusive — a PostgresException carries exactly one ConstraintName — so
        // this order is documentation of the measurement, and reversing it changes no behaviour.
        //
        // Named, and never on SQLSTATE alone, for the reason the name-index arm below is named, plus one
        // that is sharper here: this arm and that one raise the SAME SQLSTATE from the same statement, so
        // SQLSTATE alone cannot tell an id collision from a name collision and whichever sentence was
        // written first would be told to both.
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: PayeeConfiguration.PrimaryKeyName,
        })
        {
            // Detach for the reason the arm below detaches: the failed (Added) state must not leak into a
            // later SaveChanges if the context were reused.
            dbContext.Entry(payee).State = EntityState.Detached;
            throw DuplicatePayeeIdConflictException();
        }
        // Named, and never on SQLSTATE alone: SaveChanges flushes every tracked row and not just this
        // payee, so only this index says the blind index the client just computed is the one already
        // taken. Matched on SQLSTATE alone, a stranger's unique violation would wear the payee's sentence
        // and send a client off to re-read a list that has nothing to do with the failure.
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: PayeeConfiguration.NameIndexName,
        })
        {
            // Detach the rejected entity so the failed (Added) state can't leak into a later SaveChanges
            // if the context were reused, mirroring UpdateAsync's detach-on-conflict.
            dbContext.Entry(payee).State = EntityState.Detached;
            throw DuplicatePayeeConflictException();
        }
    }

    public Task<Payee?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // Use the filtered DbSet, not Find/FindAsync: Find can return a tracked entity while
        // bypassing global query filters, which would let one budget reach another budget's payee.
        return dbContext.Payees.FirstOrDefaultAsync(payee => payee.Id == id, cancellationToken);
    }

    /// <summary>
    /// Flushes a rename, translating a collision on the same index <see cref="AddAsync"/> watches into a
    /// 400 keyed on the name.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately a different status from <see cref="AddAsync"/>'s, on one index and one table.</b>
    /// The full argument is written there and is not repeated: in short, a rename's remedy is a different
    /// name — a correction to a field of the request, which is what a validation problem document exists
    /// to carry — while a create's remedy is to adopt the row that already exists, which is not a field
    /// correction at all. Do not "harmonise" the two.
    /// </remarks>
    public async Task UpdateAsync(Payee payee, CancellationToken cancellationToken = default)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        // The same index AddAsync catches, named for the same reason.
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: PayeeConfiguration.NameIndexName,
        })
        {
            // Detach the rejected entity so the failed (Modified) state can't leak into a later
            // SaveChanges if the context were reused, mirroring AddAsync's detach-on-conflict.
            dbContext.Entry(payee).State = EntityState.Detached;
            throw DuplicateNameValidationException();
        }
    }

    // ConflictExceptionHandler renders this message as ProblemDetails.Detail beside a Title fixed for
    // every conflict in the product ("The request conflicts with the current state of the resource."),
    // and adds no extension member, so this sentence is the whole of what distinguishes this 409 from
    // any other and has to say what the caller does next by itself.
    //
    // It deliberately does NOT name the existing payee's id: the handler is shared, so there is nowhere
    // to put one, and a client must decrypt the list to confirm the row is the one it meant regardless.
    // It also carries no SQLSTATE, constraint name or database text — a caller learns what to do and
    // nothing about the schema that refused it.
    private static ConflictException DuplicatePayeeConflictException() => new(
        "A payee with this name already exists in this budget. "
        + "Re-read the payee list and use the payee it already holds.");

    // The OTHER conflict this table can raise, and everything the sentence above says about the shared
    // handler applies here: one Title for every 409 in the product, no extension member, so this string
    // is the whole of what the caller is told.
    //
    // It deliberately does not say "re-read your payee list", which is the neighbouring sentence and the
    // wrong instruction: the row already wearing this id may carry a different name, so a client sent to
    // its list would look for a name that is not on it. What it does instead is name the two readings the
    // server genuinely cannot tell apart — a retry that already succeeded, and an identifier reused by
    // mistake — and give each its own next step, because the client CAN tell them apart: it knows whether
    // it sent this body before.
    //
    // "Read it back" is deliberately not "it is saved": the id is scoped to the whole table while
    // GET /api/payees/{id} is scoped to the ambient budget, so a collision with a payee in some other
    // budget answers 404 on the read-back. Phrasing it as an instruction rather than a promise keeps the
    // sentence true in that case, where the honest conclusion is the second reading — mint a fresh id.
    //
    // Carries no SQLSTATE, constraint name or database text, matching its neighbour: a caller learns what
    // to do and nothing about the schema that refused it.
    private static ConflictException DuplicatePayeeIdConflictException() => new(
        "A payee already exists with this identifier. If this request is a retry, read that payee back by "
        + "its identifier instead of posting it again; otherwise mint a fresh identifier and post again.");

    private static ValidationException DuplicateNameValidationException() => new(new Dictionary<string, string[]>
    {
        [nameof(Payee.Name)] = ["Payee name must be unique."],
    });
}
