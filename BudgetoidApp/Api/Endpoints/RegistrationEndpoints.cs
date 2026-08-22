using System.Security.Claims;
using System.Text.Json;
using Api.Infrastructure;
using Application.Passkeys.BeginRegistration;
using Application.Passkeys.CompleteRegistration;
using Application.RecoveryCodes.GenerateRecoveryCodes;
using Application.Registration;
using Application.Sessions;

namespace Api.Endpoints;

public static class RegistrationEndpoints
{
    public static IEndpointRouteBuilder MapRegistrationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // ONE GROUP, TWO ROUTES, AND THE POLICY NAMES A SCHEME — which no other group in this application
        // does. Everything else either declares nothing and inherits the fallback policy, or is anonymous.
        //
        // NOT AllowAnonymous, and the difference matters more here than anywhere: an account cannot exist
        // without a completed provider exchange, and the scheme is what enforces that. An anonymous
        // options leg would hand a caller the ability to choose which account identifiers exist, because
        // this is the leg that mints the challenge the identifier is derived from.
        //
        // Naming the scheme is what makes AuthorizationMiddleware re-authenticate against the provider's
        // handler rather than against whatever the default resolved to — which today is a policy scheme
        // that forwards a cookie-bearing request to the session handler. Without it, a browser already
        // holding a session could reach these two routes on that session, and the account it would create
        // is one nobody's provider vouched for.
        //
        // Declaring a policy also takes both routes out of the FALLBACK policy, which carries
        // FullSessionRequirement. That is the right outcome and not a side effect worked around: this
        // caller holds no session at all, so a requirement about what kind of session may read budget
        // content has nothing to judge.
        RouteGroupBuilder group = endpoints.MapGroup("/api/registration")
            .RequireAuthorization(policy => policy
                .RequireAuthenticatedUser()
                .AddAuthenticationSchemes(ProviderAuthentication.SchemeName))
            // THE SAME ARGUMENT AS THE POLICY ABOVE, ONE STEP FURTHER IN. The policy says which scheme
            // may speak for this caller; the gate says what that scheme has to have said. An account
            // may not exist without a completed provider exchange, and it may not exist under an
            // address that exchange declines to vouch for — the second half is this filter, and it is
            // declared here, on the route table, for the reason RegistrationClaimGate argues at
            // length: an authorization requirement would answer 403 with no title and collapse two
            // refusals a caller acts on differently, and a scheme event or a new marker would move the
            // rule off the route and out of a reader's way. It runs on both legs because the options
            // leg is the one that mints the challenge the account identifier is derived from.
            .AddEndpointFilter<RegistrationClaimGate>();

        // POST, not GET, for the reason both other options legs are: it mints a nonce and persists it, so
        // it is neither safe nor idempotent, and a GET would be cacheable and prefetchable — both of which
        // spend challenges nobody asked for.
        group.MapPost("/options", async (
            ClaimsPrincipal principal,
            BeginAccountRegistrationHandler handler,
            CancellationToken cancellationToken) =>
        {
            // Both claim members are read HERE and never bound from a body, for the reason the finish
            // leg's delegate states below — this leg carries no body at all, so there is nowhere for a
            // caller to put either value even if the rule were relaxed. The subject is what the handler
            // asks the credential table about before it mints anything.
            PasskeyCreationOptions options = await handler.HandleAsync(
                new BeginAccountRegistrationCommand(SubjectOf(principal), EmailOf(principal)),
                cancellationToken);

            return TypedResults.Ok(options);
        });

        group.MapPost("/", async (
            RegistrationRequest request,
            ClaimsPrincipal principal,
            RegisterAccountHandler handler,
            HttpResponse response,
            CancellationToken cancellationToken) =>
        {
            // The two claim members are read HERE and never bound from the body, which is what keeps
            // System.Security.Claims out of the Application ring — the split SessionEndpoints already
            // makes for its session id. A subject a caller could type is an account filed under somebody
            // else's provider identity; an address a caller could type makes the email_verified gate
            // worthless.
            Issued<RegisteredAccount> issued = await handler.HandleAsync(
                new RegisterAccountCommand(
                    SubjectOf(principal),
                    EmailOf(principal),
                    request.ClientDataJson,
                    request.AttestationObject,
                    request.ClientExtensionResults,
                    request.FactorId,
                    request.WrappedContentKey,
                    request.WrappedIndexKey,
                    request.Codes),
                cancellationToken);
            RegisteredAccount registered = issued.Value;

            // AFTER THE HANDLER RETURNED, and on this route that ordering carries the most of anywhere.
            // Every refusal here leaves by exception — a spent challenge, a signature that did not verify,
            // a device that cannot hold the keys, a card that is one code short — so a cookie written
            // before this line is a cookie a refusal leaves behind, naming a session that was never
            // written, on the client of whoever was guessing.
            //
            // The null arm is unreachable: a registration that returns has established a session. It is
            // written as a pattern anyway, uniformly with the three other establishing legs, because the
            // alternative is a null-forgiving operator asserting a rule that lives in another project.
            if (issued.Handoff is { } handoff)
            {
                SessionCookie.Issue(response, handoff.Token, handoff.ExpiresAtUtc);
            }

            // 201 AND NO Location HEADER. Something was created, so 201 is the honest status; but the
            // resource created is the account, and this API exposes no address for it — a Location naming
            // one would publish the account identifier in a header, which is the one value every envelope
            // on this request was sealed against and the value a body census walks straight past.
            //
            // The body says the session and nothing else: no account id, no credential id, no session id,
            // no factor id and no echo of the address. See RegisteredAccount for each omission.
            //
            // The kind is converted here rather than left to the serializer, at the same boundary the
            // three other establishing legs convert theirs: ConfigureHttpJsonOptions registers
            // JsonStringEnumConverter with no naming policy, so a SessionKind would reach the wire as
            // "Full" while the sessions.kind column, and every other spelling of it in this product, reads
            // "full".
            return TypedResults.Created(
                (string?)null,
                new RegistrationResponse(new EstablishedSessionResponse(
                    JsonNamingPolicy.CamelCase.ConvertName(registered.Kind.ToString()),
                    registered.ExpiresAtUtc)));
        });

