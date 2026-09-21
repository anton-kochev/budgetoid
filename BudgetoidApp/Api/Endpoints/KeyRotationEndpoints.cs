using System.Diagnostics.CodeAnalysis;
using Application.KeyRotations.BeginKeyRotation;
using Application.Passkeys;
using Application.Passkeys.Reauthentication;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Api.Endpoints;

/// <summary>
/// The routes of a content-key rotation. One today: the begin, which stages the next generation's
/// manifest and one copy of the new account keys per factor the account holds.
/// </summary>
/// <remarks>
/// <para>
/// <b>THIS ENDPOINT DECODES THE MANIFEST, AND IT IS THE ONLY ONE OF THE FOUR MANIFEST-CARRYING ROUTES
/// THAT DOES. READ THIS BEFORE "TIDYING" THE DECODE DOWN INTO THE COMMAND.</b>
/// <c>RevokePasskeyCommand.Manifest</c>, <c>GenerateRecoveryCodesCommand.Manifest</c> and
/// <c>CompleteRegistrationCommand.Manifest</c> are all <see cref="string" />, so their handlers run
/// <see cref="FactorManifestEnvelope.TryDecode" /> and the alphabet, the floor and the ceiling are all
/// judged inside the Application ring. <see cref="BeginKeyRotationCommand.StagedManifest" /> is
/// <c>ReadOnlyMemory&lt;byte&gt;</c> — a deliberate asymmetry, argued on that record — so the text has
/// stopped existing by the time the command is built and the decode has nowhere else to live.
/// </para>
/// <para>
/// <b>Nothing below re-checks, which is what makes the asymmetry load-bearing rather than cosmetic.</b>
/// <c>KeyRotation.Begin</c> judges the staged manifest for emptiness and against
/// <c>FactorManifest.MaximumBytes</c> and nothing else; the column's own rule is
/// <c>length(staged_manifest) between 1 and 4096</c>, which is a <c>bytea</c> saying it is not empty.
/// So the AEAD framing's 29-byte floor and the <c>0x01</c> version byte have <b>no holder on this path
/// but the call below</b>: a twenty-eight-byte manifest stores perfectly well, promotes perfectly well,
/// and opens for nobody. Moving this call into <see cref="BeginKeyRotationCommand" /> would mean giving
/// that record a <see cref="string" /> member, which is the change its own remarks refuse.
/// </para>
/// <para>
/// <b>What the decode is not.</b> A manifest is sealed under the account's content key, which this
/// server has never held, so this call judges framing and never contents — a manifest naming nobody,
/// or one naming a factor set that disagrees with the seals beside it, decodes here and stores.
/// <see cref="FactorManifestEnvelope" /> carries that argument in full, including why it is not one of
/// the two decoders the registration path runs beside it: three framings, and all three lead with
/// <c>0x01</c>.
/// </para>
/// </remarks>
public static class KeyRotationEndpoints
{
    public static IEndpointRouteBuilder MapKeyRotationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // A GROUP OF ITS OWN OVER THE WHOLE PREFIX, rather than a "/key-rotation" leaf on the "/api/me"
        // group the erasure, the export, the credential list and the recovery codes share. A rotation is
        // a run with legs: the routes that carry a chunk and complete it land underneath this same
        // prefix, and a group that already spans it is what keeps them from being three unrelated leaves
        // on a group that means "the current principal". It stays under "/api/me" because the account
        // whose keys move is whichever one the request is authenticated as — this route names no account
        // and never may.
        RouteGroupBuilder group = endpoints.MapGroup("/api/me/key-rotation");

        // THE ROUTE DECLARES NO METADATA, AND THAT IS THREE SEPARATE RULES WEARING ONE ABSENCE.
        //
        // No RequireAuthorization: the fallback policy already covers every route that declares
        // nothing, and restating it here would stop the one line that defines the anonymous surface
        // being the only one. And never AllowAnonymous — AnonymousSurfaceTests compares the whole
        // AllowAnonymous set against a written list, so one here is a red there rather than a quiet
        // widening, which is the point of that test.
        //
        // No AllowsLockedSessionAttribute: the opted-out set is exactly POST /api/me/session/revocation
        // and this route is not it. A rotation reseals every narrative column in the account, so a
        // session opened by the federated credential reaching it would be a caller who cannot hold the
        // account's keys asking for the generation those keys are sealed under to move.
        //
        // 200 AND NEVER 201, AND NO Location. A begin creates no resource this API exposes at an
        // address: there is no rotation to GET, nothing to redirect a client to, and the identifier was
        // minted by the caller rather than assigned here — so a 201 would promise a resource that does
        // not exist and a Location would name a route that answers 404.
        group.MapPost("/", async Task<Results<Ok<KeyRotationBegun>, ValidationProblem>> (
            BeginRotationRequest request,
            BeginKeyRotationHandler handler,
            CancellationToken cancellationToken) =>
        {
            // The framing floor and the version byte, applied here because nothing under this line can
            // — see this class's remarks. A ValidationProblem rather than a thrown
            // Domain.Common.ValidationException so the refusal is a value this delegate returns rather
            // than control flow through the exception handler; the body is the same
            // ValidationProblemDetails ValidationExceptionHandler writes, with the same title, so a
            // caller cannot tell an edge refusal from one of the handler's by the shape of what comes
            // back.
            //
            // Keyed on the WIRE member's name rather than on the command's: the caller can correct
            // "manifest", and has never heard of StagedManifest. Every other refusal on this path keys
            // on a name the two rings happen to share, and this is the one place they differ.
            if (!FactorManifestEnvelope.TryDecode(request.Manifest, out byte[]? manifest))
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                {
                    [nameof(BeginRotationRequest.Manifest)] =
                    [
                        "The staged factor manifest must be an unpadded base64url AEAD envelope of the "
                        + "account's content key.",
                    ],
                });
            }

