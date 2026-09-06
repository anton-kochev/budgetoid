using Domain.Common;
using Domain.Security;

namespace Domain.Categories;

public sealed class Category
{
    private Category()
    {
    }

    public Guid Id { get; private set; }
    public Guid BudgetId { get; private set; }
    public Guid CategoryGroupId { get; private set; }

    /// <summary>
    /// The sealed name of the category — the AEAD envelope over text this server has never seen and
    /// holds no key for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A <see cref="NarrativeField"/> and not a <see cref="string"/>, which is what makes "this server
    /// never sees a category name" a build property rather than a review one.</b> The type has no
    /// constructor, factory or conversion taking a <see cref="string"/>, so writing plaintext into this
    /// column does not compile. The argument for the type is <see cref="Payees.Payee.Name"/>'s and is not
    /// restated; what is specific here is that a category name is the finest label in a person's ledger —
    /// "Therapy", "Legal fees", "Child support" — so the list of them is a description of a life read
    /// straight off, with no amount needed beside it.
    /// </para>
    /// <para>
    /// <b>It is written only with <see cref="NameKey"/>, never alone.</b> Both members are assigned from
    /// one <see cref="IndexedName"/> parameter, at the two places below and nowhere else; see
    /// <see cref="Update"/> for what a member taking a bare <see cref="NarrativeField"/> would cost.
    /// </para>
    /// </remarks>
    public NarrativeField Name { get; private set; } = null!;

    /// <summary>
    /// The blind index over the same name: <c>HMAC-SHA-256</c> under the account's index key, computed by
    /// the client over the normalised text and equal across every row holding that name in this column of
    /// this budget — the key is the account's, but the message the client folds names the budget, so two
    /// budgets of one account key one label differently. It is what
    /// <c>IX_categories_budget_id_name_key</c> enforces uniqueness over.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why it is a property of its own rather than the whole <see cref="IndexedName"/>, and why
    /// <see cref="ReadOnlyMemory{T}"/> rather than <see cref="byte"/><c>[]</c>, are settled at
    /// <see cref="Accounts.Account.NameKey"/> and are not restated here.</b> The type is held by review
    /// and by nothing else, which is worth saying because it looks like a preference: swap it for a
    /// <see cref="byte"/><c>[]</c> and, once the value converter in
    /// <c>CategoryConfiguration</c> goes with it, every caller and every assertion in the product still
    /// compiles — measured — because <c>.ToArray()</c> resolves through LINQ on an array. What would be
    /// gone is the guarantee that this property cannot be handed a buffer its caller still holds a
    /// reference to.
    /// </para>
    /// <para>
    /// What this column carries is the accounts reading rather than the payees one: two categories under
    /// one name are a confusion a person can see and fix, not a deduplication mechanism the domain rests
    /// on. Nothing looks a category up by name — no read path in the product orders or matches on it — so
    /// the index's whole job is to refuse the second row. Budget-scoped and not group-scoped, which is
    /// what the retired plaintext index was too: two groups may not each hold a category of one name.
    /// </para>
    /// </remarks>
    public ReadOnlyMemory<byte> NameKey { get; private set; }

