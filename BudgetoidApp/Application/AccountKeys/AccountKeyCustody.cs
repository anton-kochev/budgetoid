namespace Application.AccountKeys;

/// <summary>
/// Everything one account holds on this server to open itself with, at the two levels it actually has:
/// the account's manifest of factor public keys and the generation that manifest is in, and one
/// <see cref="FactorEnvelopes" /> per recovery factor.
/// </summary>
/// <param name="Manifest">
/// The authenticated manifest bytes as <c>factor_manifests.manifest</c> holds them, or
/// <see langword="null" /> when the account has no manifest row. <b>It is <see langword="null" /> for
/// every account in every database</b> — the read exists, but nothing writes a manifest and the app role
/// holds no privilege that could — so a reader must not take a non-null value here as a state this
/// product can currently reach.
/// </param>
/// <param name="RotationEpoch">
/// Which generation of the manifest is in force, or <c>0</c> when there is no manifest row. <b>Zero is
/// the absence of the row and not an error</b>: <see cref="Domain.Users.FactorManifest.MinimumRotationEpoch" />
/// is 1 and a stored row is refused below it, so no stored generation can collide with the answer that
/// means "there is nothing stored". That is the pre-registration state and the state of every account
/// that exists today.
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
/// Reasoned, not run: nothing writes a manifest today, so the race is not reproducible yet, which is
/// exactly why the shape has to refuse it before the writer arrives.
/// </para>
/// <para>
/// <b>The pair is flat rather than a nullable nested <c>manifest</c> object, and the rejected shape has a
/// real argument.</b> Nesting would make the pairing unconstructible-wrong in one direction — no epoch
/// without bytes — which is worth something. What it costs is more: with no nested value there is no
/// epoch either, so every consumer would supply the <c>0</c> itself, and "absent means epoch 0" would be
/// restated once per edge instead of decided once by the implementation that is the only thing here
/// that sees the missing row. Neither shape is airtight — this one admits
/// <c>(null, 7)</c> and the nested one admits bytes at epoch 0 unless it repeats the domain's guard —
/// so the choice is made on where the default lives rather than on a safety neither buys.
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
public sealed record AccountKeyCustody(
    ReadOnlyMemory<byte>? Manifest,
    int RotationEpoch,
    IReadOnlyList<FactorEnvelopes> Factors);
