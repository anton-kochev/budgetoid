using System.Security.Cryptography;
using System.Text;
using Application.Passkeys.Verification;
using Domain.Users;
using TestSupport;

namespace UnitTests;

public sealed class PasskeyVerificationTests
{
    // Both golden vectors below were taken from SimpleWebAuthn, github.com/MasterKale/SimpleWebAuthn,
    // MIT licensed, from master at commit b2f39ba6380d34d4b2625ba16debf2f177be0655:
    //
    //   Vector A — packages/server/src/registration/verifyRegistrationResponse.test.ts,
    //              the attestationNone fixture.
    //   Vector B — packages/server/src/authentication/verifyAuthenticationResponse.test.ts,
    //              the assertionResponse fixture.
    //
    // The provenance is the whole point of the vectors, not paperwork about them. These bytes exist to
    // catch a misunderstanding of the wire format that the verifier and this repository's synthetic
    // authenticator happen to share, and only a response produced by code nothing here wrote can do
    // that. A vector whose origin is not recorded cannot be told apart from one this repository's own
    // WebAuthnWireFormat synthesized, so an unattributed vector silently stops being a defence against
    // a shared belief while still looking like one. Recording where it came from is what keeps it a
    // second opinion.
    //
    // Attribution is also an obligation rather than a courtesy: the MIT licence these were published
    // under requires the copyright and permission notice to travel with the copied material, and this
    // repository is AGPL-3.0, so the two licences have to be visibly distinguishable at the point the
    // borrowed bytes sit. That obligation is discharged in THIRD-PARTY-NOTICES.md at the repository
    // root, which carries the copyright line (Copyright (c) 2020 Matthew Miller) and the full MIT
    // permission notice, and names this file as the one holding the borrowed material.

    // Vector A — a captured webauthn.create response, none attestation, ES256.
    private const string GoldenRegistrationAttestationObject =
        "o2NmbXRkbm9uZWdhdHRTdG10oGhhdXRoRGF0YVjFPdxHEOnAiLIp26idVjIguzn3Ipr_RlsKZWsa-5qK-KBFAAAAAAAAAAA"
        + "AAAAAAAAAAAAAAAAAQQHSlyRHIdWleVqO24-6ix7JFWODqDWo_arvEz3Se5EgIFHkcVjZ4F5XDSBreIHsWRilRnKmaaql"
        + "qK3V2_4XtYs2pQECAyYgASFYID5PQTZQQg6haZFQWFzqfAOyQ_ENsMH8xxQ4GRiNPsqrIlggU8IVUOV8qpgk_Jh-OTaLu"
        + "ZL52KdX1fTht07X4DiQPow";

    private const string GoldenRegistrationClientDataJson =
        "eyJ0eXBlIjoid2ViYXV0aG4uY3JlYXRlIiwiY2hhbGxlbmdlIjoiYUVWalkxQlhkWHBwVURBd1NEQndOV2Q0YURKZmRUVm"
        + "ZVRU0wVG1WWloyUSIsIm9yaWdpbiI6Imh0dHBzOlwvXC9kZXYuZG9udG5lZWRhLnB3IiwiYW5kcm9pZFBhY2thZ2VOYW"
        + "1lIjoib3JnLm1vemlsbGEuZmlyZWZveCJ9";

    private const string GoldenRegistrationChallenge = "aEVjY1BXdXppUDAwSDBwNWd4aDJfdTVfUEM0TmVZZ2Q";

    private const string GoldenRegistrationCredentialId =
        "AdKXJEch1aV5Wo7bj7qLHskVY4OoNaj9qu8TPdJ7kSAgUeRxWNngXlcNIGt4gexZGKVGcqZpqqWordXb_he1izY";

    private const string GoldenRegistrationCoseKey =
        "pQECAyYgASFYID5PQTZQQg6haZFQWFzqfAOyQ_ENsMH8xxQ4GRiNPsqrIlggU8IVUOV8qpgk_Jh-OTaLuZL52KdX1fTht0"
        + "7X4DiQPow";

    private const int GoldenRegistrationCredentialIdLength = 65;

    // Vector B — a captured webauthn.get response from a roaming key, ES256, DER signature.
    private const string GoldenAssertionAuthenticatorData = "PdxHEOnAiLIp26idVjIguzn3Ipr_RlsKZWsa-5qK-KABAAAAkA";

    private const string GoldenAssertionClientDataJson =
        "eyJjaGFsbGVuZ2UiOiJkRzkwWVd4c2VWVnVhWEYxWlZaaGJIVmxSWFpsY25sVWFXMWwiLCJjbGllbnRFeHRlbnNpb25zIj"
        + "p7fSwiaGFzaEFsZ29yaXRobSI6IlNIQS0yNTYiLCJvcmlnaW4iOiJodHRwczovL2Rldi5kb250bmVlZGEucHciLCJ0eX"
        + "BlIjoid2ViYXV0aG4uZ2V0In0";

    private const string GoldenAssertionSignature =
        "MEUCIQDYXBOpCWSWq2Ll4558GJKD2RoWg958lvJSB_GdeokxogIgWuEVQ7ee6AswQY0OsuQ6y8Ks6jhd45bDx92wjXKs900";

    private const string GoldenAssertionCoseKey =
        "pQECAyYgASFYIIheFp-u6GvFT2LNGovf3ZrT0iFVBsA_76rRysxRG9A1Ilgg8WGeA6hPmnab0HAViUYVRkwTNcN77QBf_R"
        + "R0dv3lIvQ";

    private const string GoldenAssertionChallenge = "dG90YWxseVVuaXF1ZVZhbHVlRXZlcnlUaW1l";

    private const uint GoldenAssertionSignCount = 144;

    private const string GoldenRelyingPartyId = "dev.dontneeda.pw";

    private const string GoldenOrigin = "https://dev.dontneeda.pw";

    // The relying party this product is, used by every synthetic test so the prefix-match case reads
    // against the real name rather than a placeholder.
    private const string RelyingPartyId = "budgetoid.app";

    private const string Origin = "https://budgetoid.app";

    [Test]
    public async Task Registration_WithAGoldenVector_IsAccepted()
    {
        // This is the one test in this file that the synthetic authenticator cannot stand in for.
        // The synthetic device is a second implementation of the same wire format written in this
        // repository, so any misunderstanding of the format that it and the verifier happen to share
        // passes every other test here. A response captured from a real authenticator, produced by
        // code nothing in this repository wrote, is the only thing that cannot share such a mistake.

        // Arrange
        byte[] clientDataJson = Base64UrlText.Decode(GoldenRegistrationClientDataJson);
        byte[] attestationObjectBytes = Base64UrlText.Decode(GoldenRegistrationAttestationObject);
        PasskeyRegistrationExpectations expectations = new()
        {
            Challenge = Base64UrlText.Decode(GoldenRegistrationChallenge),
            AllowedOrigins = [GoldenOrigin],
            RelyingPartyId = GoldenRelyingPartyId,
            OfferedAlgorithms = [CoseAlgorithm.Es256, CoseAlgorithm.Rs256],
        };

        // Act
        PasskeyVerificationResult<VerifiedRegistration> result =
            PasskeyRegistrationVerifier.Verify(clientDataJson, attestationObjectBytes, expectations);
        PasskeyVerificationResult<AttestationObject> attestation =
            AttestationObject.Parse(attestationObjectBytes);

        // Assert
        await Assert.That(attestation.IsVerified).IsTrue();
        await Assert.That(attestation.TryGetValue(out AttestationObject? parsed, out _)).IsTrue();
        await Assert.That(parsed!.Format).IsEqualTo(AttestationObject.NoneFormat);
        await Assert.That(parsed.HasAttestationStatement).IsFalse();

        await Assert.That(result.Failure).IsNull();
        await Assert.That(result.TryGetValue(out VerifiedRegistration? registration, out _)).IsTrue();
        await Assert.That(Base64UrlText.Encode(registration!.WebAuthnCredentialId.Span))
            .IsEqualTo(GoldenRegistrationCredentialId);
        await Assert.That(registration.WebAuthnCredentialId.Length).IsEqualTo(GoldenRegistrationCredentialIdLength);
        await Assert.That(Base64UrlText.Encode(registration.CoseKey.Span)).IsEqualTo(GoldenRegistrationCoseKey);
        await Assert.That(registration.Algorithm).IsEqualTo(CoseAlgorithm.Es256);
        await Assert.That(registration.SignCount).IsEqualTo(0u);
    }

