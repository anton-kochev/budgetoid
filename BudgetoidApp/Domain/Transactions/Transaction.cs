using Domain.Common;
using Domain.Security;

namespace Domain.Transactions;

public sealed class Transaction
{
    private const int MaxMinorUnit = 4;

    private Transaction()
    {
    }

    public Guid Id { get; private set; }
    public Guid BudgetId { get; private set; }
    public Guid AccountId { get; private set; }
    public decimal Amount { get; private set; }
    public DateOnly Date { get; private set; }

    /// <summary>
    /// The sealed note filed against the transaction, or <see langword="null"/> where it has none. The
    /// only narrative column this entity has.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A <see cref="NarrativeField"/> and not a <see cref="string"/>, which is what makes "this server
    /// never sees a transaction note" a build property rather than a review one.</b> The type has no
    /// constructor, factory or conversion taking a <see cref="string"/>, so writing plaintext into this
    /// column does not compile. What is specific to this column is that it is the free-text field a
    /// person actually types into — "consultation", "solicitor", "moving out" — beside an amount, a date
    /// and a counterparty that give it context.
    /// </para>
    /// <para>
    /// <b>It carries no blind index and this is the first sealed table in the product with no name column
    /// at all.</b> There is no <see cref="IndexedName"/> here, no <c>name_key</c> and no unique index,
    /// because the pair type has nothing to be half of. A description is not looked up, is not unique and
    /// is not a name; an index over one would publish a deterministic per-account fingerprint of somebody's
    /// free text with nothing on the other side asking for it.
    /// </para>
    /// <para>
    /// <b>Its cap is <see cref="NarrativeFieldLimits.DescriptionBytes"/> and not
    /// <see cref="NarrativeFieldLimits.NameBytes"/>, and the two are field <em>classes</em> rather than
    /// two guesses at one number.</b>
    /// </para>
    /// <para>
    /// <b>A LOST NOTE IS INVISIBLE, AND THERE IS NO <c>NOT NULL</c> NEIGHBOUR HERE TO CATCH THE SAME
    /// OMISSION.</b> The column is nullable and it is the only narrative column on the table, so a write
    /// path that decoded a note and then forgot to assign it writes <see langword="null"/>: a legal row,
    /// violating no constraint, byte-identical to one belonging to somebody who deliberately filed none.
    /// Its sibling entities at least hold a <c>NOT NULL</c> name that turns the same defect into a
    /// <c>23502</c>; this one does not. What holds it is that every write path is covered by a case that
    /// reads a <em>non-null</em> note back — never one asserting the member is merely present, and never
    /// one asserting the route answered 204.
    /// </para>
    /// <para>
    /// <b>"Cleared" and "never filled" are two rows and must stay two.</b> An empty note seals to exactly
    /// <see cref="CiphertextEnvelope.MinimumLength"/> bytes — AES-GCM ciphertext is the length of its
    /// plaintext — while a note nobody wrote is <see langword="null"/>. The normalisation that folded a
    /// whitespace-only description onto <see langword="null"/> left with the <see cref="string"/>
    /// parameter and cannot come back in any form: with no text to inspect, an entity that folded would
    /// have to fold on the envelope's LENGTH, turning every emptied note in the product into a note
    /// nobody ever wrote.
    /// </para>
    /// </remarks>
    public NarrativeField? Description { get; private set; }