    /// <summary>
    /// The sealed note filed against the category, or <see langword="null"/> where the category has none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It carries no blind index and never will.</b> An index answers "which row holds this name". A
    /// description is not looked up, is not unique and is not a name, so a <c>description_key</c> would
    /// publish a deterministic per-account fingerprint of somebody's free text with nothing on the other
    /// side asking for it. That is a decision, not a gap to close later.
    /// </para>
    /// <para>
    /// <b>Its cap is <see cref="NarrativeFieldLimits.DescriptionBytes"/> and not
    /// <see cref="NarrativeFieldLimits.NameBytes"/>, and the two are field <em>classes</em> rather than
    /// two guesses at one number.</b> A helper that sealed a description under the name's ceiling would
    /// refuse values this column accepts.
    /// </para>
    /// <para>
    /// <b>A LOST DESCRIPTION IS INVISIBLE WHERE A LOST NAME IS <c>23502</c>.</b> The column is nullable,
    /// so a write path that decoded a description and then forgot to assign it writes
    /// <see langword="null"/>: a legal row, violating no constraint, byte-identical to one belonging to
    /// somebody who deliberately filed no note. On the <c>NOT NULL</c> name the same omission is refused
    /// by the database. Nothing in the schema can tell a bug from an operation here, so what holds it is
    /// that every write path is covered by a case that reads a <em>non-null</em> description back — never
    /// one asserting the member is merely present.
    /// </para>
    /// <para>
    /// <b>"Cleared" and "never filled" are two rows and must stay two.</b> An empty note seals to exactly
    /// <see cref="CiphertextEnvelope.MinimumLength"/> bytes — AES-GCM ciphertext is the length of its
    /// plaintext — while a note nobody wrote is <see langword="null"/>. The deleted
    /// <c>NormalizeDescription</c> mapped a whitespace-only description onto <see langword="null"/>,
    /// which is exactly that fold; it left with the <see cref="string"/> parameter and cannot come back
    /// in any form — not as a helper, not as an inlined ternary, not as a <c>?? null</c> — because there
    /// is no text on this side to inspect.
    /// </para>
    /// </remarks>
    public NarrativeField? Description { get; private set; }

