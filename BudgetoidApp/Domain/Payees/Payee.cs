using Domain.Common;
using Domain.Security;

namespace Domain.Payees;

public sealed class Payee
{
    private Payee()
    {
    }

    public Guid Id { get; private set; }
    public Guid BudgetId { get; private set; }

    /// <summary>
    /// The sealed name of the counterparty this payee stands for — the AEAD envelope over text this
    /// server has never seen and holds no key for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A <see cref="NarrativeField"/> and not a <see cref="string"/>, which is what makes "this server
    /// never sees a payee name" a build property rather than a review one.</b> The type has no
    /// constructor, factory or conversion taking a <see cref="string"/>, so writing plaintext into this
    /// column does not compile. What that forecloses on <em>this</em> table is a particular disclosure:
    /// a payee list is the set of counterparties one person deals with — a landlord, a pharmacy, a
    /// clinic, an employer — and it is legible without a single amount beside it. It is also the
    /// narrative column with the fewest distinct values per budget, so a plaintext one would be the
    /// easiest of the eight to read at a glance.
    /// </para>
    /// <para>
    /// <b>It is written only with <see cref="NameKey"/>, never alone.</b> Both members are assigned from
    /// one <see cref="IndexedName"/> parameter, at the two places below and nowhere else; see
    /// <see cref="Rename"/> for what a member taking a bare <see cref="NarrativeField"/> would cost here,
    /// which is more than it costs anywhere else in the product.
    /// </para>
    /// </remarks>
    public NarrativeField Name { get; private set; } = null!;

