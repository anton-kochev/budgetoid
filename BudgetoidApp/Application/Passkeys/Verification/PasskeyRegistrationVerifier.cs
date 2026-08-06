using Domain.Users;

namespace Application.Passkeys.Verification;

/// <summary>
/// Verifies a <c>webauthn.create</c> ceremony response.
/// </summary>
/// <remarks>
/// Pure: no I/O, no repository, no clock. Everything it judges against arrives in
/// <see cref="PasskeyRegistrationExpectations"/>, which is what makes every branch below a unit test
/// that runs in milliseconds.
/// </remarks>
public static class PasskeyRegistrationVerifier
{
    public static PasskeyVerificationResult<VerifiedRegistration> Verify(
        ReadOnlyMemory<byte> clientDataJson,
        ReadOnlyMemory<byte> attestationObject,
        PasskeyRegistrationExpectations expectations)
    {
        ArgumentNullException.ThrowIfNull(expectations);

        if (!CollectedClientData.Parse(clientDataJson)
                .TryGetValue(out CollectedClientData? clientData, out PasskeyVerificationFailure failure))
        {
            return Refused(failure);
        }

        PasskeyVerificationFailure? clientDataFailure = clientData.Verify(
            CollectedClientData.RegistrationType,
            expectations.Challenge,
            expectations.AllowedOrigins);
        if (clientDataFailure is not null)
        {
            return Refused(clientDataFailure.Value);
        }

        if (!AttestationObject.Parse(attestationObject)
                .TryGetValue(out AttestationObject? attestation, out failure))
        {
            return Refused(failure);
        }

        // Refused, not ignored. Any other format carries an attestation statement this product has
        // no trust anchor for, and storing an unverified statement is worse than storing none.
        if (!string.Equals(attestation.Format, AttestationObject.NoneFormat, StringComparison.Ordinal)
            || attestation.HasAttestationStatement)
        {
            return Refused(PasskeyVerificationFailure.UnsupportedAttestationFormat);
        }

        if (!AuthenticatorData.Parse(attestation.AuthenticatorData)
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

        if (authenticatorData.AttestedCredential is not { } attestedCredential)
        {
            return Refused(PasskeyVerificationFailure.AttestedCredentialDataMissing);
        }

        if (attestedCredential.CredentialId.Length is < PasskeyPublicKey.MinWebAuthnCredentialIdLength
            or > PasskeyPublicKey.MaxWebAuthnCredentialIdLength)
        {
            return Refused(PasskeyVerificationFailure.CredentialIdOutOfRange);
        }

        if (!CoseKeyMaterial.Decode(attestedCredential.CoseKey)
                .TryGetValue(out CoseKeyMaterial? coseKey, out failure))
        {
            return Refused(failure);
        }

        // Supported is not the same as offered: the ceremony's options named a set, and an
        // authenticator answering outside it is answering a question nobody asked.
        if (!expectations.OfferedAlgorithms.Contains(coseKey.Algorithm))
        {
            return Refused(PasskeyVerificationFailure.AlgorithmNotOffered);
        }

        return PasskeyVerificationResult<VerifiedRegistration>.Verified(new VerifiedRegistration
        {
            WebAuthnCredentialId = attestedCredential.CredentialId,
            CoseKey = attestedCredential.CoseKey,
            Algorithm = coseKey.Algorithm,
            SignCount = authenticatorData.SignCount,
        });
    }

    private static PasskeyVerificationResult<VerifiedRegistration> Refused(PasskeyVerificationFailure failure) =>
        PasskeyVerificationResult<VerifiedRegistration>.Refused(failure);
}
