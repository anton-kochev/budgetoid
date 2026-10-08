namespace Application.AccountKeys;

/// <summary>
/// One recovery factor's stored share of the account: the factor identifier, that factor's wrapped
/// private key, and the account's two keys encapsulated to that factor's public key.
/// </summary>
/// <param name="FactorId">
/// The <c>wrapped_account_keys</c> row's primary key, minted by the client and deliberately <em>not</em>
/// <c>credentials.id</c>. It is the associated data of the wrapped private key, so it is handed
/// back exactly as it was stored and never re-spelled: a client that rebuilds the associated data from a
/// different rendering of the same UUID unwraps nothing, permanently, with no error naming the
/// cause.
/// </param>
/// <param name="WrappedPrivateKey">
/// The factor's ECDH P-256 private key, <em>wrapped under</em> the key-encryption key that factor
/// derives. Exactly <c>WrappedAccountKeys.WrappedPrivateKeyLength</c> bytes, carrying
/// <c>WrappedAccountKeys.WrappedPrivateKeyVersion</c> in the leading byte — both facts the database
/// already refuses a row for breaking, restated here only as what a reader may rely on.
/// </param>
/// <param name="EncapsulatedAccountKeys">
/// The account's content key and index key as one 64-byte plaintext, <em>encapsulated to</em> this
/// factor's public key. Exactly <c>WrappedAccountKeys.EncapsulatedAccountKeysLength</c> bytes, carrying
/// <c>WrappedAccountKeys.EncapsulatedAccountKeysVersion</c>.
/// </param>
/// <remarks>
/// <para>
/// <b>This is the per-<em>factor</em> level of a two-level answer, and the level above it is
/// <see cref="AccountKeyCustody" />.</b> A fact that is true once per account — the manifest naming the
/// whole factor set, the generation that manifest is in — belongs up there, where it is stored once and
/// cannot disagree with itself. A fact that varies per factor belongs here. The distinction is what
/// keeps this type at three members: the account-level facts now have a place, so the reader who would
/// have hung one off every row has somewhere better to put it, and a row that carried eleven copies of
/// one value would be eleven chances for a later two-statement write to make them differ.
/// </para>
/// <para>
/// <b>The two members can no longer be swapped into one another, and the hazard that replaces that one
/// sits a level in.</b> The pair this type used to carry were two AEAD envelopes of identical width
/// carrying an identical version byte, distinguished by nothing the schema could check, so the
/// paragraph that stood here warned a caller against transposing them. That warning is now dead text:
/// 167 bytes against 158, two framings, two version constants, and a transposition is refused by the
/// factory, by the decoders at the edge and by the columns' own check constraints. <b>What nothing on
/// this side can see is the order of the two halves inside <see cref="EncapsulatedAccountKeys" /></b> —
/// one 64-byte plaintext holding two 32-byte keys, <b>content key first</b>, which the server never
/// sees. A client that encapsulated them the other way round produces a value of exactly the right
/// width carrying exactly the right version, which stores, reads back, is handed to a browser by this
/// very read, and opens — yielding an index key used to seal narrative text and a content key used to
/// compute blind indexes. The order is a contract between clients and is held by nothing on this side,
/// ever. <c>WrappedAccountKeys</c> carries the argument in full.
/// </para>
/// <para>
/// <b>Bytes, not base64url text, and the asymmetry with the request types is the point.</b> The
/// Application ring carries an envelope as bytes and the API edge encodes it, which is the mirror of
/// <see cref="Passkeys.WrappedPrivateKeyEnvelope.TryDecode" /> and
/// <see cref="Passkeys.EncapsulatedAccountKeysEnvelope.TryDecode" /> on the write side: text arrives,
/// is decoded and bounded once at the boundary, and nothing below that boundary holds the wire
/// spelling. A <see cref="string" /> here would put base64url — an encoding this ring has no opinion
/// about — inside a read model, and would leave the write path and the read path with two different
/// ideas of what an envelope is.
/// </para>
/// <para>
/// <b>The value equality a record advertises does not reach the two payloads.</b> The synthesized
/// <see cref="object.Equals(object)" /> compares each member through
/// <see cref="EqualityComparer{T}.Default" />, which for a <see cref="ReadOnlyMemory{T}" /> is the
/// struct's own equality — the same buffer, the same offset, the same length — so two values holding
/// identical bytes in distinct arrays compare unequal and hash differently. That is precisely the
/// comparison <c>WrappedAccountKeysConfiguration</c> refuses to let EF's change tracker use on these two
/// columns, and it declares a content comparer to replace it; nothing does that for a record, and no
/// <c>with</c> expression or synthesized member here can. Compare <see cref="FactorId" />, or compare the
/// envelopes' spans element-wise; comparing two of these values whole answers a question about buffers
/// rather than about key material. <c>ExportedBudget</c> records the same gap for its collections, for
/// the same reason and with the same instruction.
/// </para>
/// <para>
/// <b>Nothing on this type can be opened by the server, and no member may be added that could be.</b>
/// The account's keys were generated in the browser and encapsulated to a public key whose private half
/// is itself wrapped under a key-encryption key derived from a factor this server has never seen — a
/// PRF output evaluated inside an authenticator, or a recovery code stored only as a digest of a
/// verifier. A member carrying an unwrapped key, a <em>private</em> key in the clear, a key-encryption
/// key, a PRF output or a recovery code would put the account's whole plaintext within the operator's
/// reach, and it would do so without failing a single test, because there is no test that can notice a
/// value the design says never arrives.
/// </para>
/// </remarks>
public sealed record FactorEnvelopes(
    Guid FactorId,
    ReadOnlyMemory<byte> WrappedPrivateKey,
    ReadOnlyMemory<byte> EncapsulatedAccountKeys);
