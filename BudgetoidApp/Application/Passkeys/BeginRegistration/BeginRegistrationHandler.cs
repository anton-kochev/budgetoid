using Application.Abstractions;
using Application.Users;
using Domain.Users;

namespace Application.Passkeys.BeginRegistration;

/// <summary>
/// Issues a registration challenge and the credential creation options bound to it.
/// </summary>
/// <remarks>
/// Authenticated: a passkey is added to an account somebody is already signed in to, so the account
/// is never named by the request.
/// </remarks>
public sealed class BeginRegistrationHandler(
    IWebAuthnChallengeStore challengeStore,
    IPasskeyRepository passkeyRepository,
    IUserAccountReadService userAccountReadService,
    IUserContext userContext,
    IPasskeyCeremonyPolicy policy,
    TimeProvider timeProvider) : ICommandHandler<BeginRegistrationCommand, PasskeyCreationOptions>
{
    public async Task<PasskeyCreationOptions> HandleAsync(
        BeginRegistrationCommand command,
        CancellationToken cancellationToken = default)
    {
        Guid userId = userContext.UserId;

        // The account's own address is what the authenticator lists the credential under. Reading it
        // rather than accepting one from the request is the point: a name the caller supplied would
        // be a name they could put another person's address in, and it is shown at every later
        // sign-in as the account being reached.
        string email = await userAccountReadService.FindEmailAsync(userId, cancellationToken)
            ?? throw new InvalidOperationException(
                "The signed-in user has no stored account, so no registration ceremony can name one.");

        // Scoped to this user by the repository, which is load-bearing rather than tidy:
        // passkey_public_keys carries no row-level security policy (ADR 0012), so nothing beneath the
        // application narrows this read, and an unscoped one would hand this account every other
        // account's credential handles.
        IReadOnlyList<ReadOnlyMemory<byte>> registered =
            await passkeyRepository.ListWebAuthnCredentialIdsForUserAsync(userId, cancellationToken);

        IssuedChallenge issued = await challengeStore.IssueAsync(WebAuthnCeremony.Registration, cancellationToken);

        return new PasskeyCreationOptions
        {
            Challenge = PasskeyEncoding.Encode(issued.Challenge.Span),
            Rp = new PasskeyRelyingParty(policy.RelyingPartyId, policy.RelyingPartyName),
            User = new PasskeyUser(
                PasskeyEncoding.Encode(PasskeyEncoding.ToUserHandle(userId)),
                email,
                email),
            PubKeyCredParams = [.. PasskeyCeremonyConstants.OfferedAlgorithms.Select(
                algorithm => new PasskeyCredentialParameter(
                    PasskeyCeremonyConstants.PublicKeyCredentialType,
                    (int)algorithm))],
            Timeout = PasskeyCeremonyTimeout.Milliseconds(
                policy.CeremonyTimeout,
                issued.ExpiresAtUtc,
                timeProvider),
            Attestation = PasskeyCeremonyConstants.NoAttestation,
            AuthenticatorSelection = new PasskeyAuthenticatorSelection(
                PasskeyCeremonyConstants.RequiredResidentKey,
                RequireResidentKey: true,
                PasskeyCeremonyConstants.RequiredUserVerification),
            ExcludeCredentials =
            [
                .. registered.Select(credentialId => new PasskeyCredentialDescriptor(
                    PasskeyCeremonyConstants.PublicKeyCredentialType,
                    PasskeyEncoding.Encode(credentialId.Span))),
            ],
            Extensions = new PasskeyRegistrationExtensions(),
        };
    }
}
