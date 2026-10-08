using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace UnitTests;

/// <summary>
/// Exactly one production file writes the session cookie through <c>SessionCookie.Issue</c>, and it is
/// not an endpoint — it is the writer that displaces the session the incoming cookie named before it
/// writes the new one.
/// </summary>
/// <remarks>
/// <para>
/// <b>The gap this closes.</b> Five endpoints establish a session and each used to write the cookie
/// itself. Writing a new cookie overwrites the one the browser held, and the session that cookie named
/// stays live with no browser holding it unless something deletes it. Displacement lives in one writer
/// so that every establishing path gets it. A sixth path calling <c>SessionCookie.Issue</c> directly
/// would compile, answer 200 and set a working cookie — and leave the overwritten session live. No
/// endpoint test of the other five would notice. This census is what does: an endpoint calling
/// <c>SessionCookie.Issue</c> directly fails it, measured.
/// </para>
/// <para>
/// <b>Why it reads source text.</b> Reflection cannot see a call site. The shape follows
/// <c>ConflictKindDispositionCensusTests</c>: walk up to <c>BudgetoidApp.sln</c>, scan every project
/// that does not declare <c>&lt;IsTestProject&gt;true&lt;/IsTestProject&gt;</c>, skip build output.
/// The walker is re-derived here rather than shared, for the reason that census gives.
/// </para>
/// <para>
/// <b>The writer is not named, on purpose.</b> The rule is "one writer, outside the endpoints", not a
/// path. A file name pinned here would be a second thing to edit on a rename and would say nothing a
/// reader of the writer cannot see.
/// </para>
/// <para>
/// <b>What it is blind to.</b> A cookie appended by name through <c>Response.Cookies.Append</c>, which
/// never touches <c>SessionCookie.Issue</c> — <c>CookieCensusTests</c> sees the name on the wire but not
/// who wrote it. A writer that calls <c>Issue</c> and displaces nothing. A writer placed inside
/// <c>SessionCookie</c> itself and calling <c>Issue</c> unqualified, which this scan reports as zero
/// writers and so fails closed. A <c>using static</c> import of the cookie type counts as a reference,
/// for the same reason: it is the one way to call <c>Issue</c> without the type name in front.
/// </para>
/// </remarks>
public sealed partial class SessionCookieIssueCensusTests
{
    private const string SolutionFileName = "BudgetoidApp.sln";

    /// <summary>The directory every endpoint file lives in, relative to the solution root.</summary>
    private const string EndpointsDirectory = "Api/Endpoints/";

    [Test]
    public async Task Production_IssuesTheSessionCookieFromOneWriterOutsideTheEndpoints()
    {
        // Arrange
        DirectoryInfo root = SolutionRootFrom(AppContext.BaseDirectory);

        // Act
        string[] writers =
        [
            .. ProductionSourcesUnder(root)
                .Where(path => IssueReferences.CountIn(File.ReadAllText(path)) > 0)
                .Select(path => Path.GetRelativePath(root.FullName, path).Replace(Path.DirectorySeparatorChar, '/'))
                .Order(StringComparer.Ordinal),
        ];
        string[] endpointWriters =
            [.. writers.Where(path => path.StartsWith(EndpointsDirectory, StringComparison.Ordinal))];

        // Assert — no endpoint writes the cookie itself, and exactly one file does. The endpoint list
        // comes first because it names the offenders; the count alone would say only "four".
        await Assert.That(endpointWriters).IsEmpty();
        await Assert.That(writers.Length).IsEqualTo(1);
    }

    [Test]
    public async Task Scan_IgnoresAReferenceInAComment()
    {
        // Arrange — the three comment shapes the production tree already uses around this method.
        const string source = """
            /// <see cref="SessionCookie.Issue"/> is called after the handler returns.
            // SessionCookie.Issue(response, token, expiry);
            /* SessionCookie.Issue */
            """;

        // Act
        int references = IssueReferences.CountIn(source);

        // Assert
        await Assert.That(references).IsEqualTo(0);
    }

    [Test]
    public async Task Scan_CountsAQualifiedCallAMethodGroupAndAStaticImport()
    {
        // Arrange — three ways to reach Issue from code. The import is counted because it is the one way
        // to call Issue with nothing in front of it, which this scan would otherwise miss.
        const string source = """
            using static Api.Infrastructure.SessionCookie;
            Api.Infrastructure.SessionCookie.Issue(response, handoff.Token, handoff.ExpiresAtUtc);
            Action<HttpResponse, string, DateTime> write = SessionCookie . Issue;
            """;

        // Act
        int references = IssueReferences.CountIn(source);

        // Assert
        await Assert.That(references).IsEqualTo(3);
    }

    [Test]
    public async Task Scan_KeepsReadingCodeAfterAStringHoldingACommentMarker()
    {
        // Arrange — a "//" inside a string is not a comment. A scanner that took it for one would blank
        // the rest of the line and lose the call after it.
        const string source = """
            string origin = "https://example.test"; SessionCookie.Issue(response, value, expiry);
            """;

        // Act
        int references = IssueReferences.CountIn(source);

        // Assert
        await Assert.That(references).IsEqualTo(1);
    }

