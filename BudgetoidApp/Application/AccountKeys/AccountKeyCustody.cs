using Domain.Users;

namespace Application.AccountKeys;

/// <summary>
/// Everything one account holds on this server to open itself with, at the two levels it actually has:
/// the account's manifest of factor public keys and the generation that manifest is in, and one
/// <see cref="FactorEnvelopes" /> per recovery factor.
/// </summary>
/// <param name="Manifest">
/// The authenticated manifest bytes as <c>factor_manifests.manifest</c> holds them, or
/// <see langword="null" /> when the account has no manifest row. <b>Both answers are reachable, and
/// which one an account gives is decided by when it was registered</b>: registration writes the first
/// manifest at epoch 1 in the same save as the account, so an account created since that landed answers
/// bytes, and one created before it answers <see langword="null" /> forever — no backfill is possible,
/// because the blob is sealed under a content key this server has never held.
/// </param>
/// <param name="RotationEpoch">
/// Which generation of the manifest is in force, or <c>0</c> when there is no manifest row. <b>Zero is
/// the absence of the row and not an error</b>: <see cref="Domain.Users.FactorManifest.MinimumRotationEpoch" />
/// is 1 and a stored row is refused below it, so no stored generation can collide with the answer that
/// means "there is nothing stored". That is the state of an account registered before registration began
/// writing a manifest, and of no account created since.
/// </param>
/// <param name="Factors">
/// One row per recovery factor — one per registered passkey, ten per set of recovery codes — ordered
/// by <see cref="FactorEnvelopes.FactorId" />, and empty when the account holds none this request can
/// see. An empty list is a normal answer; <c>GetAccountKeysHandler</c> enumerates the four ways it is
/// reached.
/// </param>
/// <remarks>
/// <para>
/// <b>Two levels, and which fact belongs at which is the whole point of this type.</b> A manifest names
/// the <em>set</em> of factors and an epoch numbers the generation of that set: both are facts about the
/// <b>account</b>, true once however many factors the account holds. What a factor owns is its own
/// identifier and its own two payloads, and that is what <see cref="FactorEnvelopes" /> carries. Before
/// this type existed there was no per-account level at all, so the only place to hang an account-wide
/// fact was on every row — eleven copies of one value, disagreeing with each other the moment anything
/// wrote them in two statements. This type is the right place, and its existence is not permission to
/// widen the row: a member that is true once per account goes here, a member that varies per factor
/// goes there, and a member that is neither is refused by the arguments both types carry.
/// </para>
/// <para>
/// <b>One type off one read, because the consumer's whole job is to compare the two levels against each
/// other.</b> A client refuses a rotation when the manifest is missing, and refuses one where the served
/// factor set and the set the manifest names disagree. That comparison is only meaningful if both halves
/// describe the same instant. Two port members — or a second port beside
/// <see cref="IAccountKeyReadService" /> — would be two round trips that a concurrent write can sit
/// between, so a factor enrolled during the gap would make a correct manifest and a correct factor list
/// disagree, and the client would report a mismatch to a person as though something had been tampered
/// with.
/// </para>
/// <para>
/// <b>This shape is necessary for that and does not achieve it, and the difference is worth being exact
/// about, because the sentence above is the kind a later reader takes for a guarantee.</b> One member
/// removes the <em>obligation</em> to make two round trips; it cannot stop an implementation making them
/// anyway, and two awaited queries on one connection are two autocommitted statements taking two
/// <c>READ COMMITTED</c> snapshots — the same window, moved one layer down and out of sight. Wrapping
/// them in a transaction would not close it either: <c>READ COMMITTED</c> takes a fresh snapshot per
/// statement, so only <c>REPEATABLE READ</c> would, and no read on this path opens a transaction of any
/// kind. What actually holds the claim is that <c>AccountKeyReadService</c> reads both levels in
/// <b>one SQL statement</b> — measured, and argued where it is written. An implementation splitting that
/// into two awaits satisfies this type, compiles, and reddens nothing.
/// </para>
/// <para>
/// Reasoned, not run: the only writer is registration's single INSERT, which happens before anything
/// can read the account at all, so the race has no second writer to run against yet — which is exactly
/// why the shape has to refuse it before promotion arrives.
/// </para>
/// <para>
/// <b>The pair is flat rather than a nullable nested <c>manifest</c> object, and the rejected shape has a
/// real argument.</b> Nesting would make the pairing unconstructible-wrong in one direction — no epoch
/// without bytes — which is worth something. What it costs is more: with no nested value there is no
/// epoch either, so every consumer would supply the <c>0</c> itself, and "absent means epoch 0" would be
/// restated once per edge instead of decided once by the implementation that is the only thing here
/// that sees the missing row. The choice is made on where the default lives, and the safety the nested
/// shape was reaching for is bought here another way.
/// </para>
/// <para>
/// <b>Flatness is now held by a guard rather than by convention, and the guard is the reason the
/// paragraph above no longer has to concede anything.</b> The pairing this type used to admit —
/// <c>(null, 7)</c>, bytes at epoch 0, and <see cref="ReadOnlyMemory{T}.Empty" /> at any epoch, all of
/// which compiled — is refused by the primary constructor: an absent manifest takes
/// <see cref="NoManifestRotationEpoch" /> and nothing else, and a present one must be non-empty, no
/// wider than <see cref="Domain.Users.FactorManifest.MaximumBytes" />, and carry an epoch at or above
/// <see cref="Domain.Users.FactorManifest.MinimumRotationEpoch" />. The three bounds are read off the
/// entity that owns the column rather than written out here, so this guard cannot drift away from the
/// <c>CHECK</c> constraints that refuse the same rows. <b>What the guard does not reach is a
/// <c>with</c> expression</b>, which copies fields rather than running the constructor — so
/// <see cref="Manifest" /> and <see cref="RotationEpoch" /> are declared get-only, which makes
/// <c>with { Manifest = … }</c> and <c>with { RotationEpoch = … }</c> fail to compile instead of
/// slipping past. <see cref="Factors" /> keeps its <c>init</c>: it is outside the pair the guard is
/// about.
/// </para>
/// <para>
/// <b>The absent case has one spelling, <see cref="WithNoManifest" />, and it exists because it had
/// three.</b> "No manifest means epoch 0" was decided independently in two literals in
/// <c>AccountKeyReadService</c> and once more in a test fake, which is three places a later edit has to
/// find and agree with. The factory is the one that decides it now, and the guard is what makes the
/// other spellings unavailable rather than merely discouraged.
/// </para>
/// <para>
/// <b><see cref="Manifest" /> is a nullable <see cref="ReadOnlyMemory{T}" /> rather than
/// <see cref="ReadOnlyMemory{T}.Empty" />.</b> An empty buffer is a value
/// <see cref="Domain.Users.FactorManifest.For" /> refuses to build, so using it for "no row" would spend
/// a spelling the domain has already declared impossible on the one state that is normal — and it
/// survives to the wire, where an empty manifest and an absent one both encode as the empty string,
/// which is a legal base64url rendering of zero bytes. The client could then not tell them apart either.
/// </para>
/// <para>
/// <b>Bytes here, text at the edge</b>, the rule <see cref="FactorEnvelopes" /> states for its two
/// payloads and this member follows for the same reason: base64url is a wire spelling this ring has no
/// opinion about.
/// </para>
/// <para>
/// <b>Nothing on this type can be opened by the server, and no member may be added that could be.</b>
/// A manifest carries <em>public</em> keys — the halves a value is <em>encapsulated to</em> — so its
/// bytes are material this server may hold in the clear, which is a different licence from the one the
/// envelopes beside it hold and not a wider one. A member carrying an unwrapped key, a private key, a
/// key-encryption key, a PRF output or a recovery code is refused here exactly as it is on
/// <see cref="FactorEnvelopes" />, and would fail no test, because there is no test that can notice a
/// value the design says never arrives.
/// </para>
/// </remarks>
/// <exception cref="ArgumentException">A manifest was supplied and it carried no bytes.</exception>
/// <exception cref="ArgumentOutOfRangeException">
/// The manifest and the epoch disagree — an absent manifest at a non-zero epoch, or a present one at an
/// epoch below <see cref="Domain.Users.FactorManifest.MinimumRotationEpoch" /> — or the manifest is
/// wider than <see cref="Domain.Users.FactorManifest.MaximumBytes" />.
/// </exception>
public sealed record AccountKeyCustody(
    ReadOnlyMemory<byte>? Manifest,
    int RotationEpoch,
    IReadOnlyList<FactorEnvelopes> Factors)
{
    /// <summary>The epoch an account holding no <c>factor_manifests</c> row answers with.</summary>
    /// <remarks>
    /// Named rather than written out, because the number is meaningless on its own: it is the one
    /// value <see cref="Domain.Users.FactorManifest.MinimumRotationEpoch" /> keeps free so that
    /// "nothing is stored" cannot be read as a stored generation. A literal <c>0</c> at a call site
    /// says neither half of that.
    /// </remarks>
    public const int NoManifestRotationEpoch = 0;

    /// <summary>
    /// The authenticated manifest bytes, or <see langword="null" /> when the account holds no manifest
    /// row — the type's <c>Manifest</c> parameter argues what that absence means.
    /// </summary>
    // The guard hangs off this initializer because a property initializer is the one place in a
    // positional record that sees every primary-constructor argument at once. Get-only, so the
    // copy a `with` makes cannot route around it.
    public ReadOnlyMemory<byte>? Manifest { get; } = Agreeing(Manifest, RotationEpoch);

    /// <summary>
    /// Which generation the manifest is in, or <see cref="NoManifestRotationEpoch" /> when there is no
    /// manifest row.
    /// </summary>
    public int RotationEpoch { get; } = RotationEpoch;

    /// <summary>
    /// The custody of an account that holds no manifest row: no bytes, epoch
    /// <see cref="NoManifestRotationEpoch" />, and whatever factors it does hold.
    /// </summary>
    /// <param name="factors">The account's factor envelopes, empty when it holds none.</param>
    /// <returns>The only well-formed spelling of an absent manifest.</returns>
    public static AccountKeyCustody WithNoManifest(IReadOnlyList<FactorEnvelopes> factors) =>
        new(null, NoManifestRotationEpoch, factors);

    // Answers the manifest it was handed, or throws. It returns rather than returning void so that it
    // can be the property's initializer: a guard called from somewhere else is a guard a later
    // constructor path can be added without.
    private static ReadOnlyMemory<byte>? Agreeing(ReadOnlyMemory<byte>? manifest, int rotationEpoch)
    {
        if (manifest is null)
        {
            // An epoch without bytes is the pairing this type spent a paragraph refusing and used to
            // accept: it would serialise as a generation number over a manifest naming nobody.
            if (rotationEpoch != NoManifestRotationEpoch)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(RotationEpoch),
                    rotationEpoch,
                    $"An absent manifest answers epoch {NoManifestRotationEpoch} and nothing else.");
            }

            return null;
        }

        ReadOnlyMemory<byte> bytes = manifest.Value;

        // Empty before wide, as FactorManifest.For orders the same two checks: both are about the one
        // column, and the empty buffer is the one that survives to the wire indistinguishable from the
        // absent value this type exists to keep separate.
        if (bytes.IsEmpty)
        {
            throw new ArgumentException(
                "A present manifest carries bytes; the absence of one is spelled null, never empty.",
                nameof(Manifest));
        }

        if (bytes.Length > FactorManifest.MaximumBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Manifest),
                bytes.Length,
                $"A manifest is at most {FactorManifest.MaximumBytes} bytes.");
        }

        if (rotationEpoch < FactorManifest.MinimumRotationEpoch)
        {
            throw new ArgumentOutOfRangeException(
                nameof(RotationEpoch),
                rotationEpoch,
                $"A stored manifest is at generation {FactorManifest.MinimumRotationEpoch} or above; "
                + $"{NoManifestRotationEpoch} is the absence of the row.");
        }

        return manifest;
    }
}