    public Guid? PayeeId { get; private set; }
    public Guid? CategoryId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>
    /// Creates a transaction from an identifier the caller minted and a note the caller sealed, or did
    /// not file at all.
    /// </summary>
    /// <param name="id">
    /// The row's identifier, chosen by whoever sealed <paramref name="description"/> — never minted here.
    /// See the note below on why this factory has no minting overload.
    /// </param>
    /// <param name="budgetId">The budget that owns the transaction.</param>
    /// <param name="accountId">The account it is filed under.</param>
    /// <param name="amount">The signed amount, in the account's currency.</param>
    /// <param name="minorUnit">
    /// How many decimal places that currency has. Not user input — see
    /// <see cref="ValidateOrThrow"/> for why it fails fast rather than joining the collected errors.
    /// </param>
    /// <param name="date">The day the money moved.</param>
    /// <param name="description">The sealed note, or <see langword="null"/> for a transaction with none.</param>
    /// <param name="createdAtUtc">The creation instant, in UTC.</param>
    /// <remarks>
    /// <b><see cref="Guid.CreateVersion7()"/> has left this file entirely, and not merely moved behind an
    /// overload.</b> The identifier is the associated data the client sealed
    /// <paramref name="description"/> against, so a row whose id was minted here holds a note nobody can
    /// ever open — every constraint satisfied, nothing red, and the symptom arriving months later as text
    /// that will not decrypt. A minting overload would let a caller that simply forgot to thread the id
    /// through compile and pass, so deleting the minting path is the whole of the protection: it forces
    /// such a caller to <em>name</em> the identifier it invents, on a line a reviewer reads in the diff.
    /// </remarks>
    public static Transaction Create(
        Guid id,
        Guid budgetId,
        Guid accountId,
        decimal amount,
        int minorUnit,
        DateOnly date,
        NarrativeField? description,
        DateTime createdAtUtc)
    {
        ValidateOrThrow(id, budgetId, accountId, amount, minorUnit);

        return new Transaction
        {
            Id = id,
            BudgetId = budgetId,
            AccountId = accountId,
            Amount = amount,
            Date = date,

            // Assigned, not normalised. There is no text here to trim and no blankness to detect: an
            // envelope over an empty string is a note somebody emptied and null is a note nobody wrote,
            // and this line is where the difference between them survives into the row.
            Description = description,
            CreatedAtUtc = createdAtUtc,
        };
    }

    /// <summary>
    /// Replaces the editable fields of the transaction. The budget, identity and creation time are
    /// not the caller's to rewrite, and payee and category have their own assign/clear methods.
    /// </summary>
    /// <param name="accountId">The account the transaction ends up on.</param>
    /// <param name="amount">The new signed amount.</param>
    /// <param name="minorUnit">The decimal places the destination account's currency has.</param>
    /// <param name="date">The new date.</param>
    /// <param name="description">The new sealed note, or <see langword="null"/> to clear the one it held.</param>
    /// <remarks>
    /// <b>A <see langword="null"/> <paramref name="description"/> CLEARS the note, and the third
    /// "leave it alone" state belongs to the route rather than to this member.</b>
    /// <c>PATCH /api/transactions/{id}</c> carries an <c>Optional&lt;string?&gt;</c> and decides absence
    /// before the entity is reached, handing this member whichever value survives. An entity that
    /// invented a third state here would make the route's clear leg unwritable.
    /// </remarks>
    public void Update(
        Guid accountId,
        decimal amount,
        int minorUnit,
        DateOnly date,
        NarrativeField? description)
    {
        // Every assignment sits below the refusal, so a refused update replaces every field or none.
        // Unlike the sibling entities, that claim is testable here: amount, account id and minor unit are
        // values the server still reads, so a legal transaction can be handed a refused edit and the
        // ValidationException is raised INSIDE the validator with the four assignments below it. On
        // Category and CategoryGroup the same claim can only be made against a null argument, which the
        // compiler forces to be refused at a dereference before any assignment.
        ValidateOrThrow(Id, BudgetId, accountId, amount, minorUnit);

        AccountId = accountId;
        Amount = amount;
        Date = date;
        Description = description;
    }

