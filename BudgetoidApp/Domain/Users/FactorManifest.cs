using Domain.Common;

namespace Domain.Users;

/// <summary>
/// The account's manifest of every recovery factor's public key: one row per account, carrying the
/// authenticated bytes the client reads to learn which factors exist and what to encapsulate to, with
/// <see cref="RotationEpoch"/> counting the generations the manifest has been through.
/// </summary>
/// <remarks>
/// <para>
/// <b>The sole carrier of every factor's public key, and that is a decision rather than an
/// omission.</b> Every recovery factor holds an ECDH P-256 key pair: the factor's private key is
/// <em>wrapped under</em> the key-encryption key that factor already derives, and the account's content
/// and index keys are <em>encapsulated to</em> the factor's public key. There is deliberately no
/// per-row public key column beside them — <see cref="WrappedAccountKeys"/>,
/// <see cref="RecoveryCodeHash"/> and <see cref="PasskeyPublicKey"/> carry none, and
/// <see cref="PasskeyPublicKey.CoseKey"/> is a different key for a different job: the WebAuthn key an
/// assertion verifies against, not a key anything is encapsulated to. The set is authenticated as one
/// blob because what has to be unforgeable is the <em>set</em>: a per-row column is a row at a time, so
/// an added, removed or swapped row would have to carry its own authentication, and the client choosing
/// what to encapsulate to would have no way to ask whether it was looking at all of them.
/// </para>
/// <para>
/// <b>Three verbs, and they are not interchangeable</b> — <em>sealed under</em> a key over data,
/// <em>wrapped under</em> a key over another key, <em>encapsulated to</em> a public key. A manifest
/// carries the third kind of value, and only that kind.
/// </para>
/// <para>
/// <b>Nothing here takes an unwrapped key, a private key, a key-encryption key or a PRF output</b>, the
/// rule <see cref="WrappedAccountKeys"/> states at length. A manifest carries <em>public</em> keys, so
/// its bytes — like <see cref="PasskeyPublicKey.CoseKey"/> next door — are key material the server may
/// hold in the clear; a member accepting a private one would put the whole account's plaintext within
/// reach of the operator, and it would do so without reddening a single test, because there is no test
/// that can notice a value the design says never arrives.
/// </para>
/// <para>
/// <b>This floor is not the whole epoch rule, and a reader must not take it for one.</b> The
/// transition rule — a promotion writes an epoch <em>exactly one greater</em> than the one it read — is
/// held by no declarative layer. A <c>CHECK</c> constraint sees the values of one row and not the step
/// between two, and a trigger is the procedural logic
/// <see href="../../../docs/decisions/0002-enforce-rules-at-the-lowest-capable-layer.md">ADR 0002</see>
/// forbids pushing down to buy the phrase "the database enforces it". The arithmetic is application
/// code and nothing below it will notice if it goes wrong — so the application-side check is not a
/// restatement of a database rule and deleting it does not fall back on one.
/// </para>
/// <para>
/// <b>The <em>atomicity</em> half — that nobody moved the epoch between the read and the write — is
/// held by nothing either, and that is deliberate until this table has a <em>promoting</em> writer.</b>
/// EF optimistic concurrency on <see cref="RotationEpoch"/> is what will hold it, and it is not
/// configured yet. Registration now writes the first manifest, at
/// <see cref="MinimumRotationEpoch"/>, in the same save as the account — but that is an INSERT of a row
/// keyed on an account identifier this server has just derived, so there is no epoch to have moved and
/// nothing for a token to compare. The app role holds INSERT and no UPDATE of any shape, so a token
/// today would still guard a statement nobody can issue. It is also the wrong shape ahead
/// of its caller — <see cref="For"/> returns a detached instance, so the obvious promotion
/// (<c>For(user, bytes, epoch + 1)</c> then <c>Update</c>) hands EF a row whose original values are its
/// current ones, and the predicate compares the new epoch against itself. Adding it later is free: a
/// token on an <see langword="int"/> has no relational artifact, so it belongs in the commit that
/// brings the handler catching <c>DbUpdateConcurrencyException</c> and the test reproducing the race.
/// </para>
/// <para>
/// <b>Both constants stay <see langword="const"/>, and the tests deliberately do not read them.</b>
/// A <see langword="const"/> because each is a fixed fact about its column rather than anything
/// computed, and because a constant expression is what an <c>[Arguments(...)]</c> argument, a default
/// parameter value and a persistence check constraint can be written from — the positions
/// <see cref="WrappedAccountKeys.WrappedPrivateKeyLength"/> is already read from. <c>FactorManifestTests</c>
/// writes <c>4096</c>, <c>4097</c>, <c>1</c>, <c>0</c> and <c>-1</c> out as literals of its own instead,
/// which is the only reason a drift in either constant is catchable: a test that read the constant it
/// checks would move with an edit to it and compare a constant with itself.
/// </para>
/// </remarks>
public sealed class FactorManifest
{
    /// <summary>The widest manifest this column will hold.</summary>
    /// <remarks>
    /// <b>Refused, never truncated.</b> Truncation is not a safe repair here, because this blob is the
    /// sole carrier of every factor's public key: cutting it at the line silently drops whichever
    /// factor fell past it. That factor stops being encapsulatable-to, the person loses a way back into
    /// the account, and nothing about the stored row says so — the manifest is well-formed, the epoch
    /// is plausible, and the loss surfaces on the day somebody reaches for the factor that is gone. A
    /// cap rather than a width, unlike <see cref="WrappedAccountKeys.WrappedPrivateKeyLength"/>: a manifest
    /// grows with the number of factors an account has, so only the upper bound is a fact about it.
    /// </remarks>
    public const int MaximumBytes = 4096;

