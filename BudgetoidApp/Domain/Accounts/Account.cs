using Domain.Common;
using Domain.Security;

namespace Domain.Accounts;

public sealed class Account
{
    private const int MaxMinorUnit = 4;

    private Account()
    {
    }

    public Guid Id { get; private set; }
    public Guid BudgetId { get; private set; }

    /// <summary>
    /// The sealed name this account is listed under — the AEAD envelope over text this server has never
    /// seen and holds no key for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A <see cref="NarrativeField"/> and not a <see cref="string"/>, which is what makes "this server
    /// never sees an account name" a build property rather than a review one.</b> The type has no
    /// constructor, factory or conversion taking a <see cref="string"/>, so writing plaintext into this
    /// column does not compile — and the mistake it forecloses is the invisible kind: a row holding
    /// plaintext reads back, opens nothing and violates no constraint, it simply hands the operator the
    /// ledger.
    /// </para>
    /// <para>
    /// <b>It is written only with <see cref="NameKey"/>, never alone.</b> Both members are assigned from
    /// one <see cref="IndexedName"/> parameter, at the two places below and nowhere else; see
    /// <see cref="Update"/> for what a member taking a bare <see cref="NarrativeField"/> would cost.
    /// </para>
    /// </remarks>
    public NarrativeField Name { get; private set; } = null!;

    /// <summary>
    /// The blind index over the same name: <c>HMAC-SHA-256</c> under the account's index key, computed
    /// by the client over the normalised text and equal across every row holding that name in this
    /// column of this account. It is what <c>IX_accounts_budget_id_name_key</c> enforces uniqueness over.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A property of its own rather than the whole <see cref="IndexedName"/> on the entity, and that
    /// is a mapping decision as much as a modelling one.</b> The pair is two columns, so it is two
    /// properties; holding the pair type here would need EF to materialise it, and the only public way to
    /// build one is <see cref="IndexedName.Of"/>, which <em>judges</em>. A validating read side makes the
    /// caps and the version byte retroactive — the argument <see cref="NarrativeField.FromStore"/> spells
    /// out — so a limit change would become silent data loss on rows written under the old number. The
    /// pair type therefore guards the <em>call</em>, which is the moment a half can be made, and the two
    /// <c>NOT NULL</c> columns guard the row.
    /// </para>
    /// <para>
    /// <b><see cref="ReadOnlyMemory{T}"/> and not <see cref="byte"/><c>[]</c>, following
    /// <see cref="Users.WrappedAccountKeys.WrappedContentKey"/>.</b> The window handed over by
    /// <see cref="IndexedName.BlindIndex"/> is onto a buffer that value owns outright — the copy its
    /// factory took — so aliasing it here shares nothing with any caller. Nothing about the type
    /// guarantees a width: <c>default</c> is a zero-length buffer, which is why
    /// <see cref="IndexedName.Of"/> refuses anything that is not exactly
    /// <see cref="IndexedName.BlindIndexLength"/> bytes and the column restates it as an equality
    /// <c>CHECK</c>.
    /// </para>
    /// </remarks>
    public ReadOnlyMemory<byte> NameKey { get; private set; }

    public AccountType Type { get; private set; }
    public decimal OpeningBalance { get; private set; }
    public string CurrencyCode { get; private set; } = string.Empty;
    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>
    /// Creates an account from an identifier the caller minted and a name the caller sealed and indexed.
    /// </summary>
    /// <param name="id">
    /// The row's identifier, chosen by whoever sealed <paramref name="name"/> — never minted here. See
    /// the note below on why this factory has no minting overload.
    /// </param>
    /// <param name="budgetId">The budget that owns the account.</param>
    /// <param name="name">The sealed name and its blind index, already judged by <see cref="IndexedName"/>.</param>
    /// <param name="type">The kind of account.</param>
    /// <param name="openingBalance">The balance the ledger starts from.</param>
    /// <param name="currencyCode">The ISO 4217 code, normalised here.</param>
    /// <param name="minorUnit">
    /// How many decimal places <paramref name="currencyCode"/> admits, read off the currency row.
    /// </param>
    /// <param name="createdAtUtc">The creation instant, in UTC.</param>
    /// <remarks>
    /// <b><see cref="Guid.CreateVersion7"/> has left this file entirely, and not merely moved behind an
    /// overload.</b> The identifier is the associated data the client sealed the name against, so a row
    /// whose id was minted here holds a name nobody can ever open — with every constraint satisfied and
    /// nothing red. With a minting overload present, a caller that simply forgot to thread the id through
    /// would compile, would pass every test that does not assert the returned identifier, and would
    /// produce exactly that row. Deleting the minting path makes such a caller have to <em>name</em> the
    /// identifier it invents, on a line a reviewer reads in the diff. That is the whole of the
    /// protection: nothing here can tell a good id from a wrong one.
    /// </remarks>
    public static Account Create(
        Guid id,
        Guid budgetId,
        IndexedName name,
        AccountType type,
        decimal openingBalance,
        string currencyCode,
        int minorUnit,
        DateTime createdAtUtc)
    {
        // Ahead of ValidateOrThrow rather than folded into it, because a missing name is not a field
        // error a caller corrects by editing a request — this signature says a name is present, so a null
        // is a defect in this codebase. ValidationException would report it as a 400 about a member the
        // request may not even have.
        ArgumentNullException.ThrowIfNull(name);

        ValidateOrThrow(id, budgetId, type, openingBalance, currencyCode, minorUnit);

        return new Account
        {
            Id = id,
            BudgetId = budgetId,
            Name = name.Name,
            NameKey = name.BlindIndex,
            Type = type,
            OpeningBalance = openingBalance,
            CurrencyCode = NormalizeCurrencyCode(currencyCode),
            CreatedAtUtc = createdAtUtc,
        };
    }

