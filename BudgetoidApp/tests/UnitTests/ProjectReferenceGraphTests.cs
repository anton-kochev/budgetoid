using System.Xml.Linq;

namespace UnitTests;

public sealed class ProjectReferenceGraphTests
{
    [Test]
    public async Task EdgesOf_RendersTheSdkAttributeOfAProjectDeclaringNothingElse()
    {
        // Arrange
        XDocument document = XDocument.Parse("""<Project Sdk="Microsoft.NET.Sdk" />""");

        // Act
        IReadOnlyList<string> edges = ProjectGraph.EdgesOf("Domain", document);

        // Assert
        await Assert.That(edges).IsEquivalentTo(new[] { "Domain: sdk Microsoft.NET.Sdk" });
    }

    [Test]
    public async Task EdgesOf_RendersEveryDependencyKindAndDropsTheVersion()
    {
        // Arrange — one of each kind the guard recognises. The versions are the point: they are
        // what the renderer must throw away.
        XDocument document = XDocument.Parse(
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="..\Domain\Domain.csproj" />
                <PackageReference Include="System.Formats.Cbor" Version="10.0.9" />
                <FrameworkReference Include="Microsoft.AspNetCore.App" />
              </ItemGroup>
            </Project>
            """);

        // Act
        IReadOnlyList<string> edges = ProjectGraph.EdgesOf("Application", document);

        // Assert — a ProjectReference renders as the referenced project's file stem, not its path,
        // so moving a folder does not move a line but changing what it references does.
        await Assert.That(edges).IsEquivalentTo(new[]
        {
            "Application: sdk Microsoft.NET.Sdk",
            "Application: project Domain",
            "Application: package System.Formats.Cbor",
            "Application: framework Microsoft.AspNetCore.App",
        });
    }

    [Test]
    public async Task EdgesOf_StripsTheVersionFromAVersionedSdkAttribute()
    {
        // Arrange — AppHost is the one project whose Sdk attribute carries a version.
        XDocument document = XDocument.Parse("""<Project Sdk="Aspire.AppHost.Sdk/13.4.6" />""");

        // Act
        IReadOnlyList<string> edges = ProjectGraph.EdgesOf("AppHost", document);

        // Assert — same argument as package versions: a version bump moves no boundary, and a
        // guard that reddens on routine bumps trains the reader to update it without looking.
        await Assert.That(edges).IsEquivalentTo(new[] { "AppHost: sdk Aspire.AppHost.Sdk" });
    }

    [Test]
    public async Task SolutionRootFrom_FindsTheDirectoryHoldingTheSolution()
    {
        // Act
        DirectoryInfo root = ProjectGraph.SolutionRootFrom(AppContext.BaseDirectory);

        // Assert — the root is defined as "the directory CI builds from": ci.yml runs
        // `dotnet build BudgetoidApp.sln` with working-directory BudgetoidApp, so the thing holding
        // the solution and the thing under test are the same object by construction.
        await Assert.That(File.Exists(Path.Combine(root.FullName, "BudgetoidApp.sln"))).IsTrue();
    }

    [Test]
    public async Task SolutionRootFrom_ThrowsWhenNoAncestorHoldsTheSolution()
    {
        // Act & Assert — a walker that quietly returned null would hand the scan an empty directory
        // and the whole guard would pass while reading nothing at all.
        await Assert.That(() => ProjectGraph.SolutionRootFrom(Path.GetTempPath()))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task ProjectGraph_PinsEveryDeclaredDependency()
    {
        // Arrange
        IReadOnlyList<string> edges = ProjectGraph.EdgesOfEveryProjectUnder(
            ProjectGraph.SolutionRootFrom(AppContext.BaseDirectory));

        // Act — the two directions are separated rather than compared with IsEquivalentTo, which
        // reports only "collection has 60 items but expected 59" after dumping all fifty-nine rows.
        // A guard whose failure does not name the line that moved sends the reader to diff two
        // screenfuls by eye, and that is how the wrong row gets "fixed".
        string[] undeclared = [.. edges.Except(ExpectedEdges).Order(StringComparer.Ordinal)];
        string[] missing = [.. ExpectedEdges.Except(edges).Order(StringComparer.Ordinal)];

        // Assert — joined rather than counted so a failure names the offending edge.
        await Assert.That(string.Join(", ", undeclared)).IsEqualTo(string.Empty);
        await Assert.That(string.Join(", ", missing)).IsEqualTo(string.Empty);

        // Set difference is blind to a row declared twice, so the count is what catches a duplicate
        // PackageReference that both directions above would call an exact match.
        await Assert.That(edges.Count).IsEqualTo(ExpectedEdges.Length);

        // Three controls against the failure the comparison cannot see: a renderer that came back
        // with Sdk rows alone (an XML-namespace bug, say) would agree with an expected array
        // transcribed from that same broken output, and the pin would stay green while policing
        // nothing. Every kind must be represented before any of the above means anything.
        await Assert.That(edges.Count(edge => edge.Contains(": project ", StringComparison.Ordinal))).IsGreaterThan(0);
        await Assert.That(edges.Count(edge => edge.Contains(": package ", StringComparison.Ordinal))).IsGreaterThan(0);
        await Assert.That(edges.Count(edge => edge.Contains(": framework ", StringComparison.Ordinal))).IsGreaterThan(0);
    }

    [Test]
    public async Task ProjectGraph_ReportsAnEdgeTheGraphDoesNotAllow()
    {
        // Arrange — the violation this whole guard exists to catch, on a synthetic document so the
        // proof is permanent rather than a sentence in a commit message about a change that was
        // reverted. Application reaching EF Core directly is the Dependency Rule breaking inward.
        XDocument document = XDocument.Parse(
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Microsoft.EntityFrameworkCore" Version="10.0.9" />
              </ItemGroup>
            </Project>
            """);

        // Act
        IReadOnlyList<string> edges = ProjectGraph.EdgesOf("Application", document);

        // Assert — the renderer names it, and the pinned set does not contain it, so the comparison
        // in the test above would fail. Both halves are needed: a renderer that named it while the
        // pin happened to allow it would prove nothing.
        await Assert.That(edges).Contains("Application: package Microsoft.EntityFrameworkCore");
        await Assert.That(ExpectedEdges).DoesNotContain("Application: package Microsoft.EntityFrameworkCore");
    }