    [Test]
    public async Task Assertion_WithAGoldenVector_Verifies()
    {
        // This vector is deliberately not pushed through PasskeyAssertionVerifier, and that is not an
        // oversight. It was captured from a roaming security key at a time when user verification was
        // not required, so its UV flag is clear, and this product's ceremony requires UV. Running it
        // through the full ladder could only be made to pass by weakening the requirement or by
        // granting this one test an override, and either dissolves the rule.
        //
        // So it pins the layer it can honestly pin: the wire-format parsing and the cryptography. The
        // UV requirement is pinned separately, by the synthetic authenticator, which sets and clears
        // that flag on demand, and by the companion test that runs this same vector through the full
        // ladder and asserts the refusal.

        // Arrange
        byte[] authenticatorDataBytes = Base64UrlText.Decode(GoldenAssertionAuthenticatorData);
        byte[] clientDataJson = Base64UrlText.Decode(GoldenAssertionClientDataJson);
        byte[] signature = Base64UrlText.Decode(GoldenAssertionSignature);
        byte[] coseKey = Base64UrlText.Decode(GoldenAssertionCoseKey);

        // Act
        PasskeyVerificationFailure? signatureFailure = PasskeySignatureVerifier.Verify(
            coseKey,
            CoseAlgorithm.Es256,
            authenticatorDataBytes,
            clientDataJson,
            signature);
        PasskeyVerificationResult<AuthenticatorData> authenticatorData =
            AuthenticatorData.Parse(authenticatorDataBytes);
        PasskeyVerificationResult<CollectedClientData> clientData = CollectedClientData.Parse(clientDataJson);

        // Assert
        await Assert.That(signatureFailure).IsNull();

        await Assert.That(authenticatorData.TryGetValue(out AuthenticatorData? parsedAuthenticatorData, out _))
            .IsTrue();
        await Assert.That(parsedAuthenticatorData!.SignCount).IsEqualTo(GoldenAssertionSignCount);
        await Assert.That(parsedAuthenticatorData.IsUserPresent).IsTrue();
        await Assert.That(parsedAuthenticatorData.IsUserVerified).IsFalse();
        await Assert.That(parsedAuthenticatorData.AttestedCredential).IsNull();
        await Assert.That(parsedAuthenticatorData.MatchesRelyingParty(GoldenRelyingPartyId)).IsTrue();

        await Assert.That(clientData.TryGetValue(out CollectedClientData? parsedClientData, out _)).IsTrue();
        await Assert.That(parsedClientData!.Type).IsEqualTo(CollectedClientData.AuthenticationType);
        await Assert.That(parsedClientData.Origin).IsEqualTo(GoldenOrigin);
        await Assert.That(Base64UrlText.Encode(parsedClientData.Challenge.Span)).IsEqualTo(GoldenAssertionChallenge);
    }

    [Test]
    public async Task Assertion_WithAGoldenVectorAndTheFullLadder_IsRefusedForUserVerification()
    {
        // The other half of the split above: the same captured vector, this time all the way through.
        // Without this, a reader could take the carve-out in the previous test as evidence that the
        // user verification requirement does not exist.

        // Arrange
        PasskeyAssertionExpectations expectations = new()
        {
            Challenge = Base64UrlText.Decode(GoldenAssertionChallenge),
            AllowedOrigins = [GoldenOrigin],
            RelyingPartyId = GoldenRelyingPartyId,
            CoseKey = Base64UrlText.Decode(GoldenAssertionCoseKey),
            Algorithm = CoseAlgorithm.Es256,
        };

        // Act
        PasskeyVerificationResult<VerifiedAssertion> result = PasskeyAssertionVerifier.Verify(
            Base64UrlText.Decode(GoldenAssertionClientDataJson),
            Base64UrlText.Decode(GoldenAssertionAuthenticatorData),
            Base64UrlText.Decode(GoldenAssertionSignature),
            expectations);

        // Assert
        await Assert.That(result.Failure).IsEqualTo(PasskeyVerificationFailure.UserNotVerified);
    }

    [Test]
    public async Task Assertion_SignedByAnotherKeyPair_IsRefused()
    {
        // Arrange
        SyntheticAuthenticator enrolled = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
        SyntheticAuthenticator impostor = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
        byte[] challenge = NewChallenge();
        AssertionResult assertion = impostor.Authenticate(challenge, Origin);

        // Act
        PasskeyVerificationResult<VerifiedAssertion> result = PasskeyAssertionVerifier.Verify(
            assertion.ClientDataJson,
            assertion.AuthenticatorData,
            assertion.Signature,
            ExpectationsFor(enrolled, challenge));

        // Assert
        await Assert.That(result.Failure).IsEqualTo(PasskeyVerificationFailure.SignatureInvalid);
    }

    [Test]
    public async Task Assertion_WithASyntheticEs256Authenticator_Verifies()
    {
        // Arrange
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
        byte[] challenge = NewChallenge();
        AssertionResult assertion = authenticator.Authenticate(challenge, Origin, signCount: 7);

        // Act
        PasskeyVerificationResult<VerifiedAssertion> result = PasskeyAssertionVerifier.Verify(
            assertion.ClientDataJson,
            assertion.AuthenticatorData,
            assertion.Signature,
            ExpectationsFor(authenticator, challenge));

        // Assert
        await Assert.That(result.Failure).IsNull();
        await Assert.That(result.TryGetValue(out VerifiedAssertion? verified, out _)).IsTrue();
        await Assert.That(verified!.SignCount).IsEqualTo(7u);
    }

    [Test]
    public async Task Assertion_WithASyntheticRs256Authenticator_Verifies()
    {
        // Arrange
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateRs256(RelyingPartyId);
        byte[] challenge = NewChallenge();
        AssertionResult assertion = authenticator.Authenticate(challenge, Origin, signCount: 7);

        // Act
        PasskeyVerificationResult<VerifiedAssertion> result = PasskeyAssertionVerifier.Verify(
            assertion.ClientDataJson,
            assertion.AuthenticatorData,
            assertion.Signature,
            ExpectationsFor(authenticator, challenge));

        // Assert
        await Assert.That(result.Failure).IsNull();
        await Assert.That(result.TryGetValue(out VerifiedAssertion? verified, out _)).IsTrue();
        await Assert.That(verified!.SignCount).IsEqualTo(7u);
    }

    [Test]
    public async Task Assertion_WhenOneByteOfTheSignatureIsFlipped_IsRefused()
    {
        // Arrange
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
        byte[] challenge = NewChallenge();
        AssertionResult assertion = authenticator.Authenticate(challenge, Origin);
        byte[] tamperedSignature = [.. assertion.Signature];

        // The last byte of the DER sequence is inside the s value, so the signature stays parseable
        // and is refused by the mathematics rather than by the decoder.
        tamperedSignature[^1] ^= 0x01;

        // Act
        PasskeyVerificationResult<VerifiedAssertion> result = PasskeyAssertionVerifier.Verify(
            assertion.ClientDataJson,
            assertion.AuthenticatorData,
            tamperedSignature,
            ExpectationsFor(authenticator, challenge));

        // Assert
        await Assert.That(result.Failure).IsEqualTo(PasskeyVerificationFailure.SignatureInvalid);
    }

    [Test]
    public async Task Assertion_WhenOneByteOfTheAuthenticatorDataIsFlipped_IsRefused()
    {
        // Arrange
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
        byte[] challenge = NewChallenge();
        AssertionResult assertion = authenticator.Authenticate(challenge, Origin);
        byte[] tamperedAuthenticatorData = [.. assertion.AuthenticatorData];

        // The low byte of the sign counter: a change that every other check waves through, so the
        // refusal can only come from the signature covering the block as a whole.
        tamperedAuthenticatorData[36] ^= 0x01;

        // Act
        PasskeyVerificationResult<VerifiedAssertion> result = PasskeyAssertionVerifier.Verify(
            assertion.ClientDataJson,
            tamperedAuthenticatorData,
            assertion.Signature,
            ExpectationsFor(authenticator, challenge));

        // Assert
        await Assert.That(result.Failure).IsEqualTo(PasskeyVerificationFailure.SignatureInvalid);
    }