    /// <summary>
    /// Validates the fields shared by <see cref="Create"/> and <see cref="Update"/>, so the two paths
    /// cannot drift apart.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It returns <see langword="void"/>, and that is a change rather than a shape it always had.</b>
    /// It used to hand back the normalised description, and the two write paths assigned from the return.
    /// The normalisation is gone with the <see cref="string"/> parameter, so what a returning version
    /// would hand back is the argument it was given. <b>That version was built and run: it passes the
    /// whole unit suite, every case, because the value is the same either way</b> — no test can see a
    /// private method's signature. The return is deleted because a return value that exists is a return
    /// value somebody finds a use for, and the use would be a second place where a note could be
    /// transformed on its way into the column. This paragraph and review are the whole of what holds it.
    /// </para>
    /// <para>
    /// <b>Every description rule is gone and nothing replaced them.</b> The trim, the whitespace fold and
    /// the five-hundred-character cap were all questions about characters; this server holds ciphertext
    /// over text it has never seen and no key to open it with. The client, which holds the key, is the
    /// only side that can ask them, and <see cref="NarrativeField"/> has already judged the one thing
    /// judgeable here — the framing and the byte cap. A future reader who notices the absence must not
    /// restore a server-side check: there is nothing to check against.
    /// </para>
    /// <para>
    /// <b>Two classes of refusal leave this method, and the order between them is the rule.</b>
    /// <c>minorUnit</c> is not user input — it comes from the account's currency, which the database
    /// bounds with <c>CK_currencies_minor_unit</c> — so an out-of-range value is a broken caller and
    /// throws <see cref="ArgumentOutOfRangeException"/> before the errors dictionary exists. Everything
    /// else is a field a caller can correct and collects into one
    /// <see cref="Domain.Common.ValidationException"/>. Reversed, a broken caller is told it typed a bad
    /// identifier and the real fault — a currency row or a lookup this request should never have got past
    /// — is never reported at all. <b>The collision is older than this slice and the ordering is not
    /// new</b>: the previous version already threw on <c>minorUnit</c> above a dictionary collecting the
    /// budget and account identifiers, so a caller passing an empty <c>budgetId</c> beside a bad
    /// <c>minorUnit</c> met this rule then too. What this slice changed is the membership — the
    /// transaction's own identifier joined the collected class when it started arriving from a request
    /// body — not the rule, which the reader should not date to it.
    /// </para>
    /// </remarks>
    private static void ValidateOrThrow(
        Guid id,
        Guid budgetId,
        Guid accountId,
        decimal amount,
        int minorUnit)
    {
        // The minor unit comes from the account's currency, which the database already bounds to
        // 0..4, so an out-of-range value is a broken caller rather than user input - and a bad one
        // makes the precision check below meaningless, so it fails fast instead of joining errors:
        // "Amount must have no more than -1 decimal places" is not a sentence anybody can act on.
        ArgumentOutOfRangeException.ThrowIfNegative(minorUnit);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minorUnit, MaxMinorUnit);

        var errors = new Dictionary<string, string[]>();

        // The identifier is now supplied rather than minted, so the empty Guid is reachable for the first
        // time - it is what a caller that threaded a default through hands over. Refused here rather than
        // left to the primary key, which accepts it: all-zero is a legal uuid, so the first such row
        // stores and the second collides under a constraint name that says nothing about the caller that
        // never chose an id at all.
        if (id == Guid.Empty)
        {
            errors[nameof(Id)] = ["Transaction id is required."];
        }

        if (budgetId == Guid.Empty)
        {
            errors[nameof(BudgetId)] = ["Budget id is required."];
        }

        if (accountId == Guid.Empty)
        {
            errors[nameof(AccountId)] = ["Account id is required."];
        }

        if (decimal.Round(amount, minorUnit) != amount)
        {
            errors[nameof(Amount)] = [minorUnit == 0
                ? "Amount must be a whole number."
                : $"Amount must have no more than {minorUnit} decimal places."];
        }
        else if (Math.Abs(amount) > 1_000_000_000m)
        {
            errors[nameof(Amount)] = ["Amount must be less than or equal to 1000000000 in absolute value."];
        }

        // Collected and never fail-fast: a caller that threaded defaults through got three identifiers
        // wrong in one go, and would otherwise learn about them one request at a time.
        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }
    }

    public void AssignPayee(Guid payeeId)
    {
        // An empty id here is a programmer/invariant error (the caller always passes a real
        // payee id), not user-facing validation — so ArgumentException, not ValidationException.
        if (payeeId == Guid.Empty)
        {
            throw new ArgumentException("Payee id is required.", nameof(payeeId));
        }

        PayeeId = payeeId;
    }

    // Clearing is a legitimate user action once a transaction can be edited, so it gets its own
    // method rather than being spelled AssignPayee(Guid.Empty) - which would force AssignPayee to
    // stop treating an empty id as a programmer error. Already-null is a no-op, not a throw.
    public void ClearPayee() => PayeeId = null;

    public void AssignCategory(Guid categoryId)
    {
        // An empty id here is a programmer/invariant error (the caller always passes a real
        // category id), not user-facing validation — so ArgumentException, not ValidationException.
        if (categoryId == Guid.Empty)
        {
            throw new ArgumentException("Category id is required.", nameof(categoryId));
        }

        CategoryId = categoryId;
    }

    // Same reasoning as ClearPayee: uncategorised is a state a user can ask for, and
    // AssignCategory(Guid.Empty) is not how they should have to ask for it.
    public void ClearCategory() => CategoryId = null;
}
