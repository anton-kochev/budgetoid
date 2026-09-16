using System.Text.Json.Nodes;

namespace IntegrationTests;

/// <summary>
/// The generation an account's factor manifest is at, asked of the running API the way a client asks —
/// and the successor a request changing that account's factor set has to carry.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read rather than counted, and that is what makes it safe to call from a helper that does not know
/// how many times it has run.</b> Both routes that change a factor set refuse any epoch that is not
/// exactly one greater than the stored one, so a test registering a second passkey has to send a
/// different number from the first. A helper carrying a literal would be correct for whichever call
/// site it was written beside and silently wrong for the next one — and wrong in the shape that looks
/// like the feature is broken, because the refusal is a 400 naming <c>rotationEpoch</c>.
/// </para>
/// <para>
/// <b>It is the product's own flow rather than a shortcut around it.</b> A client reads
/// <c>GET /api/me/account-keys</c>, seals a manifest over the generation it reports plus one, and posts
/// both — which is exactly the remedy the <c>factor_set_moved</c> conflict asks of a caller that lost
/// the race. Going to the database instead would make every arrangement in the suite depend on a
/// connection string the helpers holding an <see cref="HttpClient" /> do not have, and would let a test
/// pass over an <c>account-keys</c> read that had stopped reporting the generation at all.
/// </para>
/// <para>
/// <b>Zero is a legal answer and is not an error.</b> An account with no manifest row reports epoch 0
/// — that is the pre-registration state, and the read says so rather than answering 404. The successor
/// this hands back is then 1, which the routes carry on to refuse with a fault, because an account
/// without a manifest is an integrity failure rather than a request anybody can correct. That is the
/// behaviour one test in the suite is about; nothing here smooths it over.
/// </para>
/// <para>
/// <b>What it cannot be used for.</b> It needs a client already carrying a full session, because
/// <c>/api/me/account-keys</c> rides the same fallback policy the two write routes do. A test measuring
/// what an anonymous or locked-session caller gets from those routes never reaches the manifest at all,
/// so it needs no epoch and must not call this.
/// </para>
/// </remarks>
internal static class FactorGeneration
{
    /// <summary>The read that reports the account's current generation.</summary>
    private const string AccountKeysPath = "/api/me/account-keys";

    /// <summary>
    /// The generation the account's manifest is at right now — <c>0</c> when it holds no manifest row.
    /// </summary>
    public static async Task<int> CurrentAsync(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        HttpResponseMessage response = await client.GetAsync(AccountKeysPath);
        response.EnsureSuccessStatusCode();

        JsonNode body = (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!;

        return body["rotationEpoch"]!.GetValue<int>();
    }

    /// <summary>
    /// The generation a request changing this account's factor set has to carry: one greater than the
    /// one the account reports.
    /// </summary>
    public static async Task<int> NextAsync(HttpClient client) => await CurrentAsync(client) + 1;
}