    [Test]
    public async Task Assertion_WhenTheSignatureIsIeeeP1363Encoded_IsRefused()
    {
        // A genuine signature by the enrolled key over exactly the right bytes, in the wrong encoding.
        // A verifier that called ECDsa.VerifyData without DSASignatureFormat.Rfc3279DerSequence would
        // accept this one and refuse every real authenticator, so this is the test that tells the two
        // apart from the outside.

        // Arrange
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
        byte[] challenge = NewChallenge();
        AssertionResult assertion = authenticator.Authenticate(
            challenge,
            Origin,
            encodeSignatureAsIeeeP1363: true);

        // Act
        PasskeyVerificationResult<VerifiedAssertion> result = PasskeyAssertionVerifier.Verify(
            assertion.ClientDataJson,
            assertion.AuthenticatorData,
            assertion.Signature,
            ExpectationsFor(authenticator, challenge));

        // Assert
        await Assert.That(result.Failure).IsEqualTo(PasskeyVerificationFailure.SignatureInvalid);
    }

    [Test]
    public async Task Assertion_WhenTheClientDataTypeIsWebauthnCreate_IsRefused()
    {
        // Arrange
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
        byte[] challenge = NewChallenge();
        AssertionResult assertion = authenticator.Authenticate(
            challenge,
            Origin,
            clientDataTypeOverride: CollectedClientData.RegistrationType);

        // Act
        PasskeyVerificationResult<VerifiedAssertion> result = PasskeyAssertionVerifier.Verify(
            assertion.ClientDataJson,
            assertion.AuthenticatorData,
            assertion.Signature,
            ExpectationsFor(authenticator, challenge));

        // Assert
        await Assert.That(result.Failure).IsEqualTo(PasskeyVerificationFailure.UnexpectedCeremonyType);
    }

    [Test]
    public async Task Assertion_WhenTheOriginOnlyPrefixMatchesAnAllowedOne_IsRefused()
    {
        // budgetoid.app.attacker.example is a domain the attacker owns outright. StartsWith or
        // Contains against the allowed origin accepts it; equality does not.

        // Arrange
        const string LookalikeOrigin = "https://budgetoid.app.attacker.example";
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
        byte[] challenge = NewChallenge();
        AssertionResult assertion = authenticator.Authenticate(challenge, LookalikeOrigin);

        // Act
        PasskeyVerificationResult<VerifiedAssertion> result = PasskeyAssertionVerifier.Verify(
            assertion.ClientDataJson,
            assertion.AuthenticatorData,
            assertion.Signature,
            ExpectationsFor(authenticator, challenge));

        // Assert
        await Assert.That(result.Failure).IsEqualTo(PasskeyVerificationFailure.UntrustedOrigin);
    }

    [Test]
    public async Task Assertion_WhenTheRpIdHashNamesAnotherRelyingParty_IsRefused()
    {
        // Arrange
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
        byte[] challenge = NewChallenge();
        AssertionResult assertion = authenticator.Authenticate(
            challenge,
            Origin,
            rpIdOverride: "attacker.example");

        // Act
        PasskeyVerificationResult<VerifiedAssertion> result = PasskeyAssertionVerifier.Verify(
            assertion.ClientDataJson,
            assertion.AuthenticatorData,
            assertion.Signature,
            ExpectationsFor(authenticator, challenge));

        // Assert
        await Assert.That(result.Failure).IsEqualTo(PasskeyVerificationFailure.RelyingPartyMismatch);
    }

    [Test]
    public async Task Assertion_WhenUserPresenceIsNotSet_IsRefused()
    {
        // Arrange
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
        byte[] challenge = NewChallenge();
        AssertionResult assertion = authenticator.Authenticate(challenge, Origin, userPresent: false);

        // Act
        PasskeyVerificationResult<VerifiedAssertion> result = PasskeyAssertionVerifier.Verify(
            assertion.ClientDataJson,
            assertion.AuthenticatorData,
            assertion.Signature,
            ExpectationsFor(authenticator, challenge));

        // Assert
        await Assert.That(result.Failure).IsEqualTo(PasskeyVerificationFailure.UserNotPresent);
    }

    [Test]
    public async Task Assertion_WhenUserVerificationIsNotSet_IsRefused()
    {
        // Arrange
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
        byte[] challenge = NewChallenge();
        AssertionResult assertion = authenticator.Authenticate(challenge, Origin, userVerified: false);

        // Act
        PasskeyVerificationResult<VerifiedAssertion> result = PasskeyAssertionVerifier.Verify(
            assertion.ClientDataJson,
            assertion.AuthenticatorData,
            assertion.Signature,
            ExpectationsFor(authenticator, challenge));

        // Assert
        await Assert.That(result.Failure).IsEqualTo(PasskeyVerificationFailure.UserNotVerified);
    }

    [Test]
    public async Task Assertion_WhenTheChallengeIsNotTheOneIssued_IsRefused()
    {
        // Arrange
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
        byte[] issuedChallenge = NewChallenge();
        AssertionResult assertion = authenticator.Authenticate(NewChallenge(), Origin);

        // Act
        PasskeyVerificationResult<VerifiedAssertion> result = PasskeyAssertionVerifier.Verify(
            assertion.ClientDataJson,
            assertion.AuthenticatorData,
            assertion.Signature,
            ExpectationsFor(authenticator, issuedChallenge));

        // Assert
        await Assert.That(result.Failure).IsEqualTo(PasskeyVerificationFailure.ChallengeMismatch);
    }

    [Test]
    public async Task Assertion_WhenTheSignCountIsAboveTwoBillion_DecodesUnsigned()
    {
        // 0x80000100 is chosen so both mistakes are visible: read as a signed int it is negative, and
        // read little-endian it is 0x00010080. Only a big-endian unsigned read produces this number.
        const uint HighSignCount = 0x80000100;

        // Arrange
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
        byte[] challenge = NewChallenge();
        AssertionResult assertion = authenticator.Authenticate(challenge, Origin, signCount: HighSignCount);

        // Act
        PasskeyVerificationResult<VerifiedAssertion> result = PasskeyAssertionVerifier.Verify(
            assertion.ClientDataJson,
            assertion.AuthenticatorData,
            assertion.Signature,
            ExpectationsFor(authenticator, challenge));

        // Assert
        await Assert.That(result.Failure).IsNull();
        await Assert.That(result.TryGetValue(out VerifiedAssertion? verified, out _)).IsTrue();
        await Assert.That(verified!.SignCount).IsEqualTo(HighSignCount);
        await Assert.That(verified.SignCount).IsGreaterThan((uint)int.MaxValue);
    }

    [Test]
    public async Task Registration_WithASyntheticEs256Authenticator_YieldsTheCredentialIdAndKey()
    {
        // Arrange
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
        byte[] challenge = NewChallenge();
        AttestationResult attestation = authenticator.Register(challenge, Origin);

        // Act
        PasskeyVerificationResult<VerifiedRegistration> result = PasskeyRegistrationVerifier.Verify(
            attestation.ClientDataJson,
            attestation.AttestationObject,
            RegistrationExpectationsFor(challenge));

        // Assert
        await Assert.That(result.Failure).IsNull();
        await Assert.That(result.TryGetValue(out VerifiedRegistration? registration, out _)).IsTrue();
        await Assert.That(Base64UrlText.Encode(registration!.WebAuthnCredentialId.Span))
            .IsEqualTo(Base64UrlText.Encode(authenticator.CredentialId));
        await Assert.That(Base64UrlText.Encode(registration.CoseKey.Span))
            .IsEqualTo(Base64UrlText.Encode(authenticator.CoseKey));
        await Assert.That(registration.Algorithm).IsEqualTo(CoseAlgorithm.Es256);
        await Assert.That(registration.SignCount).IsEqualTo(0u);
    }

    [Test]
    public async Task Registration_WithASyntheticRs256Authenticator_YieldsTheCredentialIdAndKey()
    {
        // Arrange
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateRs256(RelyingPartyId);
        byte[] challenge = NewChallenge();
        AttestationResult attestation = authenticator.Register(challenge, Origin);

        // Act
        PasskeyVerificationResult<VerifiedRegistration> result = PasskeyRegistrationVerifier.Verify(
            attestation.ClientDataJson,
            attestation.AttestationObject,
            RegistrationExpectationsFor(challenge));

        // Assert
        await Assert.That(result.Failure).IsNull();
        await Assert.That(result.TryGetValue(out VerifiedRegistration? registration, out _)).IsTrue();
        await Assert.That(Base64UrlText.Encode(registration!.WebAuthnCredentialId.Span))
            .IsEqualTo(Base64UrlText.Encode(authenticator.CredentialId));
        await Assert.That(Base64UrlText.Encode(registration.CoseKey.Span))
            .IsEqualTo(Base64UrlText.Encode(authenticator.CoseKey));
        await Assert.That(registration.Algorithm).IsEqualTo(CoseAlgorithm.Rs256);
    }

