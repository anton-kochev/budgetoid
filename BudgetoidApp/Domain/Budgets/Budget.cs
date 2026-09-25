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

    /// <summary>
    /// The content-key rotation that last re-sealed this row's narrative columns, or
    /// <see langword="null"/> while no rotation has ever touched it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the anchor for the six identical stamps, and the other five point here rather than
    /// restating it.</b> A content-key rotation re-encrypts every narrative field an account holds. The
    /// API caps a request body at 64 KB, so a whole-account rotation is chunked across several requests
    /// and can be interrupted part-way; the next generation of wrapped keys is therefore staged in
    /// <see cref="Users.KeyRotation"/> until one completion step promotes it. That completion step is
    /// destructive — it overwrites the live envelopes in place, destroying the only copies of the old
    /// keys — so it must refuse unless every narrative-bearing row has already been rewritten.
    /// </para>
    /// <para>
    /// <b>The server cannot see which rows those are, which is the whole reason for a column.</b> A
    /// re-sealed envelope and an untouched one are byte-for-byte indistinguishable to anything that
    /// cannot open them: a fresh nonce changes the bytes either way, and the associated data is never
    /// carried inside the envelope. So the client has to <em>tell</em> the server which rows it rewrote,
    /// and the telling has to be something the server can check for itself rather than a count it is
    /// asked to believe. A chunk stamps the in-flight rotation's id onto each row it rewrites, and
    /// completion reads the stamps back.
    /// </para>
    /// <para>
    /// <b>The stamp and the new ciphertext are written in one transaction, and the guarantee is
    /// transactional rather than statement-level.</b> They commit together or roll back together, so a
    /// row can never carry the new stamp over old ciphertext or the reverse. Do not write down that they
    /// are one <c>UPDATE</c>: whether EF emits one statement or two is EF's decision and may change,
    /// while the transaction is the property the rule actually rests on.
    /// </para>
    /// <para>
    /// <b>It has a private setter and no factory parameter, and the one member that writes it is
    /// <see cref="ResealName"/>.</b> A factory parameter would be a value every existing creation path
    /// has to pass and none of them has an answer for.
    /// </para>
    /// <para>
    /// <b>Its five siblings each carry a clearing line on their ordinary narrative write and this entity
    /// carries none, because there is no such write to hang one on.</b> A budget is created and never
    /// renamed — <see cref="Create"/> and <see cref="CreateDefault"/> are the whole instance surface —
    /// so no path exists that could leave old-key ciphertext under a current stamp. The day a "name your
    /// budget" screen ships, the member that serves it clears this column in the same commit, modelled on
    /// <see cref="Payees.Payee.Rename"/>; a member that wrote <see cref="BaseCurrencyCode"/> and nothing
    /// else would be on the other side of that line and must leave the stamp standing.
    /// </para>
    /// </remarks>
    public Guid? RotationId { get; private set; }

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
    /// Replaces the sealed name with the envelope a content-key rotation produced under the next
    /// generation of the account's keys, and records which run rewrote the row.
    /// </summary>
    /// <param name="name">
    /// The text the row already holds, re-sealed under the new content key, or <see langword="null"/>
    /// where the budget is nameless. Anything else is refused — see below.
    /// </param>
    /// <param name="rotationId">
    /// The run in flight, as <see cref="Users.KeyRotation.RotationId"/> spells it.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>It is <see langword="internal"/>, and the access level is the rule rather than an
    /// implementation detail.</b> <c>Domain.csproj</c> grants its internals to <c>Infrastructure</c> and
    /// to nothing else, so the Application ring — where a rotation's command handlers live — cannot bind
    /// to this member at all. Made public it becomes a door onto the one entity whose creation is a
    /// single consented act, reachable from any handler anybody adds, with nothing red; widening the
    /// grant to reach it instead moves a pinned row in <c>ProjectReferenceGraphTests</c>. Neither is a
    /// way to make a caller compile.
    /// </para>
    /// <para>
    /// <b>It takes a bare <see cref="NarrativeField"/> where its four siblings take an
    /// <c>IndexedName</c>, and that is a consequence of the sealing rather than an inconsistency.</b>
    /// Sealing surrendered uniqueness on <c>budgets.name</c> — a decision, not a gap — so there is no
    /// blind index on this column and no second half for a pair type to hold together.
    /// </para>
    /// <para>
    /// <b>The name is judged by <see cref="NarrativeReseal.Resealed"/> and not by four lines written
    /// here</b>, and on this entity the nameless arm is the ordinary case rather than the exotic one: a
    /// budget is provisioned without a name by the one path that creates an account, and most stay that
    /// way. So a rotation supplying a name for a nameless budget is refused — presence is the only
    /// property this side can check, and an arm admitting a change of it would let a budget silently
    /// acquire a name nobody typed — while a nameless budget handed no name is accepted <em>and still
    /// stamped</em>.
    /// </para>
    /// <para>
    /// <b>The stamp on that nameless row is uniformity rather than necessity, and the difference is worth
    /// stating because the obvious reason for it is false.</b> The completeness gate
    /// (<c>Application.KeyRotations.IRotationCompletenessReadService</c>) is <em>presence-aware</em>: a
    /// row carrying no narrative value owes no stamp, because there is nothing on it to re-seal. A
    /// nameless budget is exactly such a row, so it neither blocks a completion nor is counted by one,
    /// and the gate answers the same whether this member stamps it or not. Completion is <em>not</em> one
    /// short without it. What the stamp buys is smaller and still worth having: the client drives all six
    /// stamped tables through one shape, and this member has one post-condition instead of two.
    /// </para>
    /// <para>
    /// <b>The dependency runs the other way, so name it rather than leave it implied.</b> If the gate ever
    /// stopped being presence-aware — if it required a stamp on every row of all six tables — this line
    /// would become load-bearing on nearly every account in the product, because
    /// <c>budgets.name</c> is <c>NULL</c> on every row in every database today. That same change would
    /// also make every note-less transaction owe a write it has nothing to perform, which is why the gate
    /// is built the way it is and why it is not expected to change. A reader removing the stamp here must
    /// check that predicate first; a reader tightening that predicate must come back to this line.
    /// </para>
    /// <para>
    /// <b>Nothing else on the row is writable from here.</b> <see cref="UserId"/> above all: a budget is
    /// the unit of tenancy, so a row that changed owner carries a whole ledger with it. The database
    /// says the same by granting <c>UPDATE</c> on <c>name</c> alone, which
    /// <c>DomainImmutabilityTests</c> and <c>AppRoleGrantMatrixTests</c> hold from their two ends.
    /// </para>
    /// </remarks>
    /// <exception cref="ValidationException">
    /// <paramref name="rotationId"/> is <see cref="Guid.Empty"/>, or <paramref name="name"/> changes
    /// whether <see cref="Name"/> holds a value.
    /// </exception>
    internal void ResealName(NarrativeField? name, Guid rotationId)
    {
        // Both refusals run ahead of both assignments, so a refused reseal leaves the name and the stamp
        // exactly as the last chunk left them. A member that stamped first would mark a row as rewritten
        // that was not, which is the one signal the destructive completion step trusts.
        RequireRotation(rotationId);

        NarrativeField? resealedName = NarrativeReseal.Resealed(Name, name, nameof(Name));

        Name = resealedName;
        RotationId = rotationId;
    }

    /// <summary>
    /// Refuses the empty rotation identifier, keyed on the member the stamp lands in.
    /// </summary>
    /// <remarks>
    /// <b>The rule belongs to <see cref="Users.KeyRotation.Begin"/> and is restated at the far end of the
    /// same identifier rather than invented here.</b> That factory argues it: all-zeros is a storable
    /// uuid and is what a client that has begun no run sends, so a stamp carrying it marks a row as
    /// rewritten under a rotation no <c>key_rotations</c> row matches — a full house belonging to nobody,
    /// read by the step that destroys the old keys.
    /// </remarks>
    private static void RequireRotation(Guid rotationId)
    {
        if (rotationId == Guid.Empty)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                [nameof(RotationId)] = ["Rotation id is required."],
            });
        }
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
