using System.Security.Cryptography;
using Domain.Common;

namespace Domain.Users;

/// <summary>
/// One unredeemed recovery code, stored as the SHA-256 of the verifier the person was shown rather
/// than as anything the verifier can be recovered from.
/// </summary>
/// <remarks>
/// <para>
/// Its own entity referencing its credential and its user by id, for the reason
/// <see cref="PasskeyPublicKey"/> gives: hanging it off <see cref="User"/> would grow a root that is
/// loaded on every authenticated request. One row per code and one <see cref="Credential"/> per
/// <i>set</i>, so redeeming a code deletes a row while the set — and the account's ability to redeem
/// the rest — survives.
/// </para>
/// <para>
/// <b>The server never sees a code.</b> The browser mints one, derives a verifier
/// <c>V = HKDF(code, …)</c> from it and sends only <c>V</c>; <see cref="From"/> stores
/// <c>SHA-256(V)</c>. The account's key-encryption key is derived from the same code on an
/// independent HKDF branch, so a code reaching this server would hand the operator that key — which
/// is why nothing on this type takes a code, and why the hashing happens inside the factory rather
/// than at a call site somebody could get wrong once.
/// </para>
/// </remarks>
public sealed class RecoveryCodeHash
{
    /// <summary>
    /// The exact width of the verifier the client derives, and the only thing about it this layer can
    /// judge.
    /// </summary>
    /// <remarks>
    /// The server cannot measure entropy: it receives fixed-length opaque bytes, and a set of
    /// identical zero-filled verifiers is indistinguishable here from a set a good generator produced.
    /// Entropy is a client-side property verified by a client-side test. Width is not, and the bound
    /// is refused from <b>both</b> sides. Short is a shorter secret than the design claims, and it
    /// would hash to a perfectly well-formed 32-byte row nothing downstream could tell from a real one
    /// — <c>CK_recovery_code_hashes_verifier_hash_length</c> watches the <em>hash</em>, which is 32
    /// bytes whatever went into it, so the database cannot catch a short verifier and this factory is
    /// the only place it stops. Long means the client and the server disagree about what a verifier
    /// is, and since the same code also derives the key-encryption key, a width quietly accepted here
    /// surfaces much later as a key that will not unwrap. Refused, never truncated: truncating would
    /// store the hash of a prefix, and no code would ever redeem.
    /// <para>
    /// Numerically equal to the width of the SHA-256 this type stores, and that is arithmetic rather
    /// than a shared bound — the HKDF output width the client is specified to produce is chosen
    /// independently of the digest length. <c>RecoveryCodeHashConfiguration</c> keeps its own constant
    /// for the column for that reason, and the two must not be folded together.
    /// </para>
    /// </remarks>
    public const int VerifierLength = 32;

    private RecoveryCodeHash()
    {
    }

    /// <summary>
    /// The SHA-256 of the verifier, and the identity of the row: an anonymous redemption request
    /// arrives carrying a code and nothing else, so this is what the row is found by.
    /// </summary>
    public ReadOnlyMemory<byte> VerifierHash { get; private set; }

    /// <summary>The credential standing for the whole set this code belongs to.</summary>
    public Guid CredentialId { get; private set; }

    public Guid UserId { get; private set; }