    [Test]
    public async Task Registration_WhenTheRs256ModulusIsZeroPaddedToReachTheFloor_IsRefused()
    {
        // This is the test that separates the correct rule from the tempting one. An implementation
        // written as `modulus.Length >= 256` passes every other RS256 test in this file and fails only
        // here: a 512-bit modulus left-padded with 224 zero octets is 256 bytes long and still a
        // 512-bit key. Only counting bits after skipping leading zeros refuses it.
        //
        // The modulus is built rather than generated because RSA.Create(512) is refused outright on
        // macOS, and because the strength check runs before RSA.Create — the value never has to be a
        // factorable modulus to reach it, only to be genuinely 512 bits wide.

        // Arrange
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateRs256(RelyingPartyId);
        byte[] challenge = NewChallenge();
        byte[] weakModulus = SyntheticRsaModulus(bits: 512);
        byte[] paddedModulus = new byte[256];
        weakModulus.CopyTo(paddedModulus.AsSpan(paddedModulus.Length - weakModulus.Length));
        byte[] paddedWeakKey = WebAuthnWireFormat.EncodeRsaCoseKey(
            WebAuthnWireFormat.KeyTypeRsa,
            (int)CoseAlgorithm.Rs256,
            paddedModulus,
            StandardRsaExponent);
        AttestationResult attestation = authenticator.Register(challenge, Origin, coseKeyOverride: paddedWeakKey);

        // Act
        PasskeyVerificationResult<VerifiedRegistration> result = PasskeyRegistrationVerifier.Verify(
            attestation.ClientDataJson,
            attestation.AttestationObject,
            RegistrationExpectationsFor(challenge));

        // Assert
        await Assert.That(result.Failure).IsEqualTo(PasskeyVerificationFailure.WeakCoseKey);
    }

    [Test]
    public async Task Registration_WithAnRs256KeyBelowTheModulusFloor_IsRefused()
    {
        // A real 1024-bit device, emitting its own key: the plain case the floor exists for.

        // Arrange
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateRs256(RelyingPartyId, keySizeInBits: 1024);
        byte[] challenge = NewChallenge();
        AttestationResult attestation = authenticator.Register(challenge, Origin);

        // Act
        PasskeyVerificationResult<VerifiedRegistration> result = PasskeyRegistrationVerifier.Verify(
            attestation.ClientDataJson,
            attestation.AttestationObject,
            RegistrationExpectationsFor(challenge));

        // Assert
        await Assert.That(result.Failure).IsEqualTo(PasskeyVerificationFailure.WeakCoseKey);
    }

    [Test]
    public async Task Registration_WithAnRs256ExponentOfOne_IsRefused()
    {
        // e = 1 makes the signature the message: verification is the identity function, so anyone can
        // forge one. The modulus is a real 2048-bit one, so the exponent is the only deviation.

        // Arrange
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateRs256(RelyingPartyId);
        byte[] challenge = NewChallenge();
        byte[] degenerateExponentKey = EncodeRs256CoseKey(RealRsaModulus(bits: 2048), exponent: [0x01]);
        AttestationResult attestation = authenticator.Register(
            challenge,
            Origin,
            coseKeyOverride: degenerateExponentKey);

        // Act
        PasskeyVerificationResult<VerifiedRegistration> result = PasskeyRegistrationVerifier.Verify(
            attestation.ClientDataJson,
            attestation.AttestationObject,
            RegistrationExpectationsFor(challenge));

        // Assert
        await Assert.That(result.Failure).IsEqualTo(PasskeyVerificationFailure.WeakCoseKey);
    }

    [Test]
    public async Task Registration_WithAnRs256ExponentOfThree_IsRefused()
    {
        // e = 3 is where the low-exponent PKCS#1 v1.5 forgery family lives, and whether a forgery
        // lands depends on how carefully the verifier parses padding — which is exactly the
        // platform-dependent judgement the allow-list exists to stop delegating. An allow-list of one
        // value refuses it even though the key is otherwise perfectly well-formed.

        // Arrange
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateRs256(RelyingPartyId);
        byte[] challenge = NewChallenge();
        byte[] lowExponentKey = EncodeRs256CoseKey(RealRsaModulus(bits: 2048), exponent: [0x03]);
        AttestationResult attestation = authenticator.Register(challenge, Origin, coseKeyOverride: lowExponentKey);

        // Act
        PasskeyVerificationResult<VerifiedRegistration> result = PasskeyRegistrationVerifier.Verify(
            attestation.ClientDataJson,
            attestation.AttestationObject,
            RegistrationExpectationsFor(challenge));

        // Assert
        await Assert.That(result.Failure).IsEqualTo(PasskeyVerificationFailure.WeakCoseKey);
    }

    [Test]
    public async Task Registration_WhenTheRs256ModulusIsAboveTheCeiling_IsRefused()
    {
        // 4104 bits: past the ceiling by one octet, so this refusal is the ceiling and not some other
        // bound further out. Built rather than generated for the same reason as the padded case — the
        // strength check runs before RSA.Create, and no authenticator emits a key this wide anyway.

        // Arrange
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateRs256(RelyingPartyId);
        byte[] challenge = NewChallenge();
        byte[] oversizedKey = EncodeRs256CoseKey(SyntheticRsaModulus(bits: 4104), StandardRsaExponent);
        AttestationResult attestation = authenticator.Register(challenge, Origin, coseKeyOverride: oversizedKey);

        // Act
        PasskeyVerificationResult<VerifiedRegistration> result = PasskeyRegistrationVerifier.Verify(
            attestation.ClientDataJson,
            attestation.AttestationObject,
            RegistrationExpectationsFor(challenge));

        // Assert
        await Assert.That(result.Failure).IsEqualTo(PasskeyVerificationFailure.WeakCoseKey);
    }

    [Test]
    public async Task Registration_WithATwoThousandFortyEightBitRs256Key_IsAccepted()
    {
        // The floor itself, from a real device. A floor written as `>` rather than `>=` locks out
        // every 2048-bit authenticator in the field and breaks nothing else, so this is the test that
        // catches it.

        // Arrange
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateRs256(RelyingPartyId, keySizeInBits: 2048);
        byte[] challenge = NewChallenge();
        AttestationResult attestation = authenticator.Register(challenge, Origin);

        // Act
        PasskeyVerificationResult<VerifiedRegistration> result = PasskeyRegistrationVerifier.Verify(
            attestation.ClientDataJson,
            attestation.AttestationObject,
            RegistrationExpectationsFor(challenge));

        // Assert
        await Assert.That(result.Failure).IsNull();
        await Assert.That(result.TryGetValue(out VerifiedRegistration? registration, out _)).IsTrue();
        await Assert.That(Base64UrlText.Encode(registration!.CoseKey.Span))
            .IsEqualTo(Base64UrlText.Encode(authenticator.CoseKey));
        await Assert.That(registration.Algorithm).IsEqualTo(CoseAlgorithm.Rs256);
    }

    [Test]
    public async Task Registration_WithAThreeThousandSeventyTwoBitRs256Key_IsAccepted()
    {
        // Above the floor and below the ceiling, which is what says the floor is a floor rather than
        // an equality — and what a ceiling written as `<` rather than `<=` still admits, so this test
        // and the 2048-bit one together pin both comparisons.

        // Arrange
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateRs256(RelyingPartyId, keySizeInBits: 3072);
        byte[] challenge = NewChallenge();
        AttestationResult attestation = authenticator.Register(challenge, Origin);

        // Act
        PasskeyVerificationResult<VerifiedRegistration> result = PasskeyRegistrationVerifier.Verify(
            attestation.ClientDataJson,
            attestation.AttestationObject,
            RegistrationExpectationsFor(challenge));

        // Assert
        await Assert.That(result.Failure).IsNull();
        await Assert.That(result.TryGetValue(out VerifiedRegistration? registration, out _)).IsTrue();
        await Assert.That(Base64UrlText.Encode(registration!.CoseKey.Span))
            .IsEqualTo(Base64UrlText.Encode(authenticator.CoseKey));
        await Assert.That(registration.Algorithm).IsEqualTo(CoseAlgorithm.Rs256);
    }

