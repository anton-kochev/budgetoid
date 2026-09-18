using System.Text.Json;

namespace IntegrationTests;

/// <summary>
/// <c>docs/business-logic/vectors/account-keys-wire-v1.json</c>, parsed rather than trusted — the one
/// file the browser's suite and this one both read.
/// </summary>
/// <remarks>
/// <para>
/// <b>It sits one level above the solution, and a run that cannot find it throws rather than skips.</b>
/// The rule is <see cref="ClientKeyCustodyTests" />'s and the reason is the same: "the test ran from a
/// published output with no source tree" must never read as a pass. Deleting the artifact fails four
/// client spec files loudly, and it fails every reader here the same way.
/// </para>
/// <para>
/// <b>It is a file of its own rather than a private class inside one test, because it has two readers
/// and the second one is the point.</b> <see cref="AccountKeyWireContractTests" /> binds the server's
/// <em>records</em> to these member sets by reflection; <see cref="AccountKeysEndpointTests" /> binds
/// the <em>bytes a real host emitted</em> to the same sets. Each was previously satisfied by an
/// expectation living in its own file, which is the arrangement the artifact exists to end: an
/// expectation a suite owns can be greened by pasting the actual over it, and the paste reads in review
/// as a test being updated beside its code. Read from here, the same paste has to move a file the
/// browser reads too.
/// </para>
/// <para>
/// Parsed once into a <see cref="JsonDocument" /> held for the life of the process. Nothing mutates it
/// and every reader sees the same bytes, so a copy per caller would buy isolation from nothing.
/// </para>
/// </remarks>
internal static class WireContract
{
    private const string RelativePath = "docs/business-logic/vectors/account-keys-wire-v1.json";

    private static readonly JsonDocument Document =
        JsonDocument.Parse(File.ReadAllText(LocateFrom(AppContext.BaseDirectory)));

    /// <summary>
    /// The absolute path of the artifact, walking up from <paramref name="startDirectory" />.
    /// </summary>
    /// <exception cref="InvalidOperationException">No ancestor holds it.</exception>
    public static string LocateFrom(string startDirectory)
    {
        string relative = RelativePath.Replace('/', Path.DirectorySeparatorChar);

        for (DirectoryInfo? directory = new(startDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, relative);

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"No ancestor of '{startDirectory}' holds {RelativePath}. It is the only thing binding "
            + "this server's wire records to the browser that reads them; a run that cannot read it "
            + "has bound nothing, and it must not be mistaken for a run that agreed with it.");
    }

    /// <summary>
    /// The whole member set of <paramref name="message" />, refusing blanks and repeats.
    /// </summary>
    /// <remarks>
    /// <b>A repeat is refused rather than absorbed.</b> Every comparison over one of these lists is a
    /// set comparison, and a set absorbs a repeat — so a list naming <c>factorId</c> twice would compare
    /// equal to one naming it once, and the seven-member message driven from it would look like eight.
    /// </remarks>
    public static IReadOnlyList<string> Members(string message)
    {
        JsonElement entry = Child(Child(Document.RootElement, "messages"), message);

        if (!entry.TryGetProperty("members", out JsonElement listed)
            || listed.ValueKind != JsonValueKind.Array
            || listed.GetArrayLength() == 0)
        {
            throw new InvalidOperationException(
                $"The wire contract's '{message}' lists no members.");
        }

        List<string> members = [];

        foreach (JsonElement member in listed.EnumerateArray())
        {
            if (member.GetString() is not { Length: > 0 } name)
            {
                throw new InvalidOperationException(
                    $"The wire contract's '{message}' lists a blank member.");
            }

            members.Add(name);
        }

        return members.Distinct(StringComparer.Ordinal).Count() == members.Count
            ? members
            : throw new InvalidOperationException(
                $"The wire contract's '{message}' names a member twice.");
    }

    /// <summary>The integer at <paramref name="property" /> of a named width.</summary>
    public static int Width(string member, string property)
    {
        JsonElement entry = Child(Child(Document.RootElement, "widths"), member);

        return entry.TryGetProperty(property, out JsonElement value)
               && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : throw new InvalidOperationException(
                $"The wire contract's '{member}' width holds no number at '{property}'.");
    }

    /// <summary>The messages the artifact carries.</summary>
    public static IReadOnlyList<string> MessageNames() =>
        NamesUnder(Child(Document.RootElement, "messages"));

    /// <summary>The widths the artifact carries.</summary>
    public static IReadOnlyList<string> WidthNames() =>
        NamesUnder(Child(Document.RootElement, "widths"));

    /// <summary>
    /// The property names of <paramref name="owner" /> that are not prose.
    /// </summary>
    /// <remarks>
    /// The artifact carries its own argument inline under keys opening with an underscore — the
    /// convention <c>factor-keypair-v1.json</c> already uses — and those are commentary rather than
    /// entries. The filter is on the leading character rather than on a list of known prose keys, so a
    /// new paragraph does not redden a census about members.
    /// </remarks>
    private static IReadOnlyList<string> NamesUnder(JsonElement owner) =>
        [
            .. owner.EnumerateObject()
                .Select(property => property.Name)
                .Where(name => !name.StartsWith('_')),
        ];

    /// <summary>The object at <paramref name="property" /> of <paramref name="owner" />.</summary>
    /// <remarks>
    /// In place of <c>GetProperty(...)</c>, whose <see cref="KeyNotFoundException" /> names the key and
    /// says nothing about what the file is. The whole argument for reading a shared artifact rather than
    /// restating it is that the other suite reads the same one; the matching refusal is one that says
    /// which part of it went missing.
    /// </remarks>
    private static JsonElement Child(JsonElement owner, string property) =>
        owner.TryGetProperty(property, out JsonElement value)
        && value.ValueKind == JsonValueKind.Object
            ? value
            : throw new InvalidOperationException(
                $"The wire contract carries no object at '{property}'. It is read by this suite and "
                + "by the browser's; a case that cannot find its half has bound nothing.");
}
