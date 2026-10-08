using Domain.Security;

namespace Application.KeyRotations.ResealRows;

/// <summary>
/// One chunk of a content-key rotation: the rows a client has re-encrypted under the next generation's
/// keys, and the run it is continuing.
/// </summary>
/// <remarks>
/// <para>
/// <b>No account may be named here</b>, the rule <c>BeginKeyRotationCommand</c>, <c>RevokePasskeyCommand</c>
/// and <c>EraseAccountCommand</c> all state: the only identity the handler may act on is
/// <c>IUserContext.UserId</c>, because a user id declared on a command is an account a caller can
/// choose.
/// </para>
/// <para>
/// <b><see cref="RotationId" /> is the client-minted identifier the begin staged, and quoting it is
/// what makes this chunk part of that run.</b> It is not a secret and it is not proof of anything —
/// <c>ResealRowsHandler</c> checks it against the rotation the account actually has staged, because a
/// caller that named an abandoned run, or one no <c>key_rotations</c> row ever matched, would stamp
/// rows for a generation nobody staged and the completeness gate at the far end would then answer
/// <b>complete</b> for it.
/// </para>
/// <para>
/// <b>Five arms and no budget arm, which is a decision and not a gap.</b> A budget arm would need
/// <c>rotation_id</c> on the <c>budgets</c> <c>GRANT UPDATE</c> column list, and FR-099 requires the
/// application role to hold <c>UPDATE</c> on <c>budgets.name</c> and on no other column;
/// <c>Budget.ResealName</c> is <see langword="internal" /> to keep the refusal a compile error rather
/// than a runtime <c>42501</c>. <c>docs/business-logic/key-rotation.md</c> argues it.
/// </para>
/// <para>
/// <b>Lists rather than dictionaries keyed on the row, and the weaker-looking shape is the one that can
/// be refused.</b> It is <c>BeginKeyRotationCommand.Seals</c>' argument on a different identifier: a
/// dictionary makes a repeated row identifier unconstructible in C# and silently absorbs it on the wire
/// — JSON deserialisation drops a repeat, last wins — so a chunk naming one row twice under two
/// different envelopes would arrive as one with nothing anywhere saying so. Kept as lists, a repeat
/// survives into the handler, where it re-seals the same row twice in a row under the same stamp: the
/// second call wins, which is the same outcome the binder would have produced silently, but the request
/// is still the request the client sent.
/// </para>
/// <para>
/// <b>THIS IS NOT A REQUEST BODY AND MUST NEVER BE BOUND AS ONE.</b> It carries
/// <see cref="IndexedName" /> and <see cref="NarrativeField" /> — decoded, shape-checked Domain values
/// — where the sibling create commands (<c>CreatePayeeCommand</c> and its four siblings) carry the
/// wire's <see langword="string" />s and let their handler call <c>IndexedName.Of</c>. The shapes
/// differ because the decode sits in a different place, not because one of them is careless: this is
/// <c>BeginKeyRotationCommand.StagedManifest</c>'s arrangement, where the route decodes before the
/// command is built. So the route that carries a chunk owes its <b>own</b> request record of base64url
/// text, and owes the decode — <c>IndexedName.Of</c> and <c>NarrativeField.Sealed</c> raise the width
/// and framing refusals a 400 is made of, and they have to run before a value can reach this type at
/// all. Binding this record straight off the body does not merely skip those refusals; there is no
/// parameterless constructor and no converter for either value, so it fails as a deserialisation
/// error nobody can read.
/// </para>
/// <para>
/// <b>Nothing here is a key of any kind.</b> Every value is an AEAD envelope drawn under a content key
/// this server does not hold, or a blind index taken under an index key it does not hold either. A
/// member carrying either key would put the account's whole plaintext within reach of the operator
/// without reddening a test, because there is no test that can notice a value the design says never
/// arrives.
/// </para>
/// </remarks>
/// <param name="RotationId">The run this chunk continues, as the begin staged it.</param>
/// <param name="Accounts">The account rows this chunk re-seals.</param>
/// <param name="Payees">The payee rows this chunk re-seals.</param>
/// <param name="CategoryGroups">The category-group rows this chunk re-seals.</param>
/// <param name="Categories">The category rows this chunk re-seals.</param>
/// <param name="Transactions">The transaction rows this chunk re-seals.</param>
public sealed record ResealRowsCommand(
    Guid RotationId,
    IReadOnlyList<ResealedAccount> Accounts,
    IReadOnlyList<ResealedPayee> Payees,
    IReadOnlyList<ResealedCategoryGroup> CategoryGroups,
    IReadOnlyList<ResealedCategory> Categories,
    IReadOnlyList<ResealedTransaction> Transactions);