    [Test]
    public async Task Registration_WithAFourThousandNinetySixBitRs256Key_IsAccepted()
    {
        // The ceiling itself. The 3072-bit key above sits under it and so survives a ceiling written
        // as `<` rather than `<=`; only a key of exactly the permitted maximum tells the two apart,
        // which makes this the mirror of the 2048-bit test rather than a duplicate of the 3072 one.

        // Arrange
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateRs256(RelyingPartyId, keySizeInBits: 4096);
        byte[] challenge = NewChallenge();
        AttestationResult attestation = authenticator.Register(challenge, Origin);

        // Act
        PasskeyVerificationResult<VerifiedRegistration> result = PasskeyRegistrationVerifier.Verify(
            attestation.ClientDataJson,
            attestation.AttestationObject,
            RegistrationExpectationsFor(challenge));

        // Assert
        await Assert.That(result.Failure).IsNull();
        await Assert.That(result.TryGetValue(out VerifiedRegistration? registration, out _)).IsTrue();
        await Assert.That(Base64UrlText.Encode(registration!.CoseKey.Span))
            .IsEqualTo(Base64UrlText.Encode(authenticator.CoseKey));
        await Assert.That(registration.Algorithm).IsEqualTo(CoseAlgorithm.Rs256);
    }

    [Test]
    public async Task Registration_WhenTheRs256ModulusCarriesALeadingZeroOctet_IsAccepted()
    {
        // RFC 8230 says COSE omits leading zero octets, but an authenticator whose encoder copies a
        // DER INTEGER emits one anyway, so a 257-byte modulus is a real device rather than an attack.
        // This is the other half of the zero-padded refusal above: together they say the rule counts
        // bits and not bytes, in both directions.

        // Arrange
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateRs256(RelyingPartyId);
        byte[] challenge = NewChallenge();
        byte[] derPaddedKey = EncodeRs256CoseKey([0x00, .. RealRsaModulus(bits: 2048)], StandardRsaExponent);
        AttestationResult attestation = authenticator.Register(challenge, Origin, coseKeyOverride: derPaddedKey);

        // Act
        PasskeyVerificationResult<VerifiedRegistration> result = PasskeyRegistrationVerifier.Verify(
            attestation.ClientDataJson,
            attestation.AttestationObject,
            RegistrationExpectationsFor(challenge));

        // Assert
        await Assert.That(result.Failure).IsNull();
        await Assert.That(result.TryGetValue(out VerifiedRegistration? registration, out _)).IsTrue();
        await Assert.That(Base64UrlText.Encode(registration!.CoseKey.Span))
            .IsEqualTo(Base64UrlText.Encode(derPaddedKey));
        await Assert.That(registration.Algorithm).IsEqualTo(CoseAlgorithm.Rs256);
    }

    [Test]
    public async Task Registration_WhenTheRs256ExponentIsZeroPaddedToF4_IsAccepted()
    {
        // 00 01 00 01 is the same number as 01 00 01, and comparing on value rather than on the
        // encoding is what makes them the same key here. A byte-for-byte comparison against the
        // minimal encoding would refuse a device that is emitting the standard exponent.

        // Arrange
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateRs256(RelyingPartyId);
        byte[] challenge = NewChallenge();
        byte[] paddedExponentKey = EncodeRs256CoseKey(RealRsaModulus(bits: 2048), [0x00, .. StandardRsaExponent]);
        AttestationResult attestation = authenticator.Register(challenge, Origin, coseKeyOverride: paddedExponentKey);

        // Act
        PasskeyVerificationResult<VerifiedRegistration> result = PasskeyRegistrationVerifier.Verify(
            attestation.ClientDataJson,
            attestation.AttestationObject,
            RegistrationExpectationsFor(challenge));

        // Assert
        await Assert.That(result.Failure).IsNull();
        await Assert.That(result.TryGetValue(out VerifiedRegistration? registration, out _)).IsTrue();
        await Assert.That(Base64UrlText.Encode(registration!.CoseKey.Span))
            .IsEqualTo(Base64UrlText.Encode(paddedExponentKey));
        await Assert.That(registration.Algorithm).IsEqualTo(CoseAlgorithm.Rs256);
    }

    [Test]
    public async Task Registration_WhenTheCoseKeyCoordinatesAreNotOnTheCurve_IsRefused()
    {
        // Both coordinates are the right width and the key decodes as CBOR, so nothing about its shape
        // says it is wrong — the point simply is not on P-256. Constructing the key at registration is
        // what catches it, and the refusal is MalformedCoseKey rather than WeakCoseKey: a point off
        // the curve is not a key of an unacceptable strength, it is not a key at all.

        // Arrange
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
        byte[] challenge = NewChallenge();
        using ECDsa device = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        ECParameters parameters = device.ExportParameters(includePrivateParameters: false);
        byte[] offCurveX = [.. parameters.Q.X!];
        offCurveX[^1] ^= 0x01;
        byte[] offCurveKey = WebAuthnWireFormat.EncodeEc2CoseKey(
            WebAuthnWireFormat.KeyTypeEc2,
            (int)CoseAlgorithm.Es256,
            WebAuthnWireFormat.CurveP256,
            offCurveX,
            parameters.Q.Y!);
        AttestationResult attestation = authenticator.Register(challenge, Origin, coseKeyOverride: offCurveKey);

        // Act
        PasskeyVerificationResult<VerifiedRegistration> result = PasskeyRegistrationVerifier.Verify(
            attestation.ClientDataJson,
            attestation.AttestationObject,
            RegistrationExpectationsFor(challenge));

        // Assert
        await Assert.That(result.Failure).IsEqualTo(PasskeyVerificationFailure.MalformedCoseKey);
    }

    [Test]
    public async Task Registration_WhenTheAttestationFormatIsNotNone_IsRefused()
    {
        // Arrange
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
        byte[] challenge = NewChallenge();
        AttestationResult attestation = authenticator.Register(challenge, Origin, attestationFormat: "packed");

        // Act
        PasskeyVerificationResult<VerifiedRegistration> result = PasskeyRegistrationVerifier.Verify(
            attestation.ClientDataJson,
            attestation.AttestationObject,
            RegistrationExpectationsFor(challenge));

        // Assert
        await Assert.That(result.Failure).IsEqualTo(PasskeyVerificationFailure.UnsupportedAttestationFormat);
    }

    [Test]
    public async Task Registration_WhenTheAttestedCredentialDataFlagIsNotSet_IsRefused()
    {
        // Arrange
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
        byte[] challenge = NewChallenge();
        AttestationResult attestation = authenticator.Register(
            challenge,
            Origin,
            includeAttestedCredentialData: false);

        // Act
        PasskeyVerificationResult<VerifiedRegistration> result = PasskeyRegistrationVerifier.Verify(
            attestation.ClientDataJson,
            attestation.AttestationObject,
            RegistrationExpectationsFor(challenge));

        // Assert
        await Assert.That(result.Failure).IsEqualTo(PasskeyVerificationFailure.AttestedCredentialDataMissing);
    }

    [Test]
    public async Task Registration_WhenTheCoseAlgorithmIsNotOffered_IsRefused()
    {
        // The key is one the verifier can read and verify with. It is refused because the credential
        // creation options never asked for it, which is a different rule from support.

        // Arrange
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
        byte[] challenge = NewChallenge();
        AttestationResult attestation = authenticator.Register(challenge, Origin);
        PasskeyRegistrationExpectations expectations = new()
        {
            Challenge = challenge,
            AllowedOrigins = [Origin],
            RelyingPartyId = RelyingPartyId,
            OfferedAlgorithms = [CoseAlgorithm.Rs256],
        };

        // Act
        PasskeyVerificationResult<VerifiedRegistration> result = PasskeyRegistrationVerifier.Verify(
            attestation.ClientDataJson,
            attestation.AttestationObject,
            expectations);

        // Assert
        await Assert.That(result.Failure).IsEqualTo(PasskeyVerificationFailure.AlgorithmNotOffered);
    }

