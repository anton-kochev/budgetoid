using System.Reflection;
using Api.Endpoints;
using Application.Abstractions;
using Domain.Accounts;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace IntegrationTests;

/// <summary>
/// The census that carries the other half of the cookie's <c>HttpOnly</c> flag: no response this API
/// writes carries the handle a session is presented by, or an identifier that names one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a census and not three assertions.</b> Three routes establish a session today and each has a
/// test of its own pinning the members of its body whole. What none of them can do is cover the fourth
/// route — the one added next, by somebody who has just read a handler that now has a raw token in scope
/// and needs to get it to the client. The cookie is the handle and it is <c>HttpOnly</c> precisely so
/// that nothing else is; a token echoed into a body is readable by every script on the page, and it works
/// exactly as well as the cookie, so nothing downstream fails.
/// </para>
/// <para>
/// <b>The surface is derived from the route table rather than listed.</b> A written-down set of response
/// records stays green on the day a new endpoint returns a type the list never heard of, which is
/// precisely the commit this exists to catch. It is derived from what the route delegates <em>return</em>,
/// which is a narrower and sharper set than "every record in the Application assembly": that set contains
/// <c>AuthenticatedSession</c>, which legitimately carries a session id because the sign-out route ends
/// the caller's own session without naming one, and which reaches no wire at all.
/// </para>
/// <para>
/// <b>It ships a permanent negative control</b>, for the reason <see cref="KeyMaterialSecrecyTests" />
/// gives about its own three: a reflection predicate that silently matches nothing reports an empty
/// offender set, which is indistinguishable from the rule holding. A census that cannot find a token is
/// the failure mode this repository has already met once, so the control grows one and demands the
/// classifier name it — beside deliberately innocent members that it must not.
/// </para>
/// <para>
/// <b>Names are what this can judge, and names are not what a value is.</b> A member called
/// <c>continuation</c> holding a raw handle walks past every rule here, exactly as
/// <c>UnwrappedKeyMaterialVocabulary</c> says a <c>bytea</c> called <c>payload</c> walks past its own. The
/// three route tests beside this one are what close that gap from the other side: each compares the body
/// it received against the cookie value it received, so a handle under any name at all is caught by the
/// payload comparison rather than by a word.
/// </para>
/// </remarks>
public sealed class SessionTokenSecrecyTests
{
    [Test]
    public async Task NoResponse_CarriesARawSessionToken()
    {
        // Arrange — every type a route delegate can hand back, and every owned type reachable through
        // one. Derived rather than listed; see the remarks on the class.
        IReadOnlyList<SurfaceMember> surface = await ResponseSurfaceAsync();

        // Act
        string[] offenders = Offenders(surface);

        // Assert — joined rather than counted, so a failure hands the reviewer the member, what it names
        // and the argument for refusing it in one sentence instead of a number.
        await Assert.That(string.Join(Environment.NewLine, offenders)).IsEqualTo(string.Empty);

        // Non-vacuity, because every assertion above is satisfied by a walk that examined nothing — and
        // a walk that examined nothing is exactly what a changed metadata shape produces. Nothing below
        // is a claim about session handles; each asks whether the census looked at anything at all.
        string[] reached = [.. surface.Select(member => member.Owner).Distinct().Order(StringComparer.Ordinal)];
        Console.WriteLine($"Response surface: {reached.Length} types, {surface.Count} members. "
                          + string.Join(", ", reached));

        await Assert.That(surface.Count).IsGreaterThan(20);

        // The three responses this rule is about, named by hand and by string because each is a private
        // nested record this project cannot write in a typeof. Between them they are every route that
        // establishes a session, so a derivation that stopped reaching endpoint return types reds here
        // rather than going quiet.
        await Assert.That(reached).Contains("PasskeyEndpoints.AssertionResponse");
        await Assert.That(reached).Contains("RecoveryCodeEndpoints.RedemptionResponse");
        await Assert.That(reached).Contains("RecoveryCodeEndpoints.RecoveryCodeGenerationResponse");

        // The recursion's own control: this one is a member type of the response above it, so a walk
        // that stopped at the top level would report green having never looked at the one nested
        // response in the API — which is also the only place a re-established session is described.
        await Assert.That(reached).Contains("RecoveryCodeEndpoints.ReestablishedSessionResponse");
    }

