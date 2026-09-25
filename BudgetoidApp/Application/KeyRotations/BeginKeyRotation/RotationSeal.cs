using Domain.Users;

namespace Application.KeyRotations.BeginKeyRotation;

/// <summary>
/// One factor's copy of the next generation's account keys: the new content key and index key as one
/// plaintext, <em>encapsulated to</em> that factor's public key.
/// </summary>
/// <remarks>
/// <para>
/// <b>It travels in both directions and it is one type, because it is one value.</b> A begin presents
/// these and the resume read hands the staged ones back —
/// <c>Application.KeyRotations.GetKeyRotationState.StagedKeyRotation</c> carries them out again, since
/// until a completion promotes them the staged seals are the only copies of that generation anywhere.
/// A second record for the outbound leg would be the same two members under another name, able to
/// disagree with this one about what a seal is.
/// </para>
/// <para>
/// <b>The wire shape of a <see cref="KeyRotationSeal"/> and deliberately not that type.</b> The entity is
/// built by <see cref="KeyRotationSeal.For"/>, which reads the owner off the rotation and the factor off a
/// <em>loaded</em> <see cref="WrappedAccountKeys"/> row so that it can refuse a disagreement between the
/// two; a command carrying the entity would have to mint one before anything had been loaded, which is
/// exactly the refusal that factory exists to make. So this record carries what a caller can honestly
/// say — which factor, and which bytes — and the handler turns the pair into an entity after it has
/// established that the account holds that factor.
/// </para>
/// <para>
/// <b>Nothing here is an unwrapped key, a private key, a key-encryption key or a PRF output</b>, the rule
/// <see cref="BeginKeyRotationCommand"/> states for the whole request. An encapsulated value is ciphertext
/// under a <em>public</em> key, which the server may hold in the clear and can open with nothing it has.
/// A member carrying the private half would put the account's whole plaintext within reach of the
/// operator without reddening a test, because there is no test that can notice a value the design says
/// never arrives.
/// </para>
/// <para>
/// <b>Nothing judges the bytes here.</b> Width and framing version are
/// <see cref="KeyRotationSeal.For"/>'s to refuse, read off
/// <see cref="WrappedAccountKeys.EncapsulatedAccountKeysLength"/> and
/// <see cref="WrappedAccountKeys.EncapsulatedAccountKeysVersion"/> — the column a promotion copies this
/// value into — so a second statement of either number here would be one fact able to disagree with
/// itself.
/// </para>
/// </remarks>
/// <param name="FactorId">
/// The factor this copy was encapsulated to — the <see cref="WrappedAccountKeys.FactorId"/> of the row a
/// promotion will write it into.
/// </param>
/// <param name="EncapsulatedAccountKeys">
/// The next generation's content key and index key as one 64-byte plaintext, <b>content key first</b>,
/// encapsulated to that factor's public key. The order of the two halves is a client contract this server
/// cannot check and never will be able to — <see cref="WrappedAccountKeys"/> spells out what a reversed
/// pair costs.
/// </param>
public sealed record RotationSeal(Guid FactorId, ReadOnlyMemory<byte> EncapsulatedAccountKeys);