    [Test]
    public async Task Registration_WhenTheCoseKeyTypeDisagreesWithItsAlgorithm_IsRefused()
    {
        // alg says ES256, kty says RSA. Trusting alg and reading the labels as EC2 anyway would
        // decode attacker-chosen bytes under a shape they never claimed, so the disagreement is
        // refused outright rather than resolved in favour of either member.

        // Arrange
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
        byte[] challenge = NewChallenge();

        // The coordinates are never reached: the key type is checked first. They are fixed-width
        // placeholders so nothing about this test depends on which bytes they are.
        byte[] contradictoryKey = WebAuthnWireFormat.EncodeEc2CoseKey(
            WebAuthnWireFormat.KeyTypeRsa,
            (int)CoseAlgorithm.Es256,
            WebAuthnWireFormat.CurveP256,
            new byte[32],
            new byte[32]);
        AttestationResult attestation = authenticator.Register(challenge, Origin, coseKeyOverride: contradictoryKey);

        // Act
        PasskeyVerificationResult<VerifiedRegistration> result = PasskeyRegistrationVerifier.Verify(
            attestation.ClientDataJson,
            attestation.AttestationObject,
            RegistrationExpectationsFor(challenge));

        // Assert
        await Assert.That(result.Failure).IsEqualTo(PasskeyVerificationFailure.CoseKeyTypeMismatch);
    }

    [Test]
    public async Task Registration_WhenTheAuthenticatorDataCarriesAnExtensionBlock_IsStillAccepted()
    {
        // The COSE key has no length prefix and the extension map runs on directly after it, so the
        // only thing that says where the key ends is how much of it the CBOR reader consumed. A
        // reader that assumed the key runs to the end of the block stores the extension bytes as part
        // of the key and every later sign-in fails.

        // Arrange
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
        byte[] challenge = NewChallenge();
        AttestationResult attestation = authenticator.Register(
            challenge,
            Origin,
            extensionDataFlag: true,
            appendExtensionBytes: WebAuthnWireFormat.EncodeExtensionOutputs());

        // Act
        PasskeyVerificationResult<VerifiedRegistration> result = PasskeyRegistrationVerifier.Verify(
            attestation.ClientDataJson,
            attestation.AttestationObject,
            RegistrationExpectationsFor(challenge));

        // Assert
        await Assert.That(result.Failure).IsNull();
        await Assert.That(result.TryGetValue(out VerifiedRegistration? registration, out _)).IsTrue();
        await Assert.That(Base64UrlText.Encode(registration!.CoseKey.Span))
            .IsEqualTo(Base64UrlText.Encode(authenticator.CoseKey));
        await Assert.That(Base64UrlText.Encode(registration.WebAuthnCredentialId.Span))
            .IsEqualTo(Base64UrlText.Encode(authenticator.CredentialId));
    }

    [Test]
    public async Task Registration_WhenTheCredentialIdIsLongerThanTheProtocolAllows_IsRefused()
    {
        // Arrange
        byte[] oversizedCredentialId =
            RandomNumberGenerator.GetBytes(PasskeyPublicKey.MaxWebAuthnCredentialIdLength + 1);
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(
            RelyingPartyId,
            oversizedCredentialId);
        byte[] challenge = NewChallenge();
        AttestationResult attestation = authenticator.Register(challenge, Origin);

        // Act
        PasskeyVerificationResult<VerifiedRegistration> result = PasskeyRegistrationVerifier.Verify(
            attestation.ClientDataJson,
            attestation.AttestationObject,
            RegistrationExpectationsFor(challenge));

        // Assert
        await Assert.That(result.Failure).IsEqualTo(PasskeyVerificationFailure.CredentialIdOutOfRange);
    }

    [Test]
    public async Task Registration_WhenUserVerificationIsNotSet_IsRefused()
    {
        // Arrange
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
        byte[] challenge = NewChallenge();
        AttestationResult attestation = authenticator.Register(challenge, Origin, userVerified: false);

        // Act
        PasskeyVerificationResult<VerifiedRegistration> result = PasskeyRegistrationVerifier.Verify(
            attestation.ClientDataJson,
            attestation.AttestationObject,
            RegistrationExpectationsFor(challenge));

        // Assert
        await Assert.That(result.Failure).IsEqualTo(PasskeyVerificationFailure.UserNotVerified);
    }

    [Test]
    public async Task Assertion_WhenTheStoredAlgorithmDisagreesWithTheStoredKey_IsRefused()
    {
        // The algorithm-confusion defence. The stored cose_algorithm column and the algorithm inside
        // the stored COSE key are two records of the same fact, and this check is the only thing that
        // notices when they disagree — a signature verified under an algorithm the key never claimed
        // is a signature verified under attacker-chosen rules.

        // Arrange
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
        byte[] challenge = NewChallenge();
        AssertionResult assertion = authenticator.Authenticate(challenge, Origin);
        PasskeyAssertionExpectations expectations = new()
        {
            Challenge = challenge,
            AllowedOrigins = [Origin],
            RelyingPartyId = RelyingPartyId,
            CoseKey = authenticator.CoseKey,
            Algorithm = CoseAlgorithm.Rs256,
        };

        // Act
        PasskeyVerificationResult<VerifiedAssertion> result = PasskeyAssertionVerifier.Verify(
            assertion.ClientDataJson,
            assertion.AuthenticatorData,
            assertion.Signature,
            expectations);

        // Assert
        await Assert.That(result.Failure).IsEqualTo(PasskeyVerificationFailure.AlgorithmMismatch);
    }

    [Test]
    public async Task Assertion_WhenTheAssertionCarriesAttestedCredentialData_IsRefused()
    {
        // A genuinely signed assertion whose authenticator data carries a credential block the
        // ceremony never produces. Without the check the block is simply parsed and ignored, and a
        // response assembled by something other than a browser is accepted as one.

        // Arrange
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
        byte[] challenge = NewChallenge();
        AssertionResult assertion = authenticator.Authenticate(
            challenge,
            Origin,
            includeAttestedCredentialData: true);

        // Act
        PasskeyVerificationResult<VerifiedAssertion> result = PasskeyAssertionVerifier.Verify(
            assertion.ClientDataJson,
            assertion.AuthenticatorData,
            assertion.Signature,
            ExpectationsFor(authenticator, challenge));

        // Assert
        await Assert.That(result.Failure).IsEqualTo(PasskeyVerificationFailure.UnexpectedAttestedCredentialData);
    }

    [Test]
    public async Task Assertion_WhenTheCeremonyWasCrossOrigin_IsRefused()
    {
        // The origin is one this product allows and the signature is real. What is refused is that
        // the ceremony ran inside a frame on somebody else's page.

        // Arrange
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
        byte[] challenge = NewChallenge();
        AssertionResult assertion = authenticator.Authenticate(challenge, Origin, crossOrigin: true);

        // Act
        PasskeyVerificationResult<VerifiedAssertion> result = PasskeyAssertionVerifier.Verify(
            assertion.ClientDataJson,
            assertion.AuthenticatorData,
            assertion.Signature,
            ExpectationsFor(authenticator, challenge));

        // Assert
        await Assert.That(result.Failure).IsEqualTo(PasskeyVerificationFailure.CrossOriginNotAllowed);
    }

    [Test]
    public async Task Registration_WhenTheCeremonyWasCrossOrigin_IsRefused()
    {
        // Both verifiers reach this through the same CollectedClientData.Verify, but at different
        // points in their ladders, and the registration ladder is the one an authenticated caller
        // drives — a frame that enrols its own key is a permanent second credential.

        // Arrange
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
        byte[] challenge = NewChallenge();
        AttestationResult attestation = authenticator.Register(challenge, Origin, crossOrigin: true);

        // Act
        PasskeyVerificationResult<VerifiedRegistration> result = PasskeyRegistrationVerifier.Verify(
            attestation.ClientDataJson,
            attestation.AttestationObject,
            RegistrationExpectationsFor(challenge));

        // Assert
        await Assert.That(result.Failure).IsEqualTo(PasskeyVerificationFailure.CrossOriginNotAllowed);
    }

