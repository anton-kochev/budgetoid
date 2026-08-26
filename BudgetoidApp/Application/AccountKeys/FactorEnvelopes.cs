namespace Application.AccountKeys;

/// <summary>
/// One recovery factor's stored share of the account's keys: the factor identifier both envelopes were
/// sealed against, and the two envelopes themselves.
/// </summary>
/// <param name="FactorId">
/// The <c>wrapped_account_keys</c> row's primary key, minted by the client and deliberately <em>not</em>
/// <c>credentials.id</c>. It is the value the associated data binds each envelope to, so it is handed
/// back exactly as it was stored and never re-spelled: a client that rebuilds the associated data from a
/// different rendering of the same UUID opens neither envelope, permanently, with no error naming the
/// cause.
/// </param>
/// <param name="WrappedContentKey">
/// The account's content key sealed under this factor's key-encryption key. Exactly
/// <c>WrappedAccountKeys.EnvelopeLength</c> bytes, carrying <c>WrappedAccountKeys.EnvelopeVersion</c> in
/// the leading byte — both facts the database already refuses a row for breaking, restated here only as
/// what a reader may rely on.
/// </param>
/// <param name="WrappedIndexKey">
/// The account's index key sealed under the <em>same</em> key-encryption key, under its own associated
/// data. The two columns are indistinguishable by width, by version and by every check the schema
/// carries; what separates them is the purpose bound into the associated data, so a caller that swaps
/// them hands the browser a pair that fails to authenticate rather than one that returns wrong bytes.
/// </param>
/// <remarks>
/// <para>
/// <b>Bytes, not base64url text, and the asymmetry with the request types is the point.</b> The
/// Application ring carries an envelope as bytes and the API edge encodes it, which is the mirror of
/// <see cref="Passkeys.WrappedKeyEnvelope.TryDecode" /> on the write side: text arrives, is decoded and
/// bounded once at the boundary, and nothing below that boundary holds the wire spelling. A
/// <see cref="string" /> here would put base64url — an encoding this ring has no opinion about — inside a
/// read model, and would leave the write path and the read path with two different ideas of what an
/// envelope is.
/// </para>
/// <para>
/// <b>The value equality a record advertises does not reach the two envelopes.</b> The synthesized
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
/// Both keys were generated in the browser and sealed under a key-encryption key derived from a factor
/// this server has never seen — a PRF output evaluated inside an authenticator, or a recovery code
/// stored only as a digest of a verifier. A member carrying an unwrapped key, a key-encryption key, a
/// PRF output or a recovery code would put the account's whole plaintext within the operator's reach,
/// and it would do so without failing a single test, because there is no test that can notice a value
/// the design says never arrives.
/// </para>
/// </remarks>
public sealed record FactorEnvelopes(
    Guid FactorId,
    ReadOnlyMemory<byte> WrappedContentKey,
    ReadOnlyMemory<byte> WrappedIndexKey);
