using Domain.Common;
using Domain.Security;

namespace Domain.Budgets;

public sealed class Budget
{
    private Budget()
    {
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }

    /// <summary>
    /// The sealed name the user gave this budget, or <see langword="null"/> for the budget they never
    /// asked for. What a client shows in place of a missing name is presentation, so it stays in the
    /// client — the domain has no display string to keep in sync with it, and now has no readable name
    /// either.
    /// </summary>
    /// <remarks>
    /// <b>A <see cref="NarrativeField"/> and not a <see cref="string"/>, which is what makes "this
    /// server never sees a budget name" a build property rather than a review one.</b> The type has no
    /// constructor, factory or conversion taking a <see cref="string"/>, so writing plaintext into this
    /// column does not compile — and the mistake it forecloses is the invisible kind: a row holding
    /// plaintext reads back, opens nothing and violates no constraint, it simply hands the operator the
    /// ledger.
    /// </remarks>
    public NarrativeField? Name { get; private set; }

    public string? BaseCurrencyCode { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>
    /// Creates a named budget from an identifier the caller minted and a name the caller sealed.
    /// </summary>
    /// <param name="id">
    /// The row's identifier, chosen by whoever sealed <paramref name="name"/> — never minted here. See
    /// the type-level note below on why this factory has no minting overload.
    /// </param>
    /// <param name="userId">The account that owns the budget.</param>
    /// <param name="name">The sealed name, already judged by <see cref="NarrativeField"/>.</param>
    /// <param name="createdAtUtc">The creation instant, in UTC.</param>
    /// <remarks>
    /// <para>
    /// <b><see cref="Guid.CreateVersion7"/> has left this file entirely, and not merely moved behind an
    /// overload.</b> The identifier is the associated data the client sealed the name against, so a row
    /// whose id was minted here holds a name nobody can ever open — with every constraint satisfied and
    /// nothing red. With a minting overload present, a caller that simply forgot to thread the id
    /// through would compile, would pass every test that does not assert the returned identifier, and
    /// would produce exactly that row. Deleting the minting path makes such a caller have to *name* the
    /// identifier it invents, on a line a reviewer reads in the diff. That is the whole of the
    /// protection: nothing here can tell a good id from a wrong one.
    /// </para>
    /// <para>
    /// <b>No <c>Trim</c>, and no way to add one.</b> Trimming a name was a server-side repair of client
    /// input; the server now holds an envelope, and whitespace is a property of text it cannot read.
    /// </para>
    /// </remarks>
    public static Budget Create(Guid id, Guid userId, NarrativeField name, DateTime createdAtUtc)
    {
        // Ahead of ValidateOrThrow rather than folded into it, because a missing name is not a field
        // error a caller corrects by editing a request — this overload's whole signature says a name is
        // present, so a null is a defect in this codebase. ValidationException would report it as a 400
        // about a member the request may not even have.
        ArgumentNullException.ThrowIfNull(name);

        ValidateOrThrow(id, userId);

        return new Budget
        {
            Id = id,
            UserId = userId,
            Name = name,
            CreatedAtUtc = createdAtUtc,
        };
    }

    /// <summary>
    /// Creates the nameless budget a user is provisioned with. This is the only path that produces a
    /// budget without a name; naming one is an explicit act, so it goes through <see cref="Create"/>.
    /// </summary>
    /// <param name="id">
    /// The row's identifier. Chosen by the caller for the reason <see cref="Create"/> gives, even
    /// though this row carries no sealed value to be bound to it: one rule about where a budget id
    /// comes from is cheaper than two factories that disagree, and an overload that minted here would
    /// be the one a caller of <see cref="Create"/> reaches for by mistake.
    /// </param>
    /// <param name="userId">The account that owns the budget.</param>
    /// <param name="createdAtUtc">The creation instant, in UTC.</param>
    public static Budget CreateDefault(Guid id, Guid userId, DateTime createdAtUtc)
    {
        ValidateOrThrow(id, userId);

        return new Budget
        {
            Id = id,
            UserId = userId,
            Name = null,
            CreatedAtUtc = createdAtUtc,
        };
    }

    /// <summary>
    /// Runs the two rules a budget still has on this side — that it names an owner and that it has an
    /// identifier — for every creation path. The paths share this method rather than restating them, so
    /// an ownerless or unidentified budget cannot become creatable through one factory alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every name rule is gone, and nothing replaced it.</b> There is no length to measure, no
    /// blankness to detect and no whitespace to trim: the value arriving here is an AEAD envelope over
    /// text this server has never seen and holds no key for, and <see cref="NarrativeField"/> has
    /// already judged the only thing that is judgeable about it — the framing and the cap. Character
    /// counts and emptiness are questions about plaintext.
    /// </para>
    /// <para>
    /// <b>Said plainly, because it is a capability that moved rather than a rule that was quietly
    /// dropped: this server can no longer refuse a blank budget name.</b> "A name is not just spaces"
    /// is now the client's to enforce, before it seals. A future reader who notices the absence must
    /// not restore a server-side check — there is nothing here to check it against — and must not read
    /// the absence as an oversight.
    /// </para>
    /// <para>
    /// The <c>nameRequired</c> parameter went with those rules. It selected between "check the name"
    /// and "do not", and neither branch exists now; keeping it would be a switch with one arm.
    /// </para>
    /// </remarks>
    private static void ValidateOrThrow(Guid id, Guid userId)
    {
        var errors = new Dictionary<string, string[]>();

        // The identifier is now supplied rather than minted, so the empty Guid is reachable for the
        // first time — it is what a caller that threaded a default through hands over. Refused here
        // rather than left to the primary key, which accepts it: all-zero is a legal uuid, so the first
        // such row stores and the second collides under a constraint name that says nothing about the
        // caller that never chose an id at all.
        if (id == Guid.Empty)
        {
            errors[nameof(Id)] = ["Budget id is required."];
        }

        if (userId == Guid.Empty)
        {
            errors[nameof(UserId)] = ["User id is required."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }
    }
}