            if (!TryReadSeals(request.Seals, out List<RotationSeal>? seals))
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                {
                    [nameof(BeginRotationRequest.Seals)] =
                    [
                        "Every staged seal must carry the next generation's account keys as unpadded "
                        + "base64url.",
                    ],
                });
            }

            KeyRotationBegun begun = await handler.HandleAsync(
                new BeginKeyRotationCommand(
                    new ReauthenticationAssertion(
                        request.CredentialId,
                        request.ClientDataJson,
                        request.AuthenticatorData,
                        request.Signature,
                        request.UserHandle),
                    request.RotationId,
                    manifest,
                    request.RotationEpoch,
                    seals),
                cancellationToken);

            // The application record straight out, the way the recovery-code count leg answers its own:
            // camelCase comes from ConfigureHttpJsonOptions rather than from this call site, and a
            // response record of this layer's own would be six counts able to disagree with the six
            // RotationInventory declares.
            return TypedResults.Ok(begun);
        });

        return endpoints;
    }

    /// <summary>
    /// Turns the seals a caller sent into the command's own, or returns <see langword="false" /> when one
    /// of them is not text this API carries binary in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>THE ORDER IS PRESERVED AND NOTHING IS DE-DUPLICATED HERE, WHICH IS THE WHOLE POINT OF THIS
    /// METHOD BEING WRITTEN OUT RATHER THAN BEING A <c>Select</c> SOMEBODY WILL LATER "TIDY".</b> A
    /// <c>.DistinctBy(seal =&gt; seal.FactorId)</c> or a <c>.ToDictionary(…)</c> on this line reads like
    /// housekeeping and silently defeats <c>BeginKeyRotationHandler</c>'s duplicate-factor refusal:
    /// thirteen seals naming twelve factors would arrive as twelve, satisfy the set comparison against
    /// the account's twelve live factors perfectly, answer 200, and leave the account one seal short of
    /// what its client believed it sent — with nothing anywhere saying which factor was repeated. It is
    /// the same defect a JSON <b>object</b> keyed on the factor has natively, which is why
    /// <see cref="BeginKeyRotationCommand.Seals" /> is a list and why the wire member is an array.
    /// </para>
    /// <para>
    /// <b>The decode is the alphabet and a ceiling and deliberately nothing else.</b> The width's floor
    /// and the framing version belong to <c>KeyRotationSeal.For</c>, which reads both off the
    /// <c>wrapped_account_keys</c> column a promotion copies the value into — so a second statement of
    /// either number here would be one fact able to disagree with itself, and it would move the refusal
    /// in front of the re-authentication gate, where a request the handler answers 401 today would start
    /// answering 400. The ceiling is the exception and it is not a width: <c>PasskeyEncoding.TryDecode</c>
    /// requires one, and refusing an over-long member before allocating for it is the one judgement that
    /// has to happen before the gate. The number of seals needs no bound of its own — <c>Api/Program.cs</c>
    /// caps the request body, and a seal is 158 bytes of base64url inside it.
    /// </para>
    /// <para>
    /// A missing array binds to <see langword="null" /> despite the non-nullable declaration and is
    /// forwarded as an empty list rather than refused here: "this request staged no seal" is a sentence
    /// <c>BeginKeyRotationHandler</c> already owns, it is worded for the set rather than for the
    /// encoding, and it is answered <em>past</em> the gate. A <see langword="null" /> <em>entry</em> is a
    /// different thing — <c>[null]</c> is an element that carries neither a factor nor bytes — and it is
    /// refused here, because there is nothing below that could read one.
    /// </para>
    /// </remarks>
    private static bool TryReadSeals(
        IReadOnlyList<SealRequest>? submitted,
        [NotNullWhen(true)] out List<RotationSeal>? seals)
    {
        seals = [];

        if (submitted is null)
        {
            return true;
        }

        foreach (SealRequest? seal in submitted)
        {
            if (seal is null
                || !PasskeyEncoding.TryDecode(
                    seal.EncapsulatedAccountKeys,
                    PasskeyPayloadLimits.EncapsulatedAccountKeysBytes,
                    out byte[]? encapsulated))
            {
                seals = null;

                return false;
            }

            seals.Add(new RotationSeal(seal.FactorId, encapsulated));
        }

        return true;
    }

    /// <summary>
    /// One factor's copy of the next generation's account keys, as the wire carries it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A record of this layer's own rather than <see cref="RotationSeal" /> itself, because that record
    /// carries <c>ReadOnlyMemory&lt;byte&gt;</c> and this one carries the base64url text binary crosses
    /// JSON in everywhere in this API. The two members are the whole of it: nothing else about a seal is
    /// a thing a caller can honestly say.
    /// </para>
    /// <para>
    /// <b>Not <c>required</c></b>, for the reason <see cref="BeginRotationRequest" /> gives about its
    /// own members: an entry missing its envelope binds to <see langword="null" />, which
    /// <see cref="TryReadSeals" /> refuses as text it could not decode rather than as a framework 400
    /// describing the member's shape.
    /// </para>
    /// </remarks>
    /// <param name="FactorId">
    /// The factor this copy was encapsulated to — the <c>wrapped_account_keys.factor_id</c> of the row a
    /// promotion will write it into.
    /// </param>
    /// <param name="EncapsulatedAccountKeys">
    /// The next generation's content key and index key as one 64-byte plaintext, <b>content key
    /// first</b>, encapsulated to that factor's public key. The order of the two halves is a client
    /// contract no server-side check can reach.
    /// </param>
    private sealed record SealRequest(Guid FactorId, string EncapsulatedAccountKeys);

    /// <summary>
    /// The run being opened — which run, which generation, one seal per factor — and the assertion that
    /// authorizes it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The five assertion members are byte-identical to <c>ErasureRequest</c>'s and
    /// <c>RevocationRequest</c>'s, members and all, and <see cref="UserHandle" /> is forwarded exactly as
    /// it bound.</b> A caller comparing the erasure, revocation, generation and rotation gates must learn
    /// nothing from the difference between them, so a renamed member here, or a
    /// <see langword="null" /> coerced to an empty string on the way to
    /// <c>ReauthenticationAssertion</c>, would make this one gate answer differently from the other
    /// three for a caller holding a stolen handle.
    /// </para>
    /// <para>
    /// <b>No member is <c>required</c></b>, deliberately and identically to those two. Every member
    /// bound to <see langword="null" /> — a body of <c>{}</c>, or one naming only some of them — reaches
    /// the gate's own decode, which answers the same 401 every other refusal on this route answers.
    /// Marking them required would buy a framework 400 that tells a caller holding a stolen bearer token
    /// that its proof was the thing found wanting. The two members judged before the gate,
    /// <see cref="Manifest" /> and <see cref="Seals" />, are refused for their encoding and never for
    /// their meaning — a fact about the caller's own bytes, which says nothing about this account.
    /// </para>
    /// <para>
    /// That envelope covers what binds, not what fails to. No body at all, a literal <c>null</c>, or a
    /// member of the wrong JSON type is a framework 400 raised before this delegate is entered, and the
    /// gap is accepted for the reason the erasure states.
    /// </para>
    /// <para>
    /// <see cref="RotationId" /> and each seal's factor travel as the hyphenated 36-character form, and
    /// every binary member as unpadded base64url. <see cref="RotationEpoch" /> is the only member that is
    /// neither, for the reason <c>RevokePasskeyCommand.RotationEpoch</c> gives about its own: it is a
    /// number the client binds into the manifest's associated data, not text standing for bytes.
    /// </para>
    /// </remarks>
    /// <param name="RotationId">The client-minted identifier of this run.</param>
    /// <param name="Manifest">
    /// The next generation's manifest of factor public keys, sealed under the account's content key, as
    /// one unpadded base64url AEAD envelope.
    /// </param>
    /// <param name="RotationEpoch">The generation the manifest above will be filed at.</param>
    /// <param name="Seals">
    /// One copy of the next generation's account keys per factor the account holds. An <b>array</b>, so
    /// a repeated factor survives binding and is refused rather than absorbed — see
    /// <see cref="TryReadSeals" />.
    /// </param>
    /// <param name="CredentialId">The WebAuthn handle of the authenticator that signed the assertion.</param>
    /// <param name="ClientDataJson">The assertion's client data.</param>
    /// <param name="AuthenticatorData">The assertion's authenticator data.</param>
    /// <param name="Signature">The assertion's signature.</param>
    /// <param name="UserHandle">The assertion's user handle, which an authenticator may omit.</param>
    private sealed record BeginRotationRequest(
        Guid RotationId,
        string Manifest,
        int RotationEpoch,
        IReadOnlyList<SealRequest> Seals,
        string CredentialId,
        string ClientDataJson,
        string AuthenticatorData,
        string Signature,
        string? UserHandle);
}