    [Test]
    public async Task SolutionRootFrom_WithNoSolutionAbove_Throws()
    {
        // Act, Assert — a walker that quietly returned nothing would hand the census an empty tree, and
        // "no endpoint writes the cookie" would pass on reading no file at all.
        await Assert.That(() => SolutionRootFrom(Path.GetTempPath())).Throws<InvalidOperationException>();
    }

    /// <summary>
    /// Walks up to the directory holding the solution, and throws rather than returning nothing.
    /// </summary>
    private static DirectoryInfo SolutionRootFrom(string startDirectory)
    {
        for (DirectoryInfo? directory = new(startDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
            {
                return directory;
            }
        }

        throw new InvalidOperationException(
            $"No ancestor of '{startDirectory}' holds {SolutionFileName}. This census reads the product's "
            + "source from disk; it cannot run from a published output that carries no source tree.");
    }

    /// <summary>The <c>.cs</c> files of every project that does not declare itself a test project.</summary>
    private static IReadOnlyList<string> ProductionSourcesUnder(DirectoryInfo root)
    {
        List<string> sources = [];

        foreach (string project in Directory.EnumerateFiles(root.FullName, "*.csproj", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(root, project) || DeclaresTestProject(XDocument.Load(project)))
            {
                continue;
            }

            string directory = Path.GetDirectoryName(project)
                ?? throw new InvalidOperationException($"'{project}' has no directory.");

            sources.AddRange(
                Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
                    .Where(path => !IsBuildOutput(root, path)));
        }

        return [.. sources.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    private static bool DeclaresTestProject(XDocument project) =>
        project.Descendants().Any(element =>
            element.Name.LocalName == "IsTestProject"
            && string.Equals(element.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase));

    private static bool IsBuildOutput(DirectoryInfo root, string path) =>
        Path.GetRelativePath(root.FullName, path)
            .Split(Path.DirectorySeparatorChar)
            .Any(segment => segment is "bin" or "obj");

    /// <summary>
    /// Counts the code references to <c>SessionCookie.Issue</c> in one file, comments excluded.
    /// </summary>
    /// <remarks>
    /// Only comments are blanked. Strings and char literals are walked so that a <c>//</c> inside one is
    /// not read as a comment, but they are kept: a string naming the method would be counted, which is
    /// the safe direction for a census — a row too many, never one too few.
    /// </remarks>
    private static partial class IssueReferences
    {
        internal static int CountIn(string source)
        {
            ArgumentNullException.ThrowIfNull(source);

            string code = WithoutComments(source);

            return QualifiedReference().Count(code) + StaticImport().Count(code);
        }

        [GeneratedRegex(@"\bSessionCookie\s*\.\s*Issue\b", RegexOptions.CultureInvariant)]
        private static partial Regex QualifiedReference();

        [GeneratedRegex(@"\busing\s+static\s+(?:[\w.]+\.)?SessionCookie\s*;", RegexOptions.CultureInvariant)]
        private static partial Regex StaticImport();

        private static string WithoutComments(string source)
        {
            char[] buffer = new char[source.Length];
            Array.Fill(buffer, ' ');
            int index = 0;

            while (index < source.Length)
            {
                char current = source[index];
                char next = index + 1 < source.Length ? source[index + 1] : '\0';

                if (current == '/' && next == '/')
                {
                    while (index < source.Length && source[index] != '\n')
                    {
                        index++;
                    }

                    continue;
                }

                if (current == '/' && next == '*')
                {
                    int close = source.IndexOf("*/", index + 2, StringComparison.Ordinal);
                    index = close < 0 ? source.Length : close + 2;
                    continue;
                }

                int end = current switch
                {
                    '"' => EndOfString(source, index),
                    '\'' => EndOfCharLiteral(source, index),
                    _ => index + 1,
                };

                source.AsSpan(index, end - index).CopyTo(buffer.AsSpan(index));
                index = end;
            }

            return new string(buffer);
        }

        /// <summary>The index just past the string opening at <paramref name="start" />.</summary>
        private static int EndOfString(string source, int start)
        {
            int quotes = 0;
            while (start + quotes < source.Length && source[start + quotes] == '"')
            {
                quotes++;
            }

            if (quotes >= 3)
            {
                string fence = new('"', quotes);
                int close = source.IndexOf(fence, start + quotes, StringComparison.Ordinal);
                return close < 0 ? source.Length : close + quotes;
            }

            bool verbatim = LeadsWithAt(source, start);
            int index = start + 1;

            while (index < source.Length)
            {
                char current = source[index];

                if (!verbatim && current == '\\')
                {
                    index += 2;
                    continue;
                }

                if (current == '"')
                {
                    if (verbatim && index + 1 < source.Length && source[index + 1] == '"')
                    {
                        index += 2;
                        continue;
                    }

                    return index + 1;
                }

                if (!verbatim && current == '\n')
                {
                    return index;
                }

                index++;
            }

            return source.Length;
        }

        private static bool LeadsWithAt(string source, int quote)
        {
            for (int index = quote - 1; index >= 0 && source[index] is '$' or '@'; index--)
            {
                if (source[index] == '@')
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>The index just past a char literal opening at <paramref name="start" />.</summary>
        private static int EndOfCharLiteral(string source, int start)
        {
            int index = start + 1;
            index += index < source.Length && source[index] == '\\' ? 2 : 1;

            while (index < source.Length && source[index] != '\'' && source[index] != '\n')
            {
                index++;
            }

            return Math.Min(index + 1, source.Length);
        }
    }
}
