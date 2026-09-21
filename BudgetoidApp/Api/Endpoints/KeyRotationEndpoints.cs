using System.Diagnostics.CodeAnalysis;
using Application.KeyRotations.BeginKeyRotation;
using Application.KeyRotations.ResealRows;
using Application.Passkeys;
using Application.Passkeys.Reauthentication;
using Application.Security;
using Domain.Security;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Api.Endpoints;

/// <summary>
/// The routes of a content-key rotation. Two today: the begin, which stages the next generation's
/// manifest and one copy of the new account keys per factor the account holds, and the chunk, which
/// carries a batch of rows a client has re-sealed under that generation.
/// </summary>
/// <remarks>
/// <para>
/// <b>THE BEGIN DECODES THE MANIFEST, AND IT IS THE ONLY ONE OF THE FOUR MANIFEST-CARRYING ROUTES
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
/// <para>
/// <b>The chunk decodes too, for the same structural reason and over different values.</b>
/// <see cref="ResealRowsCommand" /> carries <see cref="IndexedName" /> and
/// <see cref="NarrativeField" /> where the five sibling create commands carry the wire's
/// <see cref="string" />s, so the text has stopped existing by the time that command is built and the
/// decode has nowhere below this class to live. It is not a second copy of a rule the Application ring
/// already holds: the same two <c>Try</c> members those handlers call are called here, from the one
/// place on this path that still has text in its hands.
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

        // THE SAME THREE ABSENCES AS THE BEGIN ABOVE, AND ONE OF THEM IS LOAD-BEARING FOR A DIFFERENT
        // REASON. No RequireAuthorization and never AllowAnonymous, exactly as next door.
        // AllowsLockedSessionAttribute is the one worth restating: a chunk writes narrative ciphertext
        // claiming to be sealed under the account's keys, and a session opened by the federated
        // credential cannot hold them.
        //
        // AND NO GATE OF ITS OWN BEYOND THE FALLBACK POLICY — no re-authentication, which is a decision
        // rather than an omission. A full session already writes these very columns through the ordinary
        // create and update routes; what a chunk adds is the stamp, which is read only by a completion,
        // and a completion cannot promote anything a gated begin did not stage. A prompt per chunk would
        // also break the feature outright: a rotation of a real account is dozens of requests, so it
        // would be dozens of authenticator taps.
        //
        // 204 AND NEVER 200, AND NO Location. A chunk is all-or-nothing in one save, so there is no
        // partial-accept count to report, and a "rows remaining" member would be a second denominator
        // able to disagree with the one the begin published in its inventory. It creates no resource and
        // renames none, so there is nothing to address.
        //
        // "/chunks" AND NOT "/chunk". The act-shaped leaves elsewhere on "/api/me" go both ways and the
        // split is not arbitrary: "/erasure", "/revocation" and "/redemption" are singular because each
        // names one act an account performs once, while "/recovery-codes" is plural because it names
        // what the act produces. A chunk is the second kind and is posted many times per run, so the
        // plural reads as the collection a POST appends to rather than as a verb.
        group.MapPost("/chunks", async Task<Results<NoContent, ValidationProblem>> (
            ResealChunkRequest request,
            ResealRowsHandler handler,
            CancellationToken cancellationToken) =>
        {
            // THE DECODE IS HERE AND IT IS THE `Try` SHAPE, WHICH IS THE DIFFERENCE BETWEEN A 400 AND A
            // 500. NarrativeField.Sealed refuses a malformed envelope with ArgumentException and
            // IndexedName.Of refuses a wrong-width index with the same type, and nothing in
            // Api/Infrastructure maps ArgumentException — it reaches GlobalExceptionHandler as an
            // unexpected error and answers 500. So a route that decoded with the raw base64url decoder
            // and handed the bytes straight to those factories would FAULT on a request that is merely
            // wrong. Both Try members below carry the alphabet, the ceiling, the floor and the version,
            // and the factories they feed then cannot throw.
            //
            // ONE ARM AT A TIME AND ONE REFUSAL AT A TIME, which is where this departs from the sibling
            // update handlers that judge all three of a row's members before refusing any. An arm is an
            // array a caller sized, so accumulating every malformed entry would make the size of the
            // refusal the sender's choice; the key names the arm and the sentence names the entry's
            // position in it, which is what a client needs to fix the batch it holds.
            if (!TryReadArm(request.Accounts, ReadAccount, out List<ResealedAccount>? accounts,
                    out string? refusal))
            {
                return Refused(nameof(ResealChunkRequest.Accounts), refusal);
            }

            if (!TryReadArm(request.Payees, ReadPayee, out List<ResealedPayee>? payees, out refusal))
            {
                return Refused(nameof(ResealChunkRequest.Payees), refusal);
            }

            if (!TryReadArm(request.CategoryGroups, ReadCategoryGroup,
                    out List<ResealedCategoryGroup>? categoryGroups, out refusal))
            {
                return Refused(nameof(ResealChunkRequest.CategoryGroups), refusal);
            }

            if (!TryReadArm(request.Categories, ReadCategory, out List<ResealedCategory>? categories,
                    out refusal))
            {
                return Refused(nameof(ResealChunkRequest.Categories), refusal);
            }

            if (!TryReadArm(request.Transactions, ReadTransaction,
                    out List<ResealedTransaction>? transactions, out refusal))
            {
                return Refused(nameof(ResealChunkRequest.Transactions), refusal);
            }

            // NOTHING IS JUDGED ABOUT THE SIZE OF THIS BODY HERE, AND A CEILING ADDED ON THIS LINE WOULD
            // BE A DEFECT RATHER THAN A HARDENING. BeginKeyRotationHandler.MaxChunkBytes is a budget the
            // begin PUBLISHES so a client can size its batches under the request body cap Api/Program.cs
            // sets; enforcing it again here would refuse bodies that are legal under that cap and would
            // split one condition across a 400 from this delegate and a 413 from the server. Enforcement
            // is the host's, in one place, and the published number stays advice.
            await handler.HandleAsync(
                new ResealRowsCommand(
                    request.RotationId, accounts, payees, categoryGroups, categories, transactions),
                cancellationToken);

            return TypedResults.NoContent();
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

    /// <summary>
    /// Turns one entry of an arm into the command's own, or reports the sentence a client can correct.
    /// </summary>
    /// <remarks>
    /// <b>A delegate rather than a <c>Func</c>, because the refusal travels beside the value.</b> A
    /// <c>Func&lt;TEntry, int, TResealed?&gt;</c> would say "this entry could not be read" and nothing
    /// about which of its two or three members was at fault, and the five arms differ in exactly that:
    /// a payee has a name and an index, a category has a note beside them, a transaction has only the
    /// note. The sentence is the whole of what a client gets, so it is carried rather than derived.
    /// </remarks>
    /// <param name="entry">The entry as it bound, never <see langword="null" />.</param>
    /// <param name="ordinal">Its position in the arm, which is the only thing naming it to a caller.</param>
    /// <param name="resealed">The command's entry, when every member of it was read.</param>
    /// <param name="refusal">What a client has to correct, when one was not.</param>
    private delegate bool EntryReader<in TEntry, TResealed>(
        TEntry entry,
        int ordinal,
        [NotNullWhen(true)] out TResealed? resealed,
        [NotNullWhen(false)] out string? refusal);

    /// <summary>
    /// Reads one arm of a chunk into the command's entries, in the order the caller sent them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>EVERY ENTRY IS FORWARDED AND THE ORDER IS THE CALLER'S.</b> A <c>FirstOrDefault()</c>, a
    /// <c>Take(1)</c> or a scalar where this list belongs is the defect this walk exists to refuse: it
    /// answers 204 to a chunk of two hundred rows having re-sealed one, after which the client counts
    /// all two hundred as done and the completeness gate refuses a run nobody can finish.
    /// <c>ResealChunkEndpointTests</c> holds it by carrying two rows on two of the five arms under
    /// distinct labels and reading every row back by position, so a skipped or duplicated entry fails
    /// naming the row rather than the arm.
    /// </para>
    /// <para>
    /// <b>Nothing is de-duplicated here</b>, which is <see cref="TryReadSeals" />' argument about the
    /// seals applied to a row identifier: a <c>DistinctBy</c> on this line reads like housekeeping and
    /// turns a chunk naming one row twice under two different envelopes into a chunk naming it once,
    /// with nothing anywhere saying which entry was dropped. Kept whole, the repeat reaches
    /// <c>ResealRowsHandler</c>, which re-seals that row twice in a row under the same stamp — the same
    /// outcome, arrived at from the request the client actually sent.
    /// </para>
    /// <para>
    /// <b>A missing array is read as an empty arm rather than refused</b>, for the reason
    /// <see cref="TryReadSeals" /> gives about its own: the declaration says the list is present and a
    /// missing JSON array binds to <see langword="null" /> anyway, and "this chunk named no rows of that
    /// kind" is what such a request means — most chunks of a real rotation carry one or two arms. A
    /// <see langword="null" /> <b>entry</b> is a different thing, and it is refused here, because
    /// <c>[null]</c> carries neither an identifier nor any bytes and there is nothing below that could
    /// read one.
    /// </para>
    /// </remarks>
    private static bool TryReadArm<TEntry, TResealed>(
        IReadOnlyList<TEntry>? submitted,
        EntryReader<TEntry, TResealed> read,
        [NotNullWhen(true)] out List<TResealed>? resealed,
        [NotNullWhen(false)] out string? refusal)
        where TEntry : class
        where TResealed : class
    {
        resealed = [];
        refusal = null;

        if (submitted is null)
        {
            return true;
        }

        for (int ordinal = 0; ordinal < submitted.Count; ordinal++)
        {
            TEntry? entry = submitted[ordinal];

            if (entry is null)
            {
                resealed = null;
                refusal = $"Entry {ordinal} of this arm is absent. Every entry must name the row it "
                          + "re-seals and carry that row's new sealed values.";

                return false;
            }

            if (!read(entry, ordinal, out TResealed? one, out refusal))
            {
                resealed = null;

                return false;
            }

            resealed.Add(one);
        }

        return true;
    }

    private static bool ReadAccount(
        ResealedAccountRequest entry,
        int ordinal,
        [NotNullWhen(true)] out ResealedAccount? resealed,
        [NotNullWhen(false)] out string? refusal)
    {
        resealed = null;

        if (!TryReadName(entry.Name, entry.NameKey, ordinal, out IndexedName? name, out refusal))
        {
            return false;
        }

        resealed = new ResealedAccount(entry.Id, name);

        return true;
    }

    private static bool ReadPayee(
        ResealedPayeeRequest entry,
        int ordinal,
        [NotNullWhen(true)] out ResealedPayee? resealed,
        [NotNullWhen(false)] out string? refusal)
    {
        resealed = null;

        if (!TryReadName(entry.Name, entry.NameKey, ordinal, out IndexedName? name, out refusal))
        {
            return false;
        }

        resealed = new ResealedPayee(entry.Id, name);

        return true;
    }

    private static bool ReadCategoryGroup(
        ResealedCategoryGroupRequest entry,
        int ordinal,
        [NotNullWhen(true)] out ResealedCategoryGroup? resealed,
        [NotNullWhen(false)] out string? refusal)
    {
        resealed = null;

        if (!TryReadName(entry.Name, entry.NameKey, ordinal, out IndexedName? name, out refusal))
        {
            return false;
        }

        if (!TryReadDescription(entry.Description, ordinal, out NarrativeField? description, out refusal))
        {
            return false;
        }

        resealed = new ResealedCategoryGroup(entry.Id, name, description);

        return true;
    }

    private static bool ReadCategory(
        ResealedCategoryRequest entry,
        int ordinal,
        [NotNullWhen(true)] out ResealedCategory? resealed,
        [NotNullWhen(false)] out string? refusal)
    {
        resealed = null;

        if (!TryReadName(entry.Name, entry.NameKey, ordinal, out IndexedName? name, out refusal))
        {
            return false;
        }

        if (!TryReadDescription(entry.Description, ordinal, out NarrativeField? description, out refusal))
        {
            return false;
        }

        resealed = new ResealedCategory(entry.Id, name, description);

        return true;
    }

    private static bool ReadTransaction(
        ResealedTransactionRequest entry,
        int ordinal,
        [NotNullWhen(true)] out ResealedTransaction? resealed,
        [NotNullWhen(false)] out string? refusal)
    {
        resealed = null;

        if (!TryReadDescription(entry.Description, ordinal, out NarrativeField? description, out refusal))
        {
            return false;
        }

        resealed = new ResealedTransaction(entry.Id, description);

        return true;
    }

    /// <summary>
    /// Reads one entry's re-sealed name and the blind index beside it, refusing either half on its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two decoders and not one, which is the near miss.</b> The index sits beside the ciphertext,
    /// arrives in the same entry and lands in the same row, so the envelope's decoder looks like the
    /// natural one for both — and it would refuse roughly 255 blind indexes in 256, because a keyed
    /// digest has no version byte to satisfy the framing with. <see cref="BlindIndexText" /> carries
    /// that argument in full.
    /// </para>
    /// <para>
    /// <b>Both refusals happen before <see cref="IndexedName.Of" /> is called, and that ordering is
    /// what the status code rests on.</b> That factory raises <see cref="ArgumentException" /> on a
    /// malformed envelope and on a wrong-width index, and nothing in <c>Api/Infrastructure</c> maps
    /// that type — the request would reach <c>GlobalExceptionHandler</c> as an unexpected error and
    /// answer 500 for a body that is merely wrong. With both <c>Try</c> members ahead of it the factory
    /// is handed values it has already been proved to accept.
    /// </para>
    /// <para>
    /// <b>The two caps come from the types that own them and are never written out here.</b>
    /// <see cref="IndexedName.Of" /> names <see cref="NarrativeFieldLimits.NameBytes" /> for itself, so
    /// a ceiling mistyped on this path is refused there as a defect in this codebase rather than
    /// stored; the index has one legal width and its decoder owns it.
    /// </para>
    /// </remarks>
    private static bool TryReadName(
        string? name,
        string? nameKey,
        int ordinal,
        [NotNullWhen(true)] out IndexedName? indexed,
        [NotNullWhen(false)] out string? refusal)
    {
        indexed = null;

        if (!CiphertextEnvelopeText.TryDecode(
                name, NarrativeFieldLimits.NameBytes, out byte[]? envelope))
        {
            refusal = $"Entry {ordinal} of this arm must carry the re-sealed name as base64url text "
                      + $"decoding to a sealed envelope of at most {NarrativeFieldLimits.NameBytes} "
                      + $"bytes carrying envelope version {CiphertextEnvelope.Version}.";

            return false;
        }

        if (!BlindIndexText.TryDecode(nameKey, out byte[]? blindIndex))
        {
            refusal = $"Entry {ordinal} of this arm must carry the name index as base64url text "
                      + $"decoding to exactly {IndexedName.BlindIndexLength} bytes, recomputed under "
                      + "the next generation's index key.";

            return false;
        }

        indexed = IndexedName.Of(envelope, blindIndex);
        refusal = null;

        return true;
    }

    /// <summary>
    /// Reads one entry's re-sealed note, or reports that the entry carries none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>THE ABSENCE TEST IS <c>is null</c> AND THE SPELLING IS THE WHOLE OF A RULE</b>, as it is on
    /// the sibling update handlers: never <c>IsNullOrEmpty</c> and never <c>IsNullOrWhiteSpace</c>,
    /// because the decoder underneath refuses <see langword="null" /> and <c>""</c> identically and the
    /// distinction cannot live down there. Under a forgiving spelling an empty string would reach
    /// <c>NarrativeReseal</c> as "this row holds no note", which on a row that holds one is the
    /// presence rule's refusal arriving for a reason nobody can act on.
    /// </para>
    /// <para>
    /// <b>An absent note is not an edit on this route, and that is what separates it from the PUT body
    /// next door.</b> <c>NarrativeReseal</c> refuses a reseal that changes whether the column holds a
    /// value in either direction, so leaving the member out of an entry whose row holds a note is a
    /// refusal one ring down rather than a way to clear the column. Nothing here judges that — the
    /// database is what says which rows hold notes — so this member is forwarded exactly as it bound.
    /// </para>
    /// <para>
    /// <b><see cref="NarrativeFieldLimits.DescriptionBytes" /> and not
    /// <see cref="NarrativeFieldLimits.NameBytes" />, and this line is the only holder of that number on
    /// this path.</b> The name's cap is stated twice — here and inside <see cref="IndexedName.Of" /> —
    /// so a mistyped name ceiling is refused by the domain; the description's is not, so widening it
    /// lets an over-cap value travel the whole ring and land on the column's check constraint as a
    /// <c>23514</c> nothing translates.
    /// </para>
    /// </remarks>
    private static bool TryReadDescription(
        string? description,
        int ordinal,
        out NarrativeField? sealedNote,
        [NotNullWhen(false)] out string? refusal)
    {
        sealedNote = null;
        refusal = null;

        if (description is null)
        {
            return true;
        }

        if (!CiphertextEnvelopeText.TryDecode(
                description, NarrativeFieldLimits.DescriptionBytes, out byte[]? envelope))
        {
            refusal = $"Entry {ordinal} of this arm must carry the re-sealed description as base64url "
                      + "text decoding to a sealed envelope of at most "
                      + $"{NarrativeFieldLimits.DescriptionBytes} bytes carrying envelope version "
                      + $"{CiphertextEnvelope.Version}.";

            return false;
        }

        // The factory cannot refuse what the decoder above accepted — same floor, same version, same
        // ceiling — so this is the shape check restated where the Domain owns it rather than a second
        // judgement. It is called at all because ResealRowsCommand carries the Domain value: there is no
        // other way to build one, which is the asymmetry that put this decode in the endpoint.
        sealedNote = NarrativeField.Sealed(envelope, NarrativeFieldLimits.DescriptionBytes);

        return true;
    }

    /// <summary>
    /// One malformed member as the body a client reads: a 400 keyed on the arm it was in.
    /// </summary>
    /// <remarks>
    /// The same <c>ValidationProblemDetails</c> shape and title <c>ValidationExceptionHandler</c>
    /// writes for the refusals <c>ResealRowsHandler</c> raises, so a caller cannot tell an edge refusal
    /// from one of the handler's by what comes back. Keyed on the <b>wire</b> member's name, which for
    /// every arm of a chunk is also the command's — the one place the two rings differ on this route is
    /// the entry records, and no key here names one.
    /// </remarks>
    private static ValidationProblem Refused(string arm, string refusal) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [arm] = [refusal] });

    /// <summary>
    /// One account row's new values, as the wire carries them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><see cref="Id" /> is a <see cref="Guid" /> and not base64url text, which is the one place a
    /// chunk parts company with the create bodies it otherwise resembles.</b> A create carries the row
    /// identifier as text because the client minted it and it is the associated data the envelope beside
    /// it was sealed against — a spelling this server cannot reproduce is a value that never opens
    /// again. A chunk names a row that already exists and whose identifier this server rendered, so it
    /// matches the update paths: the identifier selects a row and nothing is sealed against what this
    /// body says about it.
    /// </para>
    /// <para>
    /// <b>No member is <c>required</c></b>, for the reason <see cref="SealRequest" /> gives about its
    /// own: a member bound to <see langword="null" /> is refused by the decode with a sentence naming
    /// the entry's position in its arm, which is what a client sending hundreds of rows needs, rather
    /// than by a framework 400 describing the record's shape.
    /// </para>
    /// <para>
    /// <b>No stamp member, on this record or any of its four siblings.</b> The run is
    /// <see cref="ResealChunkRequest.RotationId" />, one level up, because a run stamps every row it
    /// rewrites with the same identifier — a per-entry stamp would be a second place for one chunk to
    /// disagree with itself, and the disagreement would be invisible until a completeness gate refused a
    /// run that looked finished.
    /// </para>
    /// </remarks>
    /// <param name="Id">The row this entry re-seals, as this server rendered it.</param>
    /// <param name="Name">
    /// The name under the next generation's content key, as one unpadded base64url AEAD envelope.
    /// </param>
    /// <param name="NameKey">
    /// The blind index recomputed over that same name under the next generation's index key, as
    /// unpadded base64url. It rides this entry because a rotation replaces the index key as well as the
    /// content key.
    /// </param>
    private sealed record ResealedAccountRequest(Guid Id, string Name, string NameKey);

    /// <inheritdoc cref="ResealedAccountRequest" />
    /// <param name="Id"><inheritdoc cref="ResealedAccountRequest" path="/param[@name='Id']" /></param>
    /// <param name="Name"><inheritdoc cref="ResealedAccountRequest" path="/param[@name='Name']" /></param>
    /// <param name="NameKey">
    /// <inheritdoc cref="ResealedAccountRequest" path="/param[@name='NameKey']" />
    /// </param>
    private sealed record ResealedPayeeRequest(Guid Id, string Name, string NameKey);

    /// <summary>
    /// One category-group row's new values, as the wire carries them.
    /// </summary>
    /// <remarks>
    /// <b><see cref="Description" /> is nullable and its absence is judged rather than obeyed</b>, which
    /// is the difference from the update body on the same table: <c>NarrativeReseal</c> refuses a reseal
    /// that changes whether the column holds a value in either direction, so omitting it for a row that
    /// holds a note is a refusal and not a way to skip the column.
    /// </remarks>
    /// <param name="Id"><inheritdoc cref="ResealedAccountRequest" path="/param[@name='Id']" /></param>
    /// <param name="Name"><inheritdoc cref="ResealedAccountRequest" path="/param[@name='Name']" /></param>
    /// <param name="NameKey">
    /// <inheritdoc cref="ResealedAccountRequest" path="/param[@name='NameKey']" />
    /// </param>
    /// <param name="Description">
    /// The note under the next generation's content key, as one unpadded base64url AEAD envelope, or
    /// absent when the row holds none.
    /// </param>
    private sealed record ResealedCategoryGroupRequest(
        Guid Id,
        string Name,
        string NameKey,
        string? Description);

    /// <inheritdoc cref="ResealedCategoryGroupRequest" />
    /// <param name="Id"><inheritdoc cref="ResealedAccountRequest" path="/param[@name='Id']" /></param>
    /// <param name="Name"><inheritdoc cref="ResealedAccountRequest" path="/param[@name='Name']" /></param>
    /// <param name="NameKey">
    /// <inheritdoc cref="ResealedAccountRequest" path="/param[@name='NameKey']" />
    /// </param>
    /// <param name="Description">
    /// <inheritdoc cref="ResealedCategoryGroupRequest" path="/param[@name='Description']" />
    /// </param>
    private sealed record ResealedCategoryRequest(
        Guid Id,
        string Name,
        string NameKey,
        string? Description);

    /// <summary>
    /// One transaction row's new value, as the wire carries it.
    /// </summary>
    /// <remarks>
    /// <b>A note and no name, because <c>transactions</c> has none.</b> It is the only arm whose whole
    /// narrative is a nullable column, which is why a note-less transaction — what most rows of a real
    /// account are — is a row a chunk never names at all rather than one it sends an empty entry for.
    /// </remarks>
    /// <param name="Id"><inheritdoc cref="ResealedAccountRequest" path="/param[@name='Id']" /></param>
    /// <param name="Description">
    /// <inheritdoc cref="ResealedCategoryGroupRequest" path="/param[@name='Description']" />
    /// </param>
    private sealed record ResealedTransactionRequest(Guid Id, string? Description);

    /// <summary>
    /// One chunk of a rotation: the run it continues, and the rows a client has re-sealed under that
    /// run's generation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Five arrays and no budget arm, which is a decision and not a gap.</b> A budget arm would need
    /// <c>rotation_id</c> on the <c>budgets</c> <c>GRANT UPDATE</c> column list, where the requirement
    /// is <c>name</c> and no other column; <c>Budget.ResealName</c> is <see langword="internal" />, so a
    /// sixth arm is a compile error rather than a runtime <c>42501</c>.
    /// </para>
    /// <para>
    /// <b>Arrays rather than objects keyed on the row, the shape <see cref="BeginRotationRequest.Seals" />'
    /// own remarks argue for.</b> A JSON object makes a repeated row identifier unconstructible on the
    /// wire — the binder
    /// drops the repeat, last wins — so a chunk naming one row twice under two different envelopes would
    /// arrive as one, with nothing saying so. As arrays the repeat survives into the handler and the
    /// request stays the request the client sent.
    /// </para>
    /// <para>
    /// <b>No account is named here and none ever may be.</b> The rows a chunk may reach are the ambient
    /// budget's, resolved from the session; an identifier on this body would be an account a caller
    /// could choose, and the five entities' query filters are what turn a foreign row into the 404 this
    /// route answers.
    /// </para>
    /// <para>
    /// <b>Carrying no count of its own is deliberate.</b> The begin publishes a per-table inventory and
    /// a chunk budget, and a "rows in this chunk" or "rows remaining" member here would be a second
    /// denominator able to disagree with it — a progress bar that never reaches the end.
    /// </para>
    /// </remarks>
    /// <param name="RotationId">The run this chunk continues, as the begin staged it.</param>
    /// <param name="Accounts">The account rows this chunk re-seals.</param>
    /// <param name="Payees">The payee rows this chunk re-seals.</param>
    /// <param name="CategoryGroups">The category-group rows this chunk re-seals.</param>
    /// <param name="Categories">The category rows this chunk re-seals.</param>
    /// <param name="Transactions">The transaction rows this chunk re-seals.</param>
    private sealed record ResealChunkRequest(
        Guid RotationId,
        IReadOnlyList<ResealedAccountRequest> Accounts,
        IReadOnlyList<ResealedPayeeRequest> Payees,
        IReadOnlyList<ResealedCategoryGroupRequest> CategoryGroups,
        IReadOnlyList<ResealedCategoryRequest> Categories,
        IReadOnlyList<ResealedTransactionRequest> Transactions);
}
