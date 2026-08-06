namespace Application.Passkeys.Verification;

/// <summary>
/// Verifies a <c>webauthn.get</c> ceremony response against a stored credential.
/// </summary>
/// <remarks>
/// Pure, like <see cref="PasskeyRegistrationVerifier"/>. Every refusal it returns is meant to reach
/// the client as the same 401 with the same sentence — the member is for the log, not the response.
/// </remarks>
public static class PasskeyAssertionVerifier
{
    public static PasskeyVerificationResult<VerifiedAssertion> Verify(
        ReadOnlyMemory<byte> clientDataJson,
        ReadOnlyMemory<byte> authenticatorDataBytes,
        ReadOnlyMemory<byte> signature,
        PasskeyAssertionExpectations expectations)
    {
        ArgumentNullException.ThrowIfNull(expectations);

        if (!CollectedClientData.Parse(clientDataJson)
                .TryGetValue(out CollectedClientData? clientData, out PasskeyVerificationFailure failure))
        {
            return Refused(failure);
        }

        PasskeyVerificationFailure? clientDataFailure = clientData.Verify(
            CollectedClientData.AuthenticationType,
            expectations.Challenge,
            expectations.AllowedOrigins);
        if (clientDataFailure is not null)
        {
            return Refused(clientDataFailure.Value);
        }

        if (!AuthenticatorData.Parse(authenticatorDataBytes)
                .TryGetValue(out AuthenticatorData? authenticatorData, out failure))
        {
            return Refused(failure);
        }

        if (!authenticatorData.MatchesRelyingParty(expectations.RelyingPartyId))
        {
            return Refused(PasskeyVerificationFailure.RelyingPartyMismatch);
        }

        if (!authenticatorData.IsUserPresent)
        {
            return Refused(PasskeyVerificationFailure.UserNotPresent);
        }

        if (!authenticatorData.IsUserVerified)
        {
            return Refused(PasskeyVerificationFailure.UserNotVerified);
        }

        // An assertion never carries attested credential data. One that does was assembled by
        // something other than the ceremony this endpoint verifies.
        if (authenticatorData.AttestedCredential is not null)
        {
            return Refused(PasskeyVerificationFailure.UnexpectedAttestedCredentialData);
        }

        // The bytes signed are the authenticator data exactly as received, not a re-serialisation of
        // what was parsed out of it above.
        PasskeyVerificationFailure? signatureFailure = PasskeySignatureVerifier.Verify(
            expectations.CoseKey,
            expectations.Algorithm,
            authenticatorDataBytes,
            clientDataJson,
            signature);
        if (signatureFailure is not null)
        {
            return Refused(signatureFailure.Value);
        }

        // Returned, not compared. Whether this counter may follow the stored one is the domain's
        // rule, and it is the domain that owns what a regression means.
        return PasskeyVerificationResult<VerifiedAssertion>.Verified(new VerifiedAssertion
        {
            SignCount = authenticatorData.SignCount,
        });
    }

    private static PasskeyVerificationResult<VerifiedAssertion> Refused(PasskeyVerificationFailure failure) =>
        PasskeyVerificationResult<VerifiedAssertion>.Refused(failure);
}