    [Test]
    public async Task EdgesOf_RendersTheDependencyFormsThatCarryNoPackageAndNoProject()
    {
        // Arrange — four ways to hand a project code it did not write, none of which is a
        // ProjectReference or a PackageReference. Each of these was a silent pass before a review
        // went looking for them, and each is here so it cannot become one again.
        XDocument document = XDocument.Parse(
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <Sdk Name="Microsoft.NET.Sdk.Web" />
              <Import Project="..\Shared.props" />
              <ItemGroup>
                <Reference Include="Microsoft.EntityFrameworkCore">
                  <HintPath>..\packages\ef\lib\net10.0\Microsoft.EntityFrameworkCore.dll</HintPath>
                </Reference>
              </ItemGroup>
            </Project>
            """);

        // Act
        IReadOnlyList<string> edges = ProjectGraph.EdgesOf("Domain", document);

        // Assert — the element form of Sdk resolves the same types as the attribute while leaving
        // the attribute untouched; a raw Reference is a real dependency with no package to name;
        // and an Import can carry any of them, so it is reported without being followed.
        await Assert.That(edges).IsEquivalentTo(new[]
        {
            "Domain: sdk Microsoft.NET.Sdk",
            "Domain: sdk Microsoft.NET.Sdk.Web",
            "Domain: import ..\\Shared.props",
            "Domain: assembly Microsoft.EntityFrameworkCore",
        });
    }

    [Test]
    public async Task ProjectGraph_ScansTheImportedBuildFilesAndNotOnlyTheProjects()
    {
        // Arrange
        DirectoryInfo root = ProjectGraph.SolutionRootFrom(AppContext.BaseDirectory);

        // Act — Directory.Build.props is imported into every project in the tree, Domain included.
        // A single PackageReference there compiled Domain against EF Core with every csproj in the
        // solution untouched, and a scan of "*.csproj" reported nothing at all.
        string[] scanned = ProjectGraph.BuildFileNamesUnder(root);

        // Assert — the file is read whether or not it declares anything today, because the point is
        // that it *would* be read on the day someone adds a line to it.
        await Assert.That(scanned).Contains("Directory.Build.props");
        await Assert.That(scanned.Count(name => name.EndsWith(".csproj", StringComparison.Ordinal)))
            .IsGreaterThan(0);
    }

    /// <summary>
    /// The whole declared dependency graph of the solution, one row per edge.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rows rather than a per-project block, following the flat snapshot idiom of
    /// <c>SchemaConstraintSnapshotTests</c> — but compared as two one-way set differences rather
    /// than with <c>IsEquivalentTo</c>, which reports "collection has 60 items but expected 59"
    /// after dumping all fifty-nine rows and names nothing. A failure has to name the line that
    /// moved, or the reader diffs two screenfuls by eye and "fixes" the wrong one.
    /// </para>
    /// <para>
    /// <b>Package versions are deliberately absent.</b> The subject here is which project may know
    /// about what, and a version bump moves no boundary. Including versions would redden this test
    /// on every dependency-update PR, and that reflex — update the array, re-run, move on — is
    /// exactly what would wave a genuinely new package id through in the same edit. A guard whose
    /// failures are routine has already stopped working. The <c>Microsoft.OpenApi</c> pin in
    /// Api.csproj is a security floor whose version does matter; it is not this test's job, and the
    /// comment on that line plus <c>dotnet list package --vulnerable</c> are where it lives.
    /// </para>
    /// <para>
    /// <b>Do not add a filter to the scan.</b> The subject is discovered and the allowance is
    /// written down, never the other way around — a new project fails this test by existing, which
    /// is the only reason it can be trusted to notice one. <c>RlsCoverageTests</c> lost
    /// <c>credentials</c> to exactly such a filter.
    /// </para>
    /// <para>
    /// Sabotaged twice before it was believed: a <c>Microsoft.EntityFrameworkCore</c> package on
    /// Application, and a <c>FrameworkReference</c> on Domain. Each moved exactly one named row.
    /// </para>
    /// </remarks>
    private static readonly string[] ExpectedEdges =
    [
        "Api: sdk Microsoft.NET.Sdk.Web",
        "Api: project Application",
        "Api: project Infrastructure",
        "Api: project ServiceDefaults",
        "Api: package Aspire.Azure.Npgsql.EntityFrameworkCore.PostgreSQL",
        "Api: package Microsoft.AspNetCore.Authentication.JwtBearer",
        "Api: package Microsoft.AspNetCore.OpenApi",
        "Api: package Microsoft.OpenApi",
        "Api: package Microsoft.EntityFrameworkCore.Design",
        "Api: package Microsoft.EntityFrameworkCore.Relational",
        "Api: package Microsoft.CodeAnalysis.BannedApiAnalyzers",

        "AppHost: sdk Aspire.AppHost.Sdk",
        "AppHost: project Api",
        "AppHost: package Aspire.Hosting.AppHost",
        "AppHost: package Aspire.Hosting.Azure.PostgreSQL",
        "AppHost: package Aspire.Hosting.PostgreSQL",
        "AppHost: package Aspire.Hosting.Azure.AppContainers",
        "AppHost: package Azure.Provisioning.Network",
        "AppHost: package Azure.Provisioning.PrivateDns",

        // Two packages, neither of them data access, and one project edge pointing inward. This is
        // the Dependency Rule's middle ring stated as data.
        "Application: sdk Microsoft.NET.Sdk",
        "Application: project Domain",
        "Application: package Microsoft.Extensions.DependencyInjection.Abstractions",
        "Application: package System.Formats.Cbor",

        "DbProvision: sdk Microsoft.NET.Sdk",
        "DbProvision: project Infrastructure",
        "DbProvision: package Microsoft.EntityFrameworkCore.Relational",

        // One row, and the row IS the invariant: Domain declares no reference of any kind. Every
        // project emits an Sdk edge precisely so this can be a line that must stay alone rather
        // than an absence nothing asserts.
        "Domain: sdk Microsoft.NET.Sdk",

        "Infrastructure: sdk Microsoft.NET.Sdk",
        "Infrastructure: project Application",
        "Infrastructure: package Microsoft.EntityFrameworkCore",
        "Infrastructure: package Microsoft.EntityFrameworkCore.Design",
        "Infrastructure: package Npgsql.EntityFrameworkCore.PostgreSQL",
        "Infrastructure: package Microsoft.CodeAnalysis.BannedApiAnalyzers",

        "IntegrationTests: sdk Microsoft.NET.Sdk",
        "IntegrationTests: project Api",
        "IntegrationTests: project Infrastructure",
        "IntegrationTests: project TestSupport",
        "IntegrationTests: package TUnit",
        "IntegrationTests: package Microsoft.Extensions.TimeProvider.Testing",
        "IntegrationTests: package Microsoft.AspNetCore.Mvc.Testing",
        "IntegrationTests: package Testcontainers.PostgreSql",
        "IntegrationTests: package Npgsql",

        // The only FrameworkReference in the solution, and it belongs here and only here. That one
        // line pulls the entire ASP.NET Core shared framework with no PackageReference to notice;
        // copied onto Application or Domain it would hand them HttpContext and hosting while a pin
        // reading only ProjectReference and PackageReference stayed green.
        "ServiceDefaults: sdk Microsoft.NET.Sdk",
        "ServiceDefaults: framework Microsoft.AspNetCore.App",
        "ServiceDefaults: package OpenTelemetry.Exporter.OpenTelemetryProtocol",
        "ServiceDefaults: package OpenTelemetry.Extensions.Hosting",

        "TestSupport: sdk Microsoft.NET.Sdk",
        "TestSupport: project Application",
        "TestSupport: project Domain",
        "TestSupport: project Infrastructure",
        "TestSupport: package System.Formats.Cbor",
        "TestSupport: package Microsoft.EntityFrameworkCore.Relational",

        // Deliberately no "UnitTests: project Api". UnitTests.csproj carries the argument — Api
        // targets the Web SDK, so referencing it would pull the whole web composition root into the
        // one project whose value is being free of it — and this array is the first thing in the
        // repository that makes that argument FAIL rather than merely state it.
        "UnitTests: sdk Microsoft.NET.Sdk",
        "UnitTests: project Application",
        "UnitTests: project Infrastructure",
        "UnitTests: project TestSupport",
        "UnitTests: package Microsoft.Extensions.TimeProvider.Testing",
        "UnitTests: package Microsoft.EntityFrameworkCore.Relational",
        "UnitTests: package TUnit",
    ];

    private static class ProjectGraph
    {
        private const string SolutionFileName = "BudgetoidApp.sln";

        /// <summary>
        /// The item types that hand a project code it did not write, and the word each renders as.
        /// </summary>
        /// <remarks>
        /// <c>Reference</c> is here because a raw assembly path with a <c>HintPath</c> is a real
        /// dependency that carries no package and no project, and a switch that knew only the three
        /// common item types let it through in silence. The rest are the forms NuGet and MSBuild
        /// accept for the same job; listing them is cheaper than discovering one at a time which
        /// spelling the next person reached for.
        /// </remarks>
        private static readonly Dictionary<string, string> DependencyKinds = new(StringComparer.Ordinal)
        {
            ["ProjectReference"] = "project",
            ["PackageReference"] = "package",
            ["FrameworkReference"] = "framework",
            ["Reference"] = "assembly",
            ["PackageDownload"] = "package",
            ["GlobalPackageReference"] = "package",
            ["COMReference"] = "com",
            ["NativeReference"] = "native",
        };

        /// <summary>
        /// Walks up from <paramref name="startDirectory" /> to the directory holding
        /// <c>BudgetoidApp.sln</c>, and throws rather than returning nothing when no ancestor does.
        /// </summary>
        internal static DirectoryInfo SolutionRootFrom(string startDirectory)
        {
            for (DirectoryInfo? directory = new(startDirectory);
                 directory is not null;
                 directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
                {
                    return directory;
                }
            }

            throw new InvalidOperationException(
                $"No ancestor of '{startDirectory}' holds {SolutionFileName}. This test scans the "
                + "solution's project files from disk; it cannot run from a published output that "
                + "carries no source tree.");
        }

        /// <summary>
        /// The MSBuild files that can give a project a dependency.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A csproj is not the only one, and scanning only csproj was a real hole.</b>
        /// <c>Directory.Build.props</c> is imported into every project in the tree, Domain included,
        /// so a single <c>PackageReference</c> added there compiles Domain against EF Core while
        /// every csproj in the solution stays untouched. That was verified, not theorised: with the
        /// package declared there and nothing else changed, <c>Domain</c> compiled against
        /// <c>Microsoft.EntityFrameworkCore.DbContext</c> and this test was green.
        /// </para>
        /// <para>
        /// <c>Directory.Packages.props</c> exists in none of these directories today and is scanned
        /// anyway, because the file that does not exist yet is exactly the one nobody thinks to add
        /// to a scan.
        /// </para>
        /// </remarks>
        private static readonly string[] BuildFilePatterns =
        [
            "*.csproj",
            "Directory.Build.props",
            "Directory.Build.targets",
            "Directory.Packages.props",
        ];

        /// <summary>
        /// Renders every MSBuild file under <paramref name="root" /> as dependency edges.
        /// </summary>
        /// <remarks>
        /// Discovery is a bare recursive glob on purpose. <c>bin</c> and <c>obj</c> are skipped
        /// because they are build output rather than source — no project file lives there today,
        /// and if one appears it is a copy of one already scanned. That is the only exclusion, and
        /// no other may be added: any filter narrowing which *files* are examined would let the
        /// next one in unnoticed, which is the whole failure this test exists to prevent.
        /// </remarks>
        internal static IReadOnlyList<string> EdgesOfEveryProjectUnder(DirectoryInfo root)
        {
            List<string> edges = [];

            foreach (string path in BuildFilesUnder(root))
            {
                edges.AddRange(
                    EdgesOf(Path.GetFileNameWithoutExtension(path), XDocument.Load(path)));
            }

            return edges;
        }

        /// <summary>Names the build files the scan reads, for the test that asserts it reads them.</summary>
        internal static string[] BuildFileNamesUnder(DirectoryInfo root) =>
            [.. BuildFilesUnder(root).Select(Path.GetFileName).OfType<string>().Distinct()];

        private static IEnumerable<string> BuildFilesUnder(DirectoryInfo root) =>
            BuildFilePatterns
                .SelectMany(pattern =>
                    Directory.EnumerateFiles(root.FullName, pattern, SearchOption.AllDirectories))
                .Where(path => !IsBuildOutput(path, root))
                .Distinct()
                .Order(StringComparer.Ordinal);

        private static bool IsBuildOutput(string path, DirectoryInfo root) =>
            Path.GetRelativePath(root.FullName, path)
                .Split(Path.DirectorySeparatorChar)
                .Any(segment => segment is "bin" or "obj");

        /// <summary>
        /// Renders one csproj document as the set of dependency edges it declares. Takes an
        /// <see cref="XDocument" /> and never a path, which is what lets the negative control below
        /// prove the guard catches a violation without anyone editing a real project file.
        /// </summary>
        internal static IReadOnlyList<string> EdgesOf(string projectName, XDocument document)
        {
            XElement? root = document.Root;

            if (root is null)
            {
                return [];
            }

            List<string> edges = [];

            // The Sdk attribute is an inbound edge like any other, and on Api it is the *only*
            // record that the whole ASP.NET Core shared framework is in scope: Microsoft.NET.Sdk.Web
            // implies a FrameworkReference that appears in no ItemGroup. Everything after "/" is a
            // version, and AppHost is the one project that carries one.
            if (root.Attribute("Sdk")?.Value is { } sdk)
            {
                edges.Add($"{projectName}: sdk {sdk.Split('/')[0]}");
            }

            // MSBuild accepts the same import as a child element, and a project carrying
            // <Sdk Name="Microsoft.NET.Sdk.Web" /> beneath an unchanged Sdk attribute resolves the
            // ASP.NET Core types while the attribute row above says nothing changed.
            foreach (XElement element in root.Elements().Where(e => e.Name.LocalName == "Sdk"))
            {
                if (element.Attribute("Name")?.Value is { } named)
                {
                    edges.Add($"{projectName}: sdk {named.Split('/')[0]}");
                }
            }

            // Filtered on LocalName rather than matched by name: SDK-style projects carry no XML
            // namespace today, and a project that grew one would otherwise render no item edges at
            // all. That fails loudly rather than quietly — every missing row is reported — but it
            // fails for a reason that has nothing to do with the graph, which is its own cost.
            foreach (XElement element in root.Descendants())
            {
                // An Import names its target in Project rather than Include, and it is rendered as
                // an edge without being followed: an imported file can carry any of the items
                // below, so the honest report is that the project pulled in something the scan does
                // not read, and a human has to say what is in it.
                if (element.Name.LocalName == "Import")
                {
                    if (element.Attribute("Project")?.Value is { } imported)
                    {
                        edges.Add($"{projectName}: import {imported}");
                    }

                    continue;
                }

                if (element.Attribute("Include")?.Value is not { } include)
                {
                    continue;
                }

                if (DependencyKinds.TryGetValue(element.Name.LocalName, out string? kind))
                {
                    edges.Add(kind == "project"
                        ? $"{projectName}: project {FileStemOf(include)}"
                        : $"{projectName}: {kind} {include}");
                }
            }

            return edges;
        }

        /// <summary>
        /// Reduces a <c>ProjectReference</c> path to the referenced project's file stem, which is
        /// the name the graph is keyed on. Backslashes are normalised first because csproj writes
        /// Windows separators and this test also runs on Linux and macOS, where
        /// <see cref="Path.GetFileNameWithoutExtension(string)" /> would treat the whole path as
        /// one segment and return <c>..\Domain\Domain</c>.
        /// </summary>
        private static string FileStemOf(string include) =>
            Path.GetFileNameWithoutExtension(include.Replace('\\', '/'));
    }
}