    public int Position { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>
    /// Creates a category from an identifier the caller minted, a name the caller sealed and indexed, and
    /// a note the caller sealed or did not file at all.
    /// </summary>
    /// <param name="id">
    /// The row's identifier, chosen by whoever sealed <paramref name="name"/> and
    /// <paramref name="description"/> — never minted here. See the note below on why this factory has no
    /// minting overload.
    /// </param>
    /// <param name="budgetId">The budget that owns the category.</param>
    /// <param name="categoryGroupId">The group the category is filed under.</param>
    /// <param name="name">The sealed name and its blind index, already judged by <see cref="IndexedName"/>.</param>
    /// <param name="description">The sealed note, or <see langword="null"/> for a category that has none.</param>
    /// <param name="position">Where the category sits in the person's own ordering within its group.</param>
    /// <param name="createdAtUtc">The creation instant, in UTC.</param>
    /// <remarks>
    /// <para>
    /// <b><see cref="Guid.CreateVersion7()"/> has left this file entirely, and not merely moved behind an
    /// overload.</b> The identifier is the associated data the client sealed <em>both</em> narrative
    /// members against, so a row whose id was minted here holds a name and a note nobody can ever open —
    /// every constraint satisfied, nothing red, and the symptom arriving whenever somebody first tries to
    /// read the list. A minting overload would let a caller that simply forgot to thread the id through
    /// compile and pass, so deleting the minting path is the whole of the protection: it forces such a
    /// caller to <em>name</em> the identifier it invents, on a line a reviewer reads in the diff. Nothing
    /// here can tell a good id from a wrong one.
    /// </para>
    /// <para>
    /// <b>The description takes no null refusal and the name does, because their parameters say different
    /// things.</b> <paramref name="description"/> is declared nullable, so absence is an ordinary value
    /// this signature invites; <paramref name="name"/> is not, so a null there is a defect in this
    /// codebase rather than a field a caller corrects by editing a request.
    /// </para>
    /// </remarks>
    public static Category Create(
        Guid id,
        Guid budgetId,
        Guid categoryGroupId,
        IndexedName name,
        NarrativeField? description,
        int position,
        DateTime createdAtUtc)
    {
        // Ahead of ValidateOrThrow rather than folded into it, because a missing name is not a field
        // error a caller corrects by editing a request - this signature says a name is present, so a null
        // is a defect in this codebase. ValidationException would report it as a 400 about a member the
        // request may not even have, and the real fault would never surface.
        ArgumentNullException.ThrowIfNull(name);

        ValidateOrThrow(id, budgetId, categoryGroupId, position);

        return new Category
        {
            Id = id,
            BudgetId = budgetId,
            CategoryGroupId = categoryGroupId,
            Name = name.Name,
            NameKey = name.BlindIndex,

            // Assigned, not normalised. There is no text here to trim and no blankness to detect: an
            // envelope over an empty string is a note somebody emptied and null is a note nobody wrote,
            // and this line is where the difference between them survives into the row.
            Description = description,
            Position = position,
            CreatedAtUtc = createdAtUtc,
        };
    }

    /// <summary>
    /// Replaces both halves of the category's name and the note beside it. It is the whole of what a
    /// <c>PUT</c> on this resource changes.
    /// </summary>
    /// <param name="name">The new sealed name and the index computed over the same text.</param>
    /// <param name="description">The new sealed note, or <see langword="null"/> to leave the category with none.</param>
    /// <remarks>
    /// <para>
    /// <b>It takes one <see cref="IndexedName"/>, and that parameter type is the whole of what makes
    /// "both halves of the name move together" a fact rather than a habit.</b> The ciphertext and the
    /// index are two columns and two properties, so nothing about the storage stops a member from writing
    /// one; what stops it is that neither this member nor <see cref="Create"/> offers a spelling for half
    /// a name. A later member taking a bare <see cref="NarrativeField"/> is the mistake to refuse in
    /// review: the row would hold new ciphertext under the previous name's index, and
    /// <c>IX_categories_budget_id_name_key</c> would go on guarding a name the row no longer holds while
    /// leaving the one it does hold free for a second category to take. Nothing on this side can notice —
    /// recomputing a digest needs the account's index key, which lives in a browser.
    /// </para>
    /// <para>
    /// <b><paramref name="description"/> is a second parameter rather than a third member of the pair,
    /// and its separateness is correct.</b> <see cref="IndexedName"/> exists because a name is two
    /// columns computed from one piece of text; a description is one column computed from another piece
    /// of text and has no index to be half of.
    /// </para>
    /// <para>
    /// <b>An absent description CLEARS the one the category held, because the route is a <c>PUT</c>.</b>
    /// A full replacement has no "leave it alone" state, so <c>Optional&lt;T&gt;</c> has no business on
    /// this path — the transaction routes carry it because they are <c>PATCH</c> and genuinely have a
    /// third state.
    /// </para>
    /// <para>
    /// <b>It calls <see cref="ValidateOrThrow"/> and NO TEST HOLDS THAT — the choice is held by this
    /// paragraph and by review.</b> Saying which shape is in the file would take a category whose
    /// position, budget id, group id or identifier is already bad at the moment this member runs, and
    /// there is no way to build one: <see cref="Create"/> refuses each of them and <see cref="Place"/>
    /// refuses a negative position and an empty group id, so every reachable instance has already
    /// satisfied the rules this call re-checks. That was measured on payees in the identical shape — the
    /// call and its absence were invisible to the suite in both directions. Do not add a comment claiming
    /// a test covers it, and do not delete the call for symmetry with <see cref="Payees.Payee.Rename"/>,
    /// which deliberately does not make it because a payee has no <see cref="Position"/> to examine.
    /// </para>
    /// </remarks>
    public void Update(IndexedName name, NarrativeField? description)
    {
        // All three assignments sit below the refusal, so a refused update replaces every column or none.
        // What this line buys is the REPORT, not that property: an ArgumentNullException naming the
        // parameter instead of a NullReferenceException out of the line below.
        //
        // It is NOT what makes half a name unspellable here - the IndexedName parameter type is, for the
        // reason the remarks give. What the ordering DOES buy is the description: an implementation that
        // assigned it first and only then dereferenced the name would leave a category with a cleared
        // note and its old name, which is a reachable state and the one the refusal case reads all three
        // columns back to rule out.
        ArgumentNullException.ThrowIfNull(name);

        ValidateOrThrow(Id, BudgetId, CategoryGroupId, Position);

        Name = name.Name;
        NameKey = name.BlindIndex;
        Description = description;
    }

    /// <summary>
    /// Moves the category into <paramref name="categoryGroupId"/> at <paramref name="position"/>.
    /// </summary>
    /// <remarks>
    /// <b>Untouched by the sealing, and it is the THIRD write path on this entity — the only one whose
    /// signature mentions no narrative value at all.</b> A group id and a position are values the server
    /// still reads, so nothing here surrendered a capability. What that costs is that a <c>Place</c> which
    /// reset a name or a note would go unnoticed on the nullable description: a legal row, and no other
    /// case in the suite reads a note back across a move.
    /// </remarks>
    public void Place(Guid categoryGroupId, int position)
    {
        var errors = new Dictionary<string, string[]>();

        if (categoryGroupId == Guid.Empty)
        {
            errors[nameof(CategoryGroupId)] = ["Category group id is required."];
        }

        if (position < 0)
        {
            errors[nameof(Position)] = ["Position must be zero or greater."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        CategoryGroupId = categoryGroupId;
        Position = position;
    }

    /// <summary>
    /// Runs the four rules this entity still owns — the identifier, the tenancy, the group and the
    /// ordering — for the two paths that write them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every name rule and every description rule is gone, and nothing replaced them.</b> There is no
    /// length to measure, no blankness to detect and no whitespace to trim: the values arriving at
    /// <see cref="Create"/> are AEAD envelopes over text this server has never seen and holds no key for,
    /// and <see cref="IndexedName"/> and <see cref="NarrativeField"/> have already judged the only things
    /// that are judgeable about them — the framing, the cap, and the index's width.
    /// </para>
    /// <para>
    /// <b>Said plainly, because these are capabilities that moved rather than rules quietly dropped: this
    /// server can no longer refuse a blank or over-long category name, nor an over-long description.</b>
    /// The two-hundred-character ceiling on the name, the five-hundred-character one on the description
    /// and "a name is not just spaces" are now the client's to enforce before it seals. A future reader
    /// who notices the absence must not restore a server-side check — there is nothing here to check
    /// against, and the only honest counts left are counts of envelope bytes, which
    /// <see cref="NarrativeFieldLimits"/> already caps — and must not read the absence as an oversight.
    /// </para>
    /// <para>
    /// <b>The case-folding rule left with them.</b> "Groceries" and "groceries" used to be one category
    /// because a case-insensitive collation on the column said so. They are one category now only if the
    /// client folds the text the same way before it computes <see cref="NameKey"/>, over bytes this side
    /// cannot inspect. The integration case that asserted the server did the folding was deleted with no
    /// replacement, because there is no server behaviour left to assert.
    /// </para>
    /// <para>
    /// <b><c>NormalizeDescription</c> is deleted and must not return in any form.</b> It mapped a
    /// whitespace-only description onto <see langword="null"/>, which folds "cleared" into "never
    /// filled" — see <see cref="Description"/>. Its removal is forced by the parameter's type rather than
    /// chosen: there is no string here to normalise.
    /// </para>
    /// </remarks>
    private static void ValidateOrThrow(Guid id, Guid budgetId, Guid categoryGroupId, int position)
    {
        var errors = new Dictionary<string, string[]>();

        // The identifier is now supplied rather than minted, so the empty Guid is reachable for the first
        // time - it is what a caller that threaded a default through hands over. Refused here rather than
        // left to the primary key, which accepts it: all-zero is a legal uuid, so the first such row
        // stores and the second collides under a constraint name that says nothing about the caller that
        // never chose an id at all.
        if (id == Guid.Empty)
        {
            errors[nameof(Id)] = ["Category id is required."];
        }

        if (budgetId == Guid.Empty)
        {
            errors[nameof(BudgetId)] = ["Budget id is required."];
        }

        // A category with no group is not a shape any screen can render, and the composite foreign key
        // would refuse it anyway; this is the refusal that names the member a caller can correct.
        if (categoryGroupId == Guid.Empty)
        {
            errors[nameof(CategoryGroupId)] = ["Category group id is required."];
        }

        // The one rule left that is about a VALUE rather than an identifier, and the reason this
        // validator is still reached from Update. Position is an int the server can read, so sealing took
        // no capability away from it.
        if (position < 0)
        {
            errors[nameof(Position)] = ["Position must be zero or greater."];
        }

        // Collected and never fail-fast: a caller that threaded defaults through got three identifiers
        // and the position wrong in one go, and would otherwise learn about them one request at a time.
        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }
    }
}