    [Test]
    public async Task TheCensus_ReportsAResponseCarryingARawSessionToken()
    {
        // Arrange — a probe rather than a mutation of a real endpoint, so the control is permanent: a
        // mutation somebody ran once proves only that it worked once. It is nested one level deep, so
        // this one probe exercises the classification and the recursion together.
        IReadOnlyList<SurfaceMember> probe = MembersOf(typeof(ProbeResponse), NameOf(typeof(ProbeResponse)));

        // Act — the same call the census makes, over the same shape.
        string[] offenders = Offenders(probe);

        // Assert — both offences named, and the nested one is the half that proves the recursion is
        // load-bearing rather than the classification alone. The strings are written out as the census
        // prints them, because a report nobody can grep for is the failure this file avoids elsewhere.
        await Assert.That(offenders.Any(offender =>
            offender.StartsWith("SessionTokenSecrecyTests.ProbeResponse.SessionToken", StringComparison.Ordinal)))
            .IsTrue();
        await Assert.That(offenders.Any(offender =>
            offender.StartsWith("SessionTokenSecrecyTests.ProbeNestedResponse.SessionId", StringComparison.Ordinal)))
            .IsTrue();

        // And the deliberately innocent members of the same two records are NOT reported, so the control
        // cannot be passing because the classifier fires on everything. SessionsEnded and Session are the
        // two that matter: both ship on a real response today, and a rule that refused either would red
        // on correct code — which is the shape of failure that teaches a reviewer to stop believing a
        // check.
        await Assert.That(offenders.Any(offender => offender.Contains(".SessionsEnded", StringComparison.Ordinal)))
            .IsFalse();
        await Assert.That(offenders.Any(offender => offender.EndsWith(".Session", StringComparison.Ordinal)))
            .IsFalse();
        await Assert.That(offenders.Any(offender => offender.Contains(".ExpiresAtUtc", StringComparison.Ordinal)))
            .IsFalse();
        await Assert.That(offenders.Any(offender => offender.Contains(".Kind", StringComparison.Ordinal)))
            .IsFalse();
    }

    /// <summary>One member of one type on the response surface.</summary>
    /// <param name="Owner">
    /// The declaring type, nested types qualified by the class that declares them — a private nested
    /// record has no name this project can write in a <c>typeof</c>.
    /// </param>
    /// <param name="Member">The member name, in the casing the CLR reports it.</param>
    private sealed record SurfaceMember(string Owner, string Member);

    /// <summary>One rule a member name can trip, carrying the argument a reviewer has to answer.</summary>
    private sealed record SecrecyRule(string Category, string Reason);

