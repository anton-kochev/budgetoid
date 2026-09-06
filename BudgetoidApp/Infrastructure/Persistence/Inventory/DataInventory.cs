namespace Infrastructure.Persistence.Inventory;

/// <summary>
/// The written-down half of the data inventory: what the model is allowed not to describe.
/// </summary>
/// <remarks>
/// <para>
/// Everything else about the inventory is discovered — the columns from
/// <see cref="MappedSchema" />, the relations from the live catalog — because a written-down subject
/// fails open: the relation nobody adds to it is the relation no rule is ever applied to. What has
/// to be written down is the exception, which fails closed in the same way
/// <see cref="Provisioning.RowLevelSecurityCoverage.Exemptions" /> does: a relation leaves the
/// reconciliation only by being named here, with a reason, and anything that appears without being
/// named stays red until somebody decides about it.
/// </para>
/// <para>
/// It lives in production code rather than as a constant in the test that reads it today. The
/// inventory is on its way to being a build gate, and a gate cannot reference a test assembly —
/// which is the call already made for <c>ProhibitedColumnVocabulary</c>. Putting the list here now
/// means the later story that tightens "no coverage test names a table" has nothing to relocate.
/// </para>
/// </remarks>
public static class DataInventory
{
    /// <summary>
    /// The relations that exist in the database and that the EF model deliberately does not map.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An entry excuses the relation, never its columns.</b> That is the shape of the decision
    /// rather than a convenience: a relation is outside the model or it is not, and there is no
    /// coherent middle where a mapped table drops one of its columns out of the inventory. A
    /// per-column list would offer exactly that middle, and the column it hid would be hidden under a
    /// reason written about the table. <see cref="Provisioning.TableExemption" /> draws the same line
    /// from the other side: it pins the column set an exemption was argued over, and pins
    /// <c>null</c> for this same relation on the ground that EF owns its shape, so there is nothing
    /// about that shape for anyone here to have an opinion on.
    /// </para>
    /// <para>
    /// <c>__EFMigrationsHistory</c> is EF's own bookkeeping. EF creates it, EF decides what it holds,
    /// and the model does not map it because mapping it would be this application claiming ownership
    /// of a table it does not get to change. So it is not model drift and never will be — it is a
    /// relation the model does not describe and never should, which is the only kind of thing this
    /// list may hold. A relation that appears here for any other reason is a configuration somebody
    /// has not written yet.
    /// </para>
    /// <para>
    /// The name is spelled exactly as <c>pg_class</c> stores it, quoting and all. Every comparison
    /// against it is ordinal for that reason: EF quotes the identifier, so PostgreSQL keeps the
    /// capitals, and a loose comparison would let an entry claim a relation it does not name.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> RelationsOutsideTheModel { get; } = ["__EFMigrationsHistory"];
}
