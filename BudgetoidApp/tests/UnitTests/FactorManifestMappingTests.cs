using Domain.Users;
using Infrastructure.Persistence.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace UnitTests;

/// <summary>
/// The half <c>FactorManifestTests</c> cannot reach: what EF does with
/// <see cref="FactorManifest.Manifest" />, which is a <see cref="ReadOnlyMemory{T}" /> and therefore a
/// type whose default equality is about <i>which buffer</i> rather than about which bytes.
/// </summary>
/// <remarks>
/// <para>
/// <b>The value comparer on this property was held by nothing, and that was measured rather than
/// suspected.</b> Deleting <c>ByteContentComparer</c> from the <c>HasConversion(...)</c> call in
/// <c>FactorManifestConfiguration</c> left both suites green and produced no model diff — a value
/// comparer is not emitted into the model snapshot at all, so
/// <c>BudgetoidDbContextConstructionTests.Migrations_MatchTheModel</c> cannot see it go, and no
/// migration, constraint census or grant matrix has anything to say about it either. It is the one
/// piece of this table's mapping with no relational artifact.
/// </para>
/// <para>
/// <b>What it costs when it is gone is a silent write that never happens.</b> Change tracking compares
/// a property against the snapshot taken at load, and the default comparison for
/// <see cref="ReadOnlyMemory{T}" /> is the struct's own equality — the buffer's identity, an offset and
/// a length. A manifest rewritten <i>in place</i> inside the buffer it was loaded into therefore
/// compares equal to its own snapshot, <c>SaveChanges</c> emits no <c>UPDATE</c>, and the account's
/// whole set of factor public keys fails to persist with nothing anywhere reporting it. The other
/// direction is the same defect read the other way: two identical manifests held in two buffers compare
/// as different, which costs a pointless write rather than a lost one.
/// </para>
/// <para>
/// <b>The design-time model, read the way every other model-reading unit test in this project reads
/// it</b> — <see cref="MappedSchema.DesignTimeModel" />, which builds the context against a connection
/// string it never opens. No database, no container, and the comparer is exercised directly rather than
/// through a round trip, because a round trip would need one.
/// </para>
/// <para>
/// <b>Both directions, and neither is enough alone.</b> A comparer answering "always equal" satisfies
/// the content case; a comparer answering "always different" satisfies the mutation case; the struct's
/// own equality — the thing actually shipped if the argument is dropped — satisfies neither, and gets
/// the mutation case wrong in the direction that loses data. The snapshot is taken through the comparer
/// rather than by copying the array here, because copying on snapshot is the comparer's job: a
/// snapshot that aliased the caller's buffer would be no record of the old value at all, and only
/// asking the comparer for it can catch that.
/// </para>
/// </remarks>
public sealed class FactorManifestMappingTests
{
    [Test]
    public async Task ManifestColumn_ComparesByContentRatherThanByBuffer()
    {
        // Arrange — two buffers holding the same bytes. Distinct arrays on purpose: this is exactly the
        // pair the struct's own equality calls different, and exactly the pair EF must call the same,
        // or every load of an unchanged row would be followed by an UPDATE rewriting it to itself.
        ValueComparer comparer = ManifestComparer();
        ReadOnlyMemory<byte> left = new(DistinctBytes(length: 32));
        ReadOnlyMemory<byte> right = new(DistinctBytes(length: 32));

        // Act
        bool equal = comparer.Equals(left, right);

        // Assert
        await Assert.That(equal)
            .IsTrue()
            .Because("two manifests holding the same bytes in different buffers are the same manifest");
    }

    [Test]
    public async Task ManifestColumn_ReportsAnInPlaceRewriteAsAChange()
    {
        // Arrange — one buffer, snapshotted through the comparer the way change tracking snapshots a
        // loaded value. One byte flipped afterwards, which is the smallest rewrite a real promotion
        // could make and the one nothing downstream would notice going missing.
        ValueComparer comparer = ManifestComparer();
        byte[] buffer = DistinctBytes(length: 32);
        ReadOnlyMemory<byte> tracked = new(buffer);
        object? snapshot = comparer.Snapshot(tracked);

        // Act — rewritten in place, so the struct is byte-for-byte the value it was: same buffer, same
        // offset, same length. Only the contents moved.
        buffer[0] ^= 0xFF;
        bool unchanged = comparer.Equals(snapshot, tracked);

        // Assert — a false negative here is the expensive direction. SaveChanges would emit no UPDATE
        // at all, and the account's manifest of every factor public key would stay at the generation it
        // was loaded at with every layer above reporting success.
        await Assert.That(unchanged)
            .IsFalse()
            .Because("a manifest rewritten inside its own buffer is a changed manifest, and EF only "
                     + "writes what its comparer calls changed");
    }

    /// <summary>
    /// The value comparer the model holds for <c>factor_manifests.manifest</c>.
    /// </summary>
    /// <remarks>
    /// Reached through the model rather than by naming <c>FactorManifestConfiguration</c>'s private
    /// field, which is the whole point of the pin: what a configuration declares and what the model
    /// ends up holding for a converted property are two different facts, and only the second one is
    /// what change tracking consults. A null here means the property carries no comparer of its own,
    /// which is the mutation this file exists to redden, so it is thrown on by name rather than
    /// dereferenced into an obscure assertion failure.
    /// </remarks>
    private static ValueComparer ManifestComparer()
    {
        IModel model = MappedSchema.DesignTimeModel();
        IEntityType entity = model.FindEntityType(typeof(FactorManifest))
            ?? throw new InvalidOperationException("The model maps no FactorManifest entity type.");
        IProperty property = entity.FindProperty(nameof(FactorManifest.Manifest))
            ?? throw new InvalidOperationException("FactorManifest maps no Manifest property.");

        return property.GetValueComparer()
            ?? throw new InvalidOperationException(
                "factor_manifests.manifest carries no value comparer.");
    }

    /// <summary>
    /// Builds <paramref name="length" /> bytes no two of which are equal, so a comparer that got the
    /// order wrong is visible as well as one that got the content wrong.
    /// </summary>
    /// <remarks>
    /// The step is coprime with 256, which keeps every value distinct up to a length of 256, and the
    /// offset is chosen so that no index holds its own value. A run of one repeated byte is its own
    /// reversal and its own rotation, so a comparer built on it could not tell a copy from a rebuild.
    /// Spelled out here rather than shared with <c>FactorManifestTests</c>: the two files ask different
    /// questions and a shared helper would tie a domain test to a mapping one.
    /// </remarks>
    private static byte[] DistinctBytes(int length)
    {
        byte[] bytes = new byte[length];

        for (int index = 0; index < length; index++)
        {
            bytes[index] = (byte)(0x11 + (index * 7));
        }

        return bytes;
    }
}