    /// <summary>The epoch a first manifest is written under — generations count from one, not zero.</summary>
    /// <remarks>
    /// <b>Epoch 0 is the <em>absence</em> of a row.</b> An account with no manifest row answers epoch
    /// 0 — that is the pre-registration state and the state of every account that exists today, and it
    /// is not an error. A stored row claiming epoch 0 would therefore assert its own absence, and "this
    /// account has never rotated" and "this account rotated to generation zero" would become
    /// indistinguishable to the one read that has to tell them apart. Counting from one costs nothing
    /// and keeps the two answers apart; the negative side is refused with it because no generation has
    /// a number below the first.
    /// </remarks>
    public const int MinimumRotationEpoch = 1;

    private FactorManifest()
    {
    }

    /// <summary>The account the manifest belongs to, and the primary key: one manifest per account.</summary>
    public Guid UserId { get; private set; }

    /// <summary>The authenticated manifest bytes — every factor's public key, as the client wrote them.</summary>
    public ReadOnlyMemory<byte> Manifest { get; private set; }

    /// <summary>Which generation of the manifest this row holds.</summary>
    public int RotationEpoch { get; private set; }

    /// <summary>
    /// Files <paramref name="manifest"/> as <paramref name="user"/>'s manifest of factor public keys.
    /// </summary>
    /// <remarks>
    /// <b>It takes the loaded user rather than a loose <see cref="Guid"/>, and what that buys is
    /// narrower than what the siblings' signatures buy.</b> It is type safety and nothing more: a
    /// <see cref="Guid"/> parameter takes a credential id, a factor id or a budget id without
    /// complaint, and at the point this is called every one of those is in the caller's hand. It does
    /// <em>not</em> stop one account's material being filed against another — a caller reaches a
    /// <see cref="User"/> by reading one keyed on a user id, so an id transposed upstream arrives here
    /// as a perfectly well-typed user. <see cref="WrappedAccountKeys.For"/> and
    /// <see cref="RecoveryCodeHash.From"/> can claim more because a credential carries three columns
    /// this row copies and a composite foreign key compares all three; one id read off one object
    /// compares with nothing. Nor is the idiom universal in this aggregate:
    /// <see cref="Credential.CreatePasskey"/> takes a loose <see cref="Guid"/> owner id.
    /// </remarks>
    /// <exception cref="ArgumentNullException">No user was supplied.</exception>
    /// <exception cref="ValidationException">
    /// The manifest is empty or wider than <see cref="MaximumBytes"/>, or the epoch is below
    /// <see cref="MinimumRotationEpoch"/>.
    /// </exception>
    public static FactorManifest For(User user, ReadOnlyMemory<byte> manifest, int rotationEpoch)
    {
        // The owner is read off the user, so there is nothing to file without one. No user typed this;
        // a caller handed over nothing.
        ArgumentNullException.ThrowIfNull(user);

        // Collected rather than thrown one at a time, and keyed on the property the value lands in, as
        // WrappedAccountKeys.For and KeyRotation.Begin both key theirs.
        Dictionary<string, string[]> errors = new();

        // Emptiness before width, and the two are one key because they are one column. An empty bytea
        // is exactly what an unset member sends, so a caller that forgot to attach the manifest would
        // otherwise file a row naming no factor at all — an account with no way back in, stored as
        // though it had one. Written as an else-if because a value cannot be both, which is what keeps
        // the second assignment from overwriting the first.
        if (manifest.IsEmpty)
        {
            errors[nameof(Manifest)] = ["A factor manifest is required."];
        }
        else if (manifest.Length > MaximumBytes)
        {
            errors[nameof(Manifest)] =
                [$"A factor manifest must be at most {MaximumBytes} bytes."];
        }

        // Below the floor, not merely at zero: a negative epoch names no generation either, and one
        // comparison refuses both without the guard having to enumerate them.
        if (rotationEpoch < MinimumRotationEpoch)
        {
            errors[nameof(RotationEpoch)] =
                [$"A manifest's rotation epoch must be at least {MinimumRotationEpoch}."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        return new FactorManifest
        {
            UserId = user.Id,

            // Copied, not aliased, the rule WrappedAccountKeys.For keeps for its two envelopes. A
            // ReadOnlyMemory<byte> is a view over a buffer the caller still owns, and a buffer reused
            // for the next write would rewrite a manifest that has already been accepted.
            Manifest = manifest.ToArray(),
            RotationEpoch = rotationEpoch,
        };
    }
}