    [Test]
    public async Task Assertion_WhenTheAuthenticatorDataIsTruncated_IsRefused()
    {
        // One byte short of the fixed header. Without the length check the sign counter is read off
        // the end of the buffer.

        // Arrange
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
        byte[] challenge = NewChallenge();
        AssertionResult assertion = authenticator.Authenticate(
            challenge,
            Origin,
            truncateToLength: AuthenticatorData.FixedHeaderLength - 1);

        // Act
        PasskeyVerificationResult<VerifiedAssertion> result = PasskeyAssertionVerifier.Verify(
            assertion.ClientDataJson,
            assertion.AuthenticatorData,
            assertion.Signature,
            ExpectationsFor(authenticator, challenge));

        // Assert
        await Assert.That(result.Failure).IsEqualTo(PasskeyVerificationFailure.MalformedAuthenticatorData);
    }

    [Test]
    public async Task Registration_WhenTheAttestedCredentialDataIsTruncated_IsRefused()
    {
        // AT is set and the block ends part way through the AAGUID, so the credential id length is
        // not there to be read. The second bound, distinct from the fixed header above.
        const int InsideTheAaguid = AuthenticatorData.FixedHeaderLength + 8;

        // Arrange
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
        byte[] challenge = NewChallenge();
        AttestationResult attestation = authenticator.Register(
            challenge,
            Origin,
            truncateToLength: InsideTheAaguid);

        // Act
        PasskeyVerificationResult<VerifiedRegistration> result = PasskeyRegistrationVerifier.Verify(
            attestation.ClientDataJson,
            attestation.AttestationObject,
            RegistrationExpectationsFor(challenge));

        // Assert
        await Assert.That(result.Failure).IsEqualTo(PasskeyVerificationFailure.MalformedAuthenticatorData);
    }

    [Test]
    public async Task Registration_WhenTheCredentialIdLengthOverrunsTheBuffer_IsRefused()
    {
        // A length the authenticator supplied, naming far more credential than the block holds. It is
        // used to slice, so an unchecked one reads past the end of the buffer.

        // Arrange
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
        byte[] challenge = NewChallenge();
        AttestationResult attestation = authenticator.Register(
            challenge,
            Origin,
            declaredCredentialIdLengthOverride: ushort.MaxValue);

        // Act
        PasskeyVerificationResult<VerifiedRegistration> result = PasskeyRegistrationVerifier.Verify(
            attestation.ClientDataJson,
            attestation.AttestationObject,
            RegistrationExpectationsFor(challenge));

        // Assert
        await Assert.That(result.Failure).IsEqualTo(PasskeyVerificationFailure.MalformedAuthenticatorData);
    }

    [Test]
    public async Task Assertion_WhenTheAuthenticatorDataCarriesTrailingBytesWithoutTheExtensionFlag_IsRefused()
    {
        // ED is clear, so nothing may follow the fixed header. Appending to a produced block rather
        // than overwriting inside one: a byte after the end is exactly the deviation under test.

        // Arrange
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
        byte[] challenge = NewChallenge();
        AssertionResult assertion = authenticator.Authenticate(challenge, Origin);
        byte[] withTrailingBytes = [.. assertion.AuthenticatorData, 0x00];

        // Act
        PasskeyVerificationResult<VerifiedAssertion> result = PasskeyAssertionVerifier.Verify(
            assertion.ClientDataJson,
            withTrailingBytes,
            assertion.Signature,
            ExpectationsFor(authenticator, challenge));

        // Assert
        await Assert.That(result.Failure).IsEqualTo(PasskeyVerificationFailure.MalformedAuthenticatorData);
    }

    [Test]
    public async Task Registration_WhenTheExtensionFlagIsSetButNoBlockFollows_IsRefused()
    {
        // The flag announces a map that is not there. The extension block is otherwise pinned only in
        // the accepting direction, so without this both arms of the check are deletable in silence.

        // Arrange
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
        byte[] challenge = NewChallenge();
        AttestationResult attestation = authenticator.Register(challenge, Origin, extensionDataFlag: true);

        // Act
        PasskeyVerificationResult<VerifiedRegistration> result = PasskeyRegistrationVerifier.Verify(
            attestation.ClientDataJson,
            attestation.AttestationObject,
            RegistrationExpectationsFor(challenge));

        // Assert
        await Assert.That(result.Failure).IsEqualTo(PasskeyVerificationFailure.MalformedExtensionData);
    }

    [Test]
    public async Task Registration_WhenTheExtensionBlockIsNotACborMap_IsRefused()
    {
        // Well-formed CBOR that is not a map. A reader that only checks the bytes decode at all lets
        // this through; one that insists on the map WebAuthn defines does not.

        // Arrange
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
        byte[] challenge = NewChallenge();
        AttestationResult attestation = authenticator.Register(
            challenge,
            Origin,
            extensionDataFlag: true,
            appendExtensionBytes: WebAuthnWireFormat.EncodeNonMapExtensionOutputs());

        // Act
        PasskeyVerificationResult<VerifiedRegistration> result = PasskeyRegistrationVerifier.Verify(
            attestation.ClientDataJson,
            attestation.AttestationObject,
            RegistrationExpectationsFor(challenge));

        // Assert
        await Assert.That(result.Failure).IsEqualTo(PasskeyVerificationFailure.MalformedExtensionData);
    }

    [Test]
    public async Task Registration_WhenTheAttestationObjectsAuthDataIsNotBytes_IsRefused()
    {
        // The member is present under the right label with the wrong CBOR major type. Reading it as a
        // byte string anyway is where a reader that trusts the label throws instead of refusing.

        // Arrange
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
        byte[] challenge = NewChallenge();
        AttestationResult attestation = authenticator.Register(
            challenge,
            Origin,
            encodeAuthDataAsTextString: true);

        // Act
        PasskeyVerificationResult<VerifiedRegistration> result = PasskeyRegistrationVerifier.Verify(
            attestation.ClientDataJson,
            attestation.AttestationObject,
            RegistrationExpectationsFor(challenge));

        // Assert
        await Assert.That(result.Failure).IsEqualTo(PasskeyVerificationFailure.MalformedAttestationObject);
    }

    [Test]
    public async Task Registration_WhenTheAttestationObjectOmitsAuthData_IsRefused()
    {
        // The absent-member arm, a different branch from the type-confusion one above: fmt and attStmt
        // both read cleanly and the map closes, and there is still nothing to verify.

        // Arrange
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
        byte[] challenge = NewChallenge();
        AttestationResult attestation = authenticator.Register(challenge, Origin, omitAuthData: true);

        // Act
        PasskeyVerificationResult<VerifiedRegistration> result = PasskeyRegistrationVerifier.Verify(
            attestation.ClientDataJson,
            attestation.AttestationObject,
            RegistrationExpectationsFor(challenge));

        // Assert
        await Assert.That(result.Failure).IsEqualTo(PasskeyVerificationFailure.MalformedAttestationObject);
    }

    [Test]
    public async Task Assertion_WhenTheClientDataIsNotJson_IsRefused()
    {
        // clientDataJSON is a direct argument to the verifier, so a malformed document is bytes
        // written here rather than a knob on the authenticator.

        // Arrange
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
        byte[] challenge = NewChallenge();
        AssertionResult assertion = authenticator.Authenticate(challenge, Origin);
        byte[] clientDataJson = Encoding.UTF8.GetBytes("this is not a JSON document");

        // Act
        PasskeyVerificationResult<VerifiedAssertion> result = PasskeyAssertionVerifier.Verify(
            clientDataJson,
            assertion.AuthenticatorData,
            assertion.Signature,
            ExpectationsFor(authenticator, challenge));

        // Assert
        await Assert.That(result.Failure).IsEqualTo(PasskeyVerificationFailure.MalformedClientData);
    }

    [Test]
    public async Task Assertion_WhenTheClientDataOmitsTheOrigin_IsRefused()
    {
        // A missing member is refused rather than defaulted. An absent origin that read as empty
        // would then be compared against the allow-list, and the comparison is not what should be
        // deciding this.

        // Arrange
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
        byte[] challenge = NewChallenge();
        AssertionResult assertion = authenticator.Authenticate(challenge, Origin);
        byte[] clientDataJson = Encoding.UTF8.GetBytes(
            $$"""{"type":"webauthn.get","challenge":"{{Base64UrlText.Encode(challenge)}}"}""");

        // Act
        PasskeyVerificationResult<VerifiedAssertion> result = PasskeyAssertionVerifier.Verify(
            clientDataJson,
            assertion.AuthenticatorData,
            assertion.Signature,
            ExpectationsFor(authenticator, challenge));

        // Assert
        await Assert.That(result.Failure).IsEqualTo(PasskeyVerificationFailure.MalformedClientData);
    }