    /// <summary>
    /// The type of the credential this code belongs to, carried on the row for the reason
    /// <see cref="PasskeyPublicKey.CredentialType"/> gives.
    /// </summary>
    public CredentialType CredentialType { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>
    /// Files one code of <paramref name="credential"/>'s set, storing the hash of
    /// <paramref name="verifier"/> and never the verifier.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It takes the credential rather than three loose ids</b>, the argument
    /// <see cref="Domain.Sessions.Session.Establish"/> and <see cref="PasskeyPublicKey.Register"/>
    /// both make, and it is sharper here than on either of them. All three copied columns are compared
    /// against <c>credentials(id, user_id, type)</c> by a composite foreign key, so a row whose owner
    /// disagreed with its credential's is unstorable — but what makes that matter is the case where it
    /// <em>is</em> storable: a redemption arrives <b>anonymous</b> and adopts the <c>user_id</c> it
    /// finds on this row, and <c>recovery_code_hashes</c> is exempt from row-level security, so
    /// nothing beneath the application is watching. A factory taking three ids is one transposed
    /// argument away from handing a redeemer somebody else's account.
    /// </para>
    /// <para>
    /// <b>It hashes internally</b>, so no shape of this call stores an unhashed value. A factory
    /// taking a hash the caller computed would mean a raw verifier could be assigned to an object
    /// something can persist, and every call site would be a place to get it wrong once.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">No credential was supplied.</exception>
    /// <exception cref="ValidationException">
    /// The credential does not stand for a set of recovery codes, or the verifier is not
    /// <see cref="VerifierLength"/> bytes.
    /// </exception>
    public static RecoveryCodeHash From(
        Credential credential,
        ReadOnlyMemory<byte> verifier,
        DateTime createdAtUtc)
    {
        // The owner and the type are both read off the credential, so there is nothing to validate
        // without one. No user typed this; a caller handed over nothing.
        ArgumentNullException.ThrowIfNull(credential);

        Dictionary<string, string[]> errors = new();

        // Enumerated as "must be RecoveryCodes" rather than as "must not be a passkey", which is the
        // shape a reader copying PasskeyPublicKey.Register backwards would produce and which would let
        // a code hang off the federated credential the identity provider owns — the one credential
        // type whose session may not reach budget content at all. CK_credentials_type_shape cannot
        // tell a set of codes from a passkey either: its recovery_codes and passkey arms read the same
        // predicate, so the type spelling is the only thing separating them for everything downstream.
        if (credential.Type != CredentialType.RecoveryCodes)
        {
            errors[nameof(CredentialType)] =
                ["A recovery code may only be filed against a credential standing for a set of recovery codes."];
        }

        // Keyed on the property the value ends up in, as PasskeyPublicKey.Register keys its own length
        // refusals. See VerifierLength for why both sides of the bound are refused.
        if (verifier.Length != VerifierLength)
        {
            errors[nameof(VerifierHash)] = [$"Verifier must be exactly {VerifierLength} bytes."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        return new RecoveryCodeHash
        {
            // HashOf and nothing else, so the two spellings of "the hash of a verifier" cannot drift:
            // see the remarks there.
            VerifierHash = HashOf(verifier),
            CredentialId = credential.Id,
            UserId = credential.UserId,
            CredentialType = credential.Type,
            CreatedAtUtc = createdAtUtc,
        };
    }

    /// <summary>
    /// The value a verifier is stored under — the same hash <see cref="From"/> computes, without an
    /// entity to compute it against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Unsalted SHA-256, and neither half of that is an omission.</b>
    /// </para>
    /// <para>
    /// <b>No salt</b>, because a redemption arrives carrying a verifier and no identity at all — no
    /// credential, no account — so the row has to be findable by its hash alone. A per-row salt is a
    /// value the lookup cannot know before it has found the row it needs the salt to find, and
    /// anything else per-call (a nonce, a keyed MAC over a deployment secret) turns the lookup into a
    /// query with no argument to give it. That is also why this member exists at all rather than
    /// callers going through <see cref="From"/>: the redemption has no credential to hand it.
    /// </para>
    /// <para>
    /// <b>No slow KDF, and no package.</b> A future reader will want to harden this to Argon2 or
    /// PBKDF2. Those exist to make a <em>guessable</em> input expensive to enumerate; the input here
    /// is a uniform 256-bit value the client derived, so there is no dictionary to slow down and the
    /// work factor would buy nothing but latency on a request that already holds the account. Adding a
    /// hashing package would also move a pinned row in <c>ProjectReferenceGraphTests</c> and put a
    /// third-party dependency on <c>Domain</c>, which declares none.
    /// </para>
    /// <para>
    /// <b>The two spellings must agree or no recovery code in the system ever redeems</b>, and the
    /// symptom is silent: every code the account was ever issued simply stops matching. That is why
    /// <see cref="From"/> calls this rather than repeating the digest.
    /// </para>
    /// </remarks>
    public static ReadOnlyMemory<byte> HashOf(ReadOnlyMemory<byte> verifier) =>
        // SHA256.HashData allocates the result, so — unlike PasskeyPublicKey.Register, which copies a
        // buffer the caller still owns — there is nothing here aliasing the argument.
        SHA256.HashData(verifier.Span);
}