        return endpoints;
    }

    /// <summary>The address the provider asserted, off the principal this request authenticated as.</summary>
    /// <remarks>
    /// <see cref="RegistrationClaimGate"/> has already refused a principal carrying no <c>email</c>, and
    /// one whose address the provider does not report as verified — it is an endpoint filter on this very
    /// group, so it runs after the route's policy and before this delegate. The empty fallback exists so
    /// this expression has a total answer rather than a null-forgiving operator asserting a rule enforced
    /// one filter away; an empty address reaches <c>Email.Create</c> and is refused there.
    /// </remarks>
    private static string EmailOf(ClaimsPrincipal principal) =>
        principal.FindFirstValue("email") ?? string.Empty;

    /// <summary>The provider's stable identifier for this caller. See <see cref="EmailOf"/>.</summary>
    private static string SubjectOf(ClaimsPrincipal principal) =>
        principal.FindFirstValue("sub") ?? string.Empty;

    /// <summary>
    /// What the device produced, the passkey factor's share of the account keys, and the card of recovery
    /// codes the client minted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No <c>sub</c> and no <c>email</c>, and neither may ever be added.</b> Both arrive on the
    /// request's own authentication and are read off the principal at the call site; a member here would be
    /// a value the server would have to either ignore or trust, and trusting one would let a caller
    /// register an account under another person's provider identity or under an address nobody verified.
    /// </para>
    /// <para>
    /// <b>No member is <c>required</c></b>, deliberately and for the reason
    /// <see cref="CompleteRegistrationCommand"/> makes at length: an absent member binds to
    /// <see langword="null"/> despite the declaration and reaches the handler's own refusal, past the prf
    /// gate, rather than earning a framework 400 raised before anything signed was judged. It bites
    /// hardest on <see cref="Codes"/>, where an absent set is refused as a set of the wrong size,
    /// which is what it is.
    /// </para>
    /// <para>
    /// That envelope covers what binds, not what fails to. No body at all, a literal <c>null</c>, or a
    /// member of the wrong JSON type is a framework 400 raised before the handler is entered, and the gap
    /// is accepted for the reason the erasure states — a deserialization failure is a fact about the
    /// caller's own request and says nothing about what is stored.
    /// </para>
    /// <para>
    /// <see cref="Codes"/> is <see cref="RecoveryCodeSubmission"/> rather than a wire record of
    /// this layer's own, exactly as on the generation route: that record carries the same decision whole —
    /// all four members <see cref="string"/>, none <c>required</c> — so a copy would be four declarations
    /// able to disagree with the command about which spellings a caller may send, on the one member whose
    /// shape is a cryptographic binding rather than a convenience.
    /// </para>
    /// <para>
    /// <b><see cref="Codes"/> is named to match the sibling route, and the more descriptive-sounding
    /// spelling is the wrong one.</b> <c>RecoveryCodeGenerationRequest.Codes</c> is the other write path
    /// that accepts a set, so one name here is one wire contract — <c>codes</c> — for both. Renaming it to
    /// <c>RecoveryCodes</c> would also camel-case to <c>recoveryCodes</c> on the wire, which the request
    /// surface census refuses: it admits the token <c>recovery_code</c> only directly in front of
    /// <c>hash</c>, on purpose, so that a member able to hold key material has to be looked at. Answering
    /// that with a <c>JsonPropertyName</c> on a differently-named property is the evasion the census
    /// exists to catch — it hides the word from the census while still shipping it.
    /// </para>
    /// </remarks>
    private sealed record RegistrationRequest(
        string ClientDataJson,
        string AttestationObject,
        PasskeyClientExtensionResults? ClientExtensionResults,
        string FactorId,
        string WrappedContentKey,
        string WrappedIndexKey,
        IReadOnlyList<RecoveryCodeSubmission> Codes);

    /// <summary>
    /// Everything a completed registration says: one member, and it describes the sign-in.
    /// </summary>
    /// <remarks>
    /// The session is nested rather than flattened into a kind and an expiry at the top level, so that
    /// widening this response later cannot produce "kind present, expiry absent" — the shape
    /// <c>RecoveryCodeGenerationResponse</c> keeps for the same reason.
    /// </remarks>
    private sealed record RegistrationResponse(EstablishedSessionResponse Session);

    /// <summary>
    /// The session registration opened.
    /// </summary>
    /// <remarks>
    /// The kind is a string rather than a <c>SessionKind</c> so the spelling on the wire is the column's,
    /// decided at this boundary — see the conversion at the call site. No session id, for the reason
    /// <c>PasskeyEndpoints.AssertionResponse</c> gives: returning the row's id would hand the client a
    /// stable handle to a session, and the likeliest way this design is broken later is somebody deciding
    /// that handle is close enough to a token to start accepting it.
    /// </remarks>
    private sealed record EstablishedSessionResponse(string Kind, DateTime ExpiresAtUtc);
}