/// <summary>One account row, re-sealed.</summary>
/// <remarks>
/// <b>The new value and no stamp.</b> The stamp is <see cref="ResealRowsCommand.RotationId" />'s, one
/// level up, because a run stamps every row it rewrites with the same identifier — a per-entry stamp
/// would be a second place for a chunk to disagree with itself, and the disagreement would be invisible
/// until a completeness gate refused a run that looked finished.
/// </remarks>
/// <param name="Id">The row this entry re-seals. Minted by the client when the row was created, and the
/// associated data every envelope that row has ever held was sealed against.</param>
/// <param name="Name">The name under the next generation's content key, with its blind index under the
/// next generation's index key.</param>
public sealed record ResealedAccount(Guid Id, IndexedName Name);

/// <inheritdoc cref="ResealedAccount" />
/// <param name="Id"><inheritdoc cref="ResealedAccount" path="/param[@name='Id']" /></param>
/// <param name="Name"><inheritdoc cref="ResealedAccount" path="/param[@name='Name']" /></param>
public sealed record ResealedPayee(Guid Id, IndexedName Name);

/// <summary>One category-group row, re-sealed.</summary>
/// <remarks>
/// <b><paramref name="Description" /> is nullable and its absence is judged rather than obeyed.</b>
/// <c>NarrativeReseal.Resealed</c> refuses a chunk that changes whether the column holds a value in
/// either direction, so leaving it out of an entry whose row holds a note is a refusal and not a way to
/// skip the column.
/// </remarks>
/// <param name="Id"><inheritdoc cref="ResealedAccount" path="/param[@name='Id']" /></param>
/// <param name="Name"><inheritdoc cref="ResealedAccount" path="/param[@name='Name']" /></param>
/// <param name="Description">The note under the next generation's content key, or
/// <see langword="null" /> when the row holds none.</param>
public sealed record ResealedCategoryGroup(Guid Id, IndexedName Name, NarrativeField? Description);

/// <inheritdoc cref="ResealedCategoryGroup" />
/// <param name="Id"><inheritdoc cref="ResealedAccount" path="/param[@name='Id']" /></param>
/// <param name="Name"><inheritdoc cref="ResealedAccount" path="/param[@name='Name']" /></param>
/// <param name="Description">
/// <inheritdoc cref="ResealedCategoryGroup" path="/param[@name='Description']" />
/// </param>
public sealed record ResealedCategory(Guid Id, IndexedName Name, NarrativeField? Description);

/// <summary>One transaction row, re-sealed.</summary>
/// <remarks>
/// <b>A note and no name, because <c>transactions</c> has none.</b> It is the only arm whose whole
/// narrative is a nullable column, which is why a note-less transaction — what most rows of a real
/// account are — is a row a chunk never names at all rather than one it sends an empty entry for.
/// </remarks>
/// <param name="Id"><inheritdoc cref="ResealedAccount" path="/param[@name='Id']" /></param>
/// <param name="Description">
/// <inheritdoc cref="ResealedCategoryGroup" path="/param[@name='Description']" />
/// </param>
public sealed record ResealedTransaction(Guid Id, NarrativeField? Description);