    /// <summary>
    /// Replaces the account's name, kind and opening balance. The currency is not among them: it is
    /// fixed at creation, and <paramref name="minorUnit"/> is the one the account's own currency carries.
    /// </summary>
    /// <param name="name">The new sealed name and the index computed over the same text.</param>
    /// <param name="type">The kind of account.</param>
    /// <param name="openingBalance">The balance the ledger starts from.</param>
    /// <param name="minorUnit">
    /// How many decimal places <see cref="CurrencyCode"/> admits, read off the currency row.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>It takes an <see cref="IndexedName"/>, and that parameter type is the whole of what makes "both
    /// halves of the name move together" a fact rather than a habit.</b> The ciphertext and the index are
    /// two columns and two properties, so nothing about the storage stops a member from writing one; what
    /// stops it is that neither this member nor <see cref="Create"/> offers a spelling for half a name.
    /// </para>
    /// <para>
    /// <b>A later member taking a bare <see cref="NarrativeField"/> is therefore the mistake to refuse in
    /// review, and it is silent in every direction.</b> The row would hold new ciphertext under the
    /// previous name's index: the uniqueness constraint would go on policing a name the row no longer
    /// holds, a search for the new name would miss a row that has it, a search for the old one would
    /// return a row that does not, and a rename to a name already taken would be accepted. Every
    /// constraint is satisfied, nothing reads back wrong, and the server cannot compute either half to
    /// notice the disagreement — recomputing an index needs the account's index key, which lives in a
    /// browser.
    /// </para>
    /// </remarks>
    public void Update(IndexedName name, AccountType type, decimal openingBalance, int minorUnit)
    {
        ArgumentNullException.ThrowIfNull(name);

        ValidateOrThrow(Id, BudgetId, type, openingBalance, CurrencyCode, minorUnit);

        Name = name.Name;
        NameKey = name.BlindIndex;
        Type = type;
        OpeningBalance = openingBalance;
    }

    /// <summary>
    /// Runs the rules this entity still owns — the tenancy, the identifier, the kind, the currency code
    /// and the two halves of the balance rule — for every path that writes them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every name rule is gone, and nothing replaced it.</b> There is no length to measure, no
    /// blankness to detect and no whitespace to trim: the value arriving here is an AEAD envelope over
    /// text this server has never seen and holds no key for, and <see cref="IndexedName"/> has already
    /// judged the only things that are judgeable about the pair — the framing, the cap, and the index's
    /// width.
    /// </para>
    /// <para>
    /// <b>Said plainly, because it is a capability that moved rather than a rule that was quietly
    /// dropped: this server can no longer refuse a blank account name.</b> "A name is not just spaces",
    /// and the 200-character ceiling with it, are now the client's to enforce before it seals. A future
    /// reader who notices the absence must not restore a server-side check — there is nothing here to
    /// check it against, and the only honest count left is a count of envelope bytes, which
    /// <c>NarrativeFieldLimits.NameBytes</c> already caps — and must not read the absence as an
    /// oversight.
    /// </para>
    /// </remarks>
    private static void ValidateOrThrow(
        Guid id,
        Guid budgetId,
        AccountType type,
        decimal openingBalance,
        string? currencyCode,
        int minorUnit)
    {
        // The minor unit comes from the account's currency, which the database already bounds to
        // 0..4, so an out-of-range value is a broken caller rather than user input - and a bad one
        // makes the precision check below meaningless, so it fails fast instead of joining errors.
        ArgumentOutOfRangeException.ThrowIfNegative(minorUnit);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minorUnit, MaxMinorUnit);

        var errors = new Dictionary<string, string[]>();
        string normalizedCurrencyCode = NormalizeCurrencyCode(currencyCode);

        // The identifier is now supplied rather than minted, so the empty Guid is reachable for the first
        // time — it is what a caller that threaded a default through hands over. Refused here rather than
        // left to the primary key, which accepts it: all-zero is a legal uuid, so the first such row
        // stores and the second collides under a constraint name that says nothing about the caller that
        // never chose an id at all.
        if (id == Guid.Empty)
        {
            errors[nameof(Id)] = ["Account id is required."];
        }

        if (budgetId == Guid.Empty)
        {
            errors[nameof(BudgetId)] = ["Budget id is required."];
        }

        if (!Enum.IsDefined(type))
        {
            errors[nameof(Type)] = ["Account type is invalid."];
        }

        if (string.IsNullOrWhiteSpace(normalizedCurrencyCode))
        {
            errors[nameof(CurrencyCode)] = ["Currency code is required."];
        }
        else if (normalizedCurrencyCode.Length != 3 || normalizedCurrencyCode.Any(character => character is < 'A' or > 'Z'))
        {
            errors[nameof(CurrencyCode)] = ["Currency code must be exactly 3 ASCII letters."];
        }

        if (decimal.Round(openingBalance, minorUnit) != openingBalance)
        {
            errors[nameof(OpeningBalance)] = [minorUnit == 0
                ? "Opening balance must be a whole number."
                : $"Opening balance must have no more than {minorUnit} decimal places."];
        }
        else if (Math.Abs(openingBalance) > 1_000_000_000m)
        {
            errors[nameof(OpeningBalance)] = ["Opening balance must be less than or equal to 1000000000 in absolute value."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }
    }

    private static string NormalizeCurrencyCode(string? currencyCode) => (currencyCode ?? string.Empty).Trim().ToUpperInvariant();
}