    /// <summary>
    /// The blind index over the same name: <c>HMAC-SHA-256</c> under the account's index key, computed by
    /// the client over the normalised text and equal across every row holding that name in this column of
    /// this account. It is what <c>IX_payees_budget_id_name_key</c> enforces uniqueness over.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why it is a property of its own rather than the whole <see cref="IndexedName"/>, and why
    /// <see cref="ReadOnlyMemory{T}"/> rather than <see cref="byte"/><c>[]</c>, are settled at
    /// <see cref="Accounts.Account.NameKey"/> and are not restated here.</b> Neither answer is about
    /// payees; a second copy of them is a second thing to correct the day either changes.
    /// </para>
    /// <para>
    /// <b>What is specific to this column is what the uniqueness it carries <em>means</em>.</b> On
    /// accounts the unique index is a convenience — two accounts under one name are confusing, and
    /// nothing else. Here it is the whole of counterparty deduplication: one index value per budget is
    /// what makes a payee list a list of counterparties rather than a list of the times somebody typed
    /// one. The server used to hold that property itself, by folding a name's case and re-reading the
    /// table; it can no longer do either, so the property now rests entirely on this column and on the
    /// client computing the same digest for the same name every time.
    /// </para>
    /// </remarks>
    public ReadOnlyMemory<byte> NameKey { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>
    /// Creates a payee from an identifier the caller minted and a name the caller sealed and indexed.
    /// </summary>
    /// <param name="id">
    /// The row's identifier, chosen by whoever sealed <paramref name="name"/> — never minted here. See
    /// the note below on why this factory has no minting overload.
    /// </param>
    /// <param name="budgetId">The budget that owns the payee.</param>
    /// <param name="name">The sealed name and its blind index, already judged by <see cref="IndexedName"/>.</param>
    /// <param name="createdAtUtc">The creation instant, in UTC.</param>
    /// <remarks>
    /// <para>
    /// <b><see cref="Guid.CreateVersion7"/> has left this file entirely, and not merely moved behind an
    /// overload.</b> The identifier is the associated data the client sealed the name against, so a row
    /// whose id was minted here holds a name nobody can ever open — every constraint satisfied, nothing
    /// red, and the symptom arriving whenever somebody first tries to read the list. A minting overload
    /// would let a caller that simply forgot to thread the id through compile and pass, so deleting the
    /// minting path is the whole of the protection: it forces such a caller to <em>name</em> the
    /// identifier it invents, on a line a reviewer reads in the diff. Nothing here can tell a good id
    /// from a wrong one.
    /// </para>
    /// <para>
    /// <b>That argument bites harder on payees than it did on accounts, because this is the one entity
    /// whose rows used to be born on the server.</b> A find-or-create path minted ids by the dozen, one
    /// per counterparty a person named while entering transactions, and never returned any of them to
    /// the caller. A surviving overload here would look exactly like the member that path called.
    /// </para>
    /// </remarks>
    public static Payee Create(Guid id, Guid budgetId, IndexedName name, DateTime createdAtUtc)
    {
        // Ahead of ValidateOrThrow rather than folded into it, because a missing name is not a field
        // error a caller corrects by editing a request - this signature says a name is present, so a null
        // is a defect in this codebase. ValidationException would report it as a 400 about a member the
        // request may not even have.
        ArgumentNullException.ThrowIfNull(name);

        ValidateOrThrow(id, budgetId);

        return new Payee
        {
            Id = id,
            BudgetId = budgetId,
            Name = name.Name,
            NameKey = name.BlindIndex,
            CreatedAtUtc = createdAtUtc,
        };
    }

    /// <summary>
    /// Replaces both halves of the payee's name. It is the only thing about a payee that ever changes.
    /// </summary>
    /// <param name="name">The new sealed name and the index computed over the same text.</param>
    /// <remarks>
    /// <para>
    /// <b>It takes one <see cref="IndexedName"/>, and that parameter type is the whole of what makes
    /// "both halves of the name move together" a fact rather than a habit.</b> The ciphertext and the
    /// index are two columns and two properties, so nothing about the storage stops a member from
    /// writing one; what stops it is that neither this member nor <see cref="Create"/> offers a spelling
    /// for half a name — no bare <see cref="NarrativeField"/> overload, and no two-parameter pair a
    /// caller could fill from two places.
    /// </para>
    /// <para>
    /// <b>A later member taking a bare <see cref="NarrativeField"/> is therefore the mistake to refuse in
    /// review, and on this table it costs more than the same mistake costs anywhere else.</b> The row
    /// would hold new ciphertext under the previous name's index. On accounts that is bad and silent and
    /// that is all, because nothing looks an account up by name. Here the payee list <em>is</em> the
    /// deduplication mechanism of the domain, so the same row produces two further failures:
    /// </para>
    /// <para>
    /// Creating the <b>new</b> name afterwards is accepted, because its index is still free — so the
    /// budget gains a second payee for one counterparty, which is precisely the duplication find-or-create
    /// existed to prevent, arriving by the path meant to fix a typo. And creating the <b>old</b> name is
    /// refused, pointing at a payee that no longer has it: the client re-reads the list, decrypts every
    /// name, finds no match, and has nowhere to go — a name the person can neither create nor find.
    /// Nothing on this side can notice either, because recomputing a digest needs the budget's index key,
    /// which lives in a browser.
    /// </para>
    /// <para>
    /// <b>It does not call <see cref="ValidateOrThrow"/>, and the asymmetry with
    /// <see cref="Accounts.Account.Update"/> is deliberate.</b> That member still has a kind, a balance
    /// and a currency to judge on every write. Once every name rule moved to the client, a payee's only
    /// remaining rules are <see cref="Id"/> and <see cref="BudgetId"/> — both already on the entity when
    /// this member runs, both written once and never again, and neither reachable as empty on a row that
    /// materialised out of the database. A call here would re-examine two values this member cannot
    /// change and could not have been handed: a no-op that reads as protection, which is worse than no
    /// call, because the next reader trusts it to be doing something. Do not add one back for symmetry.
    /// </para>
    /// </remarks>
    public void Rename(IndexedName name)
    {
        // Both assignments sit below the refusal, so a refused rename replaces both halves or neither.
        // What this line buys is the report, not that property: an ArgumentNullException naming the
        // parameter instead of a NullReferenceException out of the line below.
        //
        // It is NOT what makes half a name unspellable here, and the ordering of the two lines below is
        // convention rather than a guard. Measured, with this line deleted and a null passed, `name.Name`
        // throws on the dereference before either assignment runs, so neither order can produce an entity
        // whose two columns describe two different names. What forecloses that state is the IndexedName
        // parameter type - no bare NarrativeField overload and no two-parameter pair - which is the
        // argument the remarks above make.
        ArgumentNullException.ThrowIfNull(name);

        Name = name.Name;
        NameKey = name.BlindIndex;
    }

    /// <summary>
    /// Runs the two rules this entity still owns — the identifier and the tenancy — for the one path
    /// that writes them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every name rule is gone, and nothing replaced it.</b> There is no length to measure, no
    /// blankness to detect and no whitespace to trim: the value arriving at <see cref="Create"/> is an
    /// AEAD envelope over text this server has never seen and holds no key for, and
    /// <see cref="IndexedName"/> has already judged the only things that are judgeable about the pair —
    /// the framing, the cap, and the index's width.
    /// </para>
    /// <para>
    /// <b>Said plainly, because it is a capability that moved rather than a rule that was quietly
    /// dropped: this server can no longer refuse a blank or over-long payee name.</b> "A name is not just
    /// spaces", and the 200-character ceiling with it, are now the client's to enforce before it seals. A
    /// future reader who notices the absence must not restore a server-side check — there is nothing here
    /// to check it against, and the only honest count left is a count of envelope bytes, which
    /// <see cref="NarrativeFieldLimits.NameBytes"/> already caps — and must not read the absence as an
    /// oversight.
    /// </para>
    /// <para>
    /// <b>The case-folding rule left with them, and that one has no client-side twin worth pretending
    /// otherwise about.</b> "Starbucks" and "starbucks" used to be one payee because a case-insensitive
    /// collation on the column said so. They are one payee now only if the client folds the text the same
    /// way before it computes <see cref="Payee.NameKey"/>, over bytes this side cannot inspect.
    /// </para>
    /// </remarks>
    private static void ValidateOrThrow(Guid id, Guid budgetId)
    {
        var errors = new Dictionary<string, string[]>();

        // The identifier is now supplied rather than minted, so the empty Guid is reachable for the first
        // time - it is what a caller that threaded a default through hands over. Refused here rather than
        // left to the primary key, which accepts it: all-zero is a legal uuid, so the first such row
        // stores and the second collides under a constraint name that says nothing about the caller that
        // never chose an id at all.
        if (id == Guid.Empty)
        {
            errors[nameof(Id)] = ["Payee id is required."];
        }

        if (budgetId == Guid.Empty)
        {
            errors[nameof(BudgetId)] = ["Budget id is required."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }
    }
}