    [Test]
    public async Task Assertion_WhenTheChallengeIsNotBase64Url_IsRefused()
    {
        // Characters outside the base64url alphabet. A hand-rolled decoder that substitutes and
        // re-pads accepts strings the specification does not, and the challenge is the one value
        // whose decoding must be exact.

        // Arrange
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
        byte[] challenge = NewChallenge();
        AssertionResult assertion = authenticator.Authenticate(challenge, Origin);
        byte[] clientDataJson = Encoding.UTF8.GetBytes(
            $$"""{"type":"webauthn.get","challenge":"not base64url!!","origin":"{{Origin}}"}""");

        // Act
        PasskeyVerificationResult<VerifiedAssertion> result = PasskeyAssertionVerifier.Verify(
            clientDataJson,
            assertion.AuthenticatorData,
            assertion.Signature,
            ExpectationsFor(authenticator, challenge));

        // Assert
        await Assert.That(result.Failure).IsEqualTo(PasskeyVerificationFailure.MalformedClientData);
    }

    [Test]
    public async Task Assertion_WhenCrossOriginIsNotABoolean_IsRefused()
    {
        // The string "false" rather than the literal. Coercing it — or treating anything that is not
        // the literal true as same-origin — turns the cross-origin check into a permissive default.

        // Arrange
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
        byte[] challenge = NewChallenge();
        AssertionResult assertion = authenticator.Authenticate(challenge, Origin);
        byte[] clientDataJson = Encoding.UTF8.GetBytes(
            $$"""
            {"type":"webauthn.get","challenge":"{{Base64UrlText.Encode(challenge)}}",
             "origin":"{{Origin}}","crossOrigin":"false"}
            """);

        // Act
        PasskeyVerificationResult<VerifiedAssertion> result = PasskeyAssertionVerifier.Verify(
            clientDataJson,
            assertion.AuthenticatorData,
            assertion.Signature,
            ExpectationsFor(authenticator, challenge));

        // Assert
        await Assert.That(result.Failure).IsEqualTo(PasskeyVerificationFailure.MalformedClientData);
    }

    [Test]
    public async Task Registration_WhenTheCoseKeyIsNotCbor_IsRefused()
    {
        // The key has no length prefix, so where it ends is whatever the CBOR reader consumed. Bytes
        // that are not CBOR at all give the reader nothing to consume.

        // Arrange
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
        byte[] challenge = NewChallenge();
        byte[] notCbor = [0xff, 0xff, 0xff, 0xff];
        AttestationResult attestation = authenticator.Register(challenge, Origin, coseKeyOverride: notCbor);

        // Act
        PasskeyVerificationResult<VerifiedRegistration> result = PasskeyRegistrationVerifier.Verify(
            attestation.ClientDataJson,
            attestation.AttestationObject,
            RegistrationExpectationsFor(challenge));

        // Assert
        await Assert.That(result.Failure).IsEqualTo(PasskeyVerificationFailure.MalformedCoseKey);
    }

    [Test]
    public async Task Assertion_WhenTheStoredKeyCarriesTrailingBytes_IsRefused()
    {
        // The stored blob is re-validated on every sign-in rather than trusted because it was
        // validated once at registration. Trailing bytes are unreachable from a registration block —
        // the key reader there stops where the key stops — so this arm has no other way in.

        // Arrange
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
        byte[] challenge = NewChallenge();
        AssertionResult assertion = authenticator.Authenticate(challenge, Origin);
        byte[] storedKeyWithTrailingBytes = [.. authenticator.CoseKey, 0x00];
        PasskeyAssertionExpectations expectations = new()
        {
            Challenge = challenge,
            AllowedOrigins = [Origin],
            RelyingPartyId = RelyingPartyId,
            CoseKey = storedKeyWithTrailingBytes,
            Algorithm = authenticator.Algorithm,
        };

        // Act
        PasskeyVerificationResult<VerifiedAssertion> result = PasskeyAssertionVerifier.Verify(
            assertion.ClientDataJson,
            assertion.AuthenticatorData,
            assertion.Signature,
            expectations);

        // Assert
        await Assert.That(result.Failure).IsEqualTo(PasskeyVerificationFailure.MalformedCoseKey);
    }

    [Test]
    public async Task Registration_WhenTheCoseKeyNamesAnAlgorithmThisProductCannotVerify_IsRefused()
    {
        // -8 is EdDSA: a real COSE algorithm with no verifier here at all. Distinct from the offered
        // check, which refuses an algorithm this product can verify but nobody asked for.
        const int EdDsa = -8;

        // Arrange
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
        byte[] challenge = NewChallenge();

        // The coordinates are never reached: the algorithm decides first. Fixed-width placeholders so
        // nothing about this test depends on which bytes they are.
        byte[] unsupportedKey = WebAuthnWireFormat.EncodeEc2CoseKey(
            WebAuthnWireFormat.KeyTypeEc2,
            EdDsa,
            WebAuthnWireFormat.CurveP256,
            new byte[32],
            new byte[32]);
        AttestationResult attestation = authenticator.Register(challenge, Origin, coseKeyOverride: unsupportedKey);

        // Act
        PasskeyVerificationResult<VerifiedRegistration> result = PasskeyRegistrationVerifier.Verify(
            attestation.ClientDataJson,
            attestation.AttestationObject,
            RegistrationExpectationsFor(challenge));

        // Assert
        await Assert.That(result.Failure).IsEqualTo(PasskeyVerificationFailure.UnsupportedAlgorithm);
    }

    /// <summary>A fresh 32-byte nonce, the size the ceremony issues.</summary>
    private static byte[] NewChallenge() => RandomNumberGenerator.GetBytes(32);

    /// <summary>F4, minimally encoded — what every authenticator in the field emits.</summary>
    private static byte[] StandardRsaExponent => [0x01, 0x00, 0x01];

    /// <summary>The public modulus of a freshly generated key of the given size.</summary>
    private static byte[] RealRsaModulus(int bits)
    {
        using RSA rsa = RSA.Create(bits);

        return rsa.ExportParameters(includePrivateParameters: false).Modulus!;
    }

    /// <summary>
    /// A big-endian value that is exactly <paramref name="bits"/> bits wide and shaped like a modulus
    /// — top bit set so the width is the stated one, low bit set so it is odd.
    /// </summary>
    /// <remarks>
    /// For the widths no generator will produce: 512 bits is refused by the platform outright, and
    /// nothing generates 4104. The strength check runs before the key is constructed, so a value of
    /// the right width reaches it without having to be a factorable modulus.
    /// </remarks>
    private static byte[] SyntheticRsaModulus(int bits)
    {
        byte[] modulus = RandomNumberGenerator.GetBytes(bits / 8);
        modulus[0] |= 0x80;
        modulus[^1] |= 0x01;

        return modulus;
    }

    /// <summary>An RS256 COSE key carrying the given modulus and exponent verbatim.</summary>
    private static byte[] EncodeRs256CoseKey(ReadOnlySpan<byte> modulus, ReadOnlySpan<byte> exponent) =>
        WebAuthnWireFormat.EncodeRsaCoseKey(
            WebAuthnWireFormat.KeyTypeRsa,
            (int)CoseAlgorithm.Rs256,
            modulus,
            exponent);

    /// <summary>
    /// What the server would have stored for <paramref name="authenticator"/> and issued for this
    /// ceremony. Named rather than inlined because every negative assertion test below differs from
    /// the accepting one by exactly one argument, and the shared baseline is what makes that visible.
    /// </summary>
    private static PasskeyAssertionExpectations ExpectationsFor(
        SyntheticAuthenticator authenticator,
        byte[] challenge) =>
        new()
        {
            Challenge = challenge,
            AllowedOrigins = [Origin],
            RelyingPartyId = RelyingPartyId,
            CoseKey = authenticator.CoseKey,
            Algorithm = authenticator.Algorithm,
        };

    /// <summary>What the server issued for a registration ceremony, offering both algorithms.</summary>
    private static PasskeyRegistrationExpectations RegistrationExpectationsFor(byte[] challenge) =>
        new()
        {
            Challenge = challenge,
            AllowedOrigins = [Origin],
            RelyingPartyId = RelyingPartyId,
            OfferedAlgorithms = [CoseAlgorithm.Es256, CoseAlgorithm.Rs256],
        };
}
