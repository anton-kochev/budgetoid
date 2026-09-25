using Domain.Common;

namespace Domain.Users;

public sealed class User
{
    private User()
    {
    }

    public Guid Id { get; private set; }
    public Email Email { get; private set; } = null!;
    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>
    /// Creates the account under an identifier the caller already holds — the only way an account comes
    /// into existence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The only legitimate source of <paramref name="id"/> is
    /// <c>Application.Registration.RegistrationAccountId.For</c></b>, called after the challenge store
    /// has confirmed it issued and spent exactly those bytes for the account-registration pool. That
    /// identifier is the WebAuthn user handle the authenticator was given when the ceremony opened, and
    /// an account row written under any other value answers no assertion that device will ever produce —
    /// silently, permanently, with no error naming the cause. Any other source is a caller choosing which
    /// account identifiers exist.
    /// </para>
    /// <para>
    /// <b>This is not a compile error, and reading it as one is the trap.</b> There used to be a
    /// second factory beside this one, minting the id itself, so that "which path may name an
    /// account" was a fact about the call site; six route groups carried a marker permitting them to
    /// reach it through a provisioning middleware. All of that is gone, and what the deletion buys is
    /// that a creating path now has to <em>name</em> the identifier it invents, in the diff a reviewer
    /// reads. It buys nothing else: this factory takes a plain <see cref="Guid"/>, so
    /// <c>CreateWithId(Guid.CreateVersion7(), …)</c> compiles, and five call sites across the test
    /// projects write exactly that on purpose. What holds "one path creates an account" is that
    /// <c>RegisterAccountHandler</c> is its only production caller — a fact somebody checks, not one
    /// the compiler does. Do not restore the minting factory to make a test or a seeding helper
    /// shorter, and do not tell the next reader the compiler is watching this.
    /// </para>
    /// </remarks>
    /// <param name="id">The derived account identifier. Never <see cref="Guid.Empty"/>.</param>
    /// <param name="email">The address the provider asserted for this account.</param>
    /// <param name="createdAtUtc">The instant the whole account comes into existence at.</param>
    /// <exception cref="ValidationException">
    /// The identifier is empty, or the address is not one this product accepts.
    /// </exception>
    public static User CreateWithId(Guid id, string email, DateTime createdAtUtc)
    {
        // Refused rather than replaced with a fresh uuid: all-zeros is what an unset field sends, and a
        // repair here would create the account under an identifier the authenticator was never handed —
        // the one failure this whole derivation exists to make unreachable. Judged before the address so
        // that a caller passing nothing at all is told about the identifier rather than about the email.
        if (id == Guid.Empty)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                [nameof(Id)] = ["Account id is required."],
            });
        }

        Email emailValue = Email.Create(email);

        return new User
        {
            Id = id,
            Email = emailValue,
            CreatedAtUtc = createdAtUtc,
        };
    }
}