    /// <summary>
    /// Every member of <paramref name="surface" /> a rule refuses, as one sentence each.
    /// </summary>
    /// <remarks>
    /// A pure function over what was walked, so the census and its control run byte-identical
    /// classification over two different shapes. A control that exercised a separately written
    /// classification would prove that one can fail.
    /// </remarks>
    private static string[] Offenders(IReadOnlyList<SurfaceMember> surface) =>
    [
        .. surface
            .Select(member => (member, rule: Classify(member.Member)))
            .Where(candidate => candidate.rule is not null)
            .Select(candidate =>
                $"{candidate.member.Owner}.{candidate.member.Member} "
                + $"— {candidate.rule!.Category}: {candidate.rule.Reason}")
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>
    /// The rule <paramref name="member" /> trips, or <see langword="null" /> when a response may carry it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Judged over the member's words rather than over its characters, so <c>SessionsEnded</c> and
    /// <c>Session</c> — both of which ship on a real response — are not refused for containing the
    /// letters of one that is. Both are the reason the runs below are matched as adjacent whole words
    /// rather than as substrings.
    /// </para>
    /// <para>
    /// <b><c>token</c> is refused wherever it appears, including <c>TokenHash</c>.</b> A digest in a
    /// response body opens nothing, but it is the value a stored row is found by, and a response that
    /// carried one would be handing a client the key to a lookup it has no business making. There is no
    /// legal spelling of it on this surface today, so the rule costs nothing and the first member that
    /// wants an exception can argue for it here.
    /// </para>
    /// </remarks>
    private static SecrecyRule? Classify(string member)
    {
        string[] words = Words(member);

        if (words.Contains("token", StringComparer.Ordinal))
        {
            return new SecrecyRule(
                "a session handle",
                "the cookie is the handle and it is HttpOnly precisely so nothing else is. A token in a "
                + "response body is readable by every script on the page and authenticates exactly as "
                + "well as the cookie, so nothing downstream ever fails");
        }

        if (Runs(words, "session", "id") || Runs(words, "session", "identifier"))
        {
            return new SecrecyRule(
                "a session identifier",
                "returning the row's id hands the client a stable handle to a session, and the likeliest "
                + "way this design is broken later is somebody deciding that handle is close enough to a "
                + "token to start accepting it");
        }

        if (Runs(words, "session", "handle") || Runs(words, "session", "cookie"))
        {
            return new SecrecyRule(
                "a session handle",
                "the handle travels in one place, the cookie, and a second copy of it in a body is a "
                + "second value a client can be persuaded to store, log or forward");
        }

        return null;
    }

    /// <summary>Whether <paramref name="words" /> carries the two given words adjacently, in order.</summary>
    private static bool Runs(string[] words, string first, string second)
    {
        for (int index = 0; index + 1 < words.Length; index++)
        {
            if (string.Equals(words[index], first, StringComparison.Ordinal)
                && string.Equals(words[index + 1], second, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A member name split into lower-case words on camel-case boundaries and separators.
    /// </summary>
    /// <remarks>
    /// Digits end a word as well as a case change, so <c>Token2</c> tokenizes to <c>token</c> rather than
    /// to a word no rule can match.
    /// </remarks>
    private static string[] Words(string member)
    {
        List<string> words = [];
        System.Text.StringBuilder current = new();

        foreach (char character in member)
        {
            if (character is '_' or '-' or '.')
            {
                Flush();
                continue;
            }

            if (char.IsUpper(character) || char.IsDigit(character))
            {
                Flush();
            }

            if (char.IsLetterOrDigit(character))
            {
                current.Append(char.ToLowerInvariant(character));
            }
        }

        Flush();

        return [.. words];

        void Flush()
        {
            if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }
    }

    /// <summary>
    /// Every member of every type reachable from what a route delegate returns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The return type, not the endpoint's nested records.</b> A nested-record sweep is what
    /// <see cref="KeyMaterialSecrecyTests" /> uses, and it is right for that census because a request
    /// record and a response record are equally places key material could sit. Here the direction is the
    /// whole rule: a request member called <c>userHandle</c> is the WebAuthn handle a device returns and
    /// is perfectly legal, while the same word on a response would not be. Taking the delegates' return
    /// types is what separates the two without a naming convention to be written around.
    /// </para>
    /// <para>
    /// The route table is built from the app model over a connection string that resolves to nothing, on
    /// the Production environment, so no database is touched to read it — the arrangement
    /// <c>CompositionBoundaryTests</c> makes for the same reason.
    /// </para>
    /// </remarks>
    private static async Task<IReadOnlyList<SurfaceMember>> ResponseSurfaceAsync()
    {
        await using ApiFactory factory = new(
            "Host=localhost;Port=5432;Database=unused;Username=postgres;Password=postgres",
            environment: "Production");

        EndpointDataSource dataSource = factory.Services.GetRequiredService<EndpointDataSource>();

        List<SurfaceMember> members = [];
        HashSet<Type> visited = [];

        IEnumerable<Type> roots = dataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.Metadata.GetMetadata<MethodInfo>()?.ReturnType)
            .Where(type => type is not null)
            .SelectMany(type => Carried(type!))
            .Where(type => IsOwned(type) && !type.IsEnum && !type.IsGenericTypeDefinition)
            .Distinct();

        foreach (Type root in roots)
        {
            members.AddRange(MembersOf(root, NameOf(root), visited));
        }

        return members;
    }

    /// <summary>
    /// The types a declared return type can be carrying: itself, and everything inside its generic
    /// arguments.
    /// </summary>
    /// <remarks>
    /// Deliberately blind to which wrapper it is unwrapping. A delegate returns
    /// <c>Task&lt;Ok&lt;T&gt;&gt;</c> today and could return <c>Results&lt;Ok&lt;T&gt;, NotFound&gt;</c>,
    /// a <c>ValueTask</c>, or a bare <c>T</c> tomorrow; naming the wrappers would make the census go quiet
    /// on the first shape nobody listed, which is a green that means nothing.
    /// </remarks>
    private static IEnumerable<Type> Carried(Type type)
    {
        yield return type;

        if (!type.IsGenericType)
        {
            yield break;
        }

        foreach (Type argument in type.GetGenericArguments())
        {
            foreach (Type carried in Carried(argument))
            {
                yield return carried;
            }
        }
    }

    /// <summary>
    /// Every member of <paramref name="type" /> and of every owned type reachable through its members.
    /// </summary>
    /// <remarks>
    /// The visited set makes the walk terminate on a self-referential shape and means each type is
    /// reported once however many members point at it. Enums are skipped because their members are values
    /// rather than places a handle can sit, and the member that names one is classified anyway.
    /// </remarks>
    private static IReadOnlyList<SurfaceMember> MembersOf(
        Type type,
        string name,
        HashSet<Type>? visited = null)
    {
        visited ??= [];

        if (!visited.Add(type))
        {
            return [];
        }

        List<SurfaceMember> members = [];

        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            members.Add(new SurfaceMember(name, property.Name));

            foreach (Type payload in PayloadTypes(property.PropertyType))
            {
                if (IsOwned(payload) && !payload.IsEnum && !payload.IsGenericTypeDefinition)
                {
                    members.AddRange(MembersOf(payload, NameOf(payload), visited));
                }
            }
        }

        return members;
    }

    /// <summary>
    /// The types a member could carry a value of: the type itself, what a nullable wraps, what a sequence
    /// yields, and any generic argument.
    /// </summary>
    private static IEnumerable<Type> PayloadTypes(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            yield return underlying;
            yield break;
        }

        if (type == typeof(string))
        {
            yield break;
        }

        yield return type;

        if (type.IsArray && type.GetElementType() is { } element)
        {
            yield return element;
        }

        if (!type.IsGenericType)
        {
            yield break;
        }

        foreach (Type argument in type.GetGenericArguments())
        {
            yield return argument;
        }
    }

    /// <summary>
    /// Whether the type was declared by this solution, as opposed to the framework.
    /// </summary>
    /// <remarks>
    /// Derived from anchor types rather than from assembly-name strings, so a rename is a compile error
    /// rather than a census that walks nothing. This assembly is an anchor so the control's probe is
    /// recursed into; it widens the census by nothing, since no type the Api declares can reference one
    /// declared in a test project.
    /// </remarks>
    private static bool IsOwned(Type type) =>
        type.Assembly == typeof(TransactionEndpoints).Assembly
        || type.Assembly == typeof(Optional<>).Assembly
        || type.Assembly == typeof(IAccountRepository).Assembly
        || type.Assembly == typeof(SessionTokenSecrecyTests).Assembly;

    /// <summary>A type's name for reporting, a nested one qualified by the class that declares it.</summary>
    private static string NameOf(Type type) =>
        type.DeclaringType is { } declaring ? $"{declaring.Name}.{type.Name}" : type.Name;

    /// <summary>
    /// The control's offending shape: two members that must be refused and four beside them that must not.
    /// </summary>
    /// <remarks>
    /// The innocent four are not decoration. <c>SessionsEnded</c> and <c>Session</c> are members of a
    /// response that ships today, and a rule refusing either would red on correct code; <c>Kind</c> and
    /// <c>ExpiresAtUtc</c> are what every establishing response is allowed to say.
    /// </remarks>
    private sealed record ProbeResponse(
        string SessionToken,
        int SessionsEnded,
        string Kind,
        ProbeNestedResponse? Session);

    /// <summary>The nested half of the probe. See <see cref="ProbeResponse" />.</summary>
    private sealed record ProbeNestedResponse(Guid SessionId, DateTime ExpiresAtUtc);
}
