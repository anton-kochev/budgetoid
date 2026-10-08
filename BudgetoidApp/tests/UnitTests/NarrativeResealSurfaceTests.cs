using System.Reflection;
using System.Reflection.Emit;
using Domain.Accounts;
using Domain.Budgets;
using Domain.Categories;
using Domain.CategoryGroups;
using Domain.Common;
using Domain.Payees;
using Domain.Security;
using Domain.Transactions;
using TUnit.Assertions.Enums;

namespace UnitTests;

/// <summary>
/// The call-site census for <see cref="NarrativeReseal" />: which entities declare a reseal member, and
/// which of those members actually route their nullable narrative column through the one owner of the
/// presence rule.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists, in one sentence: nothing else in the suite can tell a reseal member that
/// calls <see cref="NarrativeReseal.Resealed" /> from one that restates the two arms inline.</b>
/// <c>NarrativeResealTests</c> holds the rule itself and says so in its own prose — it can prove that
/// the shared function refuses a cleared column and refuses an invented one, and it can prove nothing at
/// all about who calls it. The per-entity files hold the behaviour — each one asserts that its reseal
/// refuses both presence changes — but every one of those assertions is satisfied by four lines written
/// out inside the entity, because the cases assert only that the refusal carries the expected key and
/// never its sentence. So six entities each restating the rule would be green today, and would stay
/// green as the six copies drift apart. The copy that drops the "invented a value" arm, or gives both
/// arms one shared sentence, still stores, still reads back and still opens; it differs from the others
/// only in what it lets a rotation do to a column nobody exercised that day. That is the property this
/// file holds and the only one it holds.
/// </para>
/// <para>
/// <b>The second half closes an asymmetry, not a rule.</b> <c>DomainImmutabilityTests</c> pins the
/// public method set of <see cref="Account" /> and <see cref="Budget" /> and of nothing else, so
/// <see cref="Account.Reseal" /> had to be acknowledged by a human while <see cref="Payee.Reseal" />,
/// <see cref="Category.Reseal" />, <see cref="CategoryGroup.Reseal" /> and
/// <see cref="Transaction.ResealDescription" /> could land with nothing noticing. Widening that file to
/// six entities would pin every public member of all six and make an unrelated rename a failure in a
/// file about currency redenomination; the set pinned here is narrower — the reseal members only — and
/// belongs beside the rule they are supposed to call.
/// </para>
/// <para>
/// <b>What the routing half can see, stated before it is read rather than inferred from it.</b> The
/// mechanism is IL: the member's compiled body is walked instruction by instruction and every
/// <c>call</c>/<c>callvirt</c>/<c>newobj</c> operand is resolved back to the method it names, which is a
/// direct reading of what the member does rather than of how its source is spelled. It catches the whole
/// of the failure it was written for — a member whose body no longer reaches
/// <see cref="NarrativeReseal.Resealed" />, however the replacement is spelled, whether that is four
/// inline lines, a private copy on the entity, or a silent deletion of the judgement altogether.
/// </para>
/// <para>
/// <b>What it misses, and these are real.</b> (1) <b>It sees the call, never the arguments.</b> A member
/// calling <c>Resealed(current, current, column)</c>, or passing the wrong column name, or swapping the
/// two envelopes, passes this census — those are caught, if at all, by the per-entity behaviour cases.
/// (2) <b>It sees the call, never what is done with the answer.</b> A member that calls
/// <c>Resealed</c> for its refusals and then assigns the raw incoming value, or assigns
/// <c>current</c>, is invisible here. (3) <b>It cannot see a rule added beside the call.</b> An entity
/// that routes correctly and then applies an extra refusal of its own — a length check, an idempotence
/// check — is green. (4) <b>Only the member's own body is read, one level deep.</b> A refactor that
/// moved the call into a private helper the reseal invokes would redden even though the routing is
/// intact; that is a deliberate false positive, on the census principle that a change to how the rule is
/// reached is a change somebody should have to look at, not a shape the scanner quietly accommodates.
/// (5) <b>A call reached through a delegate or through reflection is not an IL call to the target</b>
/// and would redden the same way. (6) The count below is call <b>sites</b>, so a hypothetical entity
/// judging two nullable columns in a loop would report one call for two columns and redden; no entity
/// has two today, and the arithmetic is written where it can be argued about rather than relaxed to
/// "at least one", which is the form that stops noticing the second column.
/// </para>
/// <para>
/// <b>The expected call count is derived, not listed.</b> An entity must call the rule exactly once per
/// nullable narrative column it declares — so <see cref="Account" /> and <see cref="Payee" />, whose
/// names are typed <see cref="NarrativeField" /> rather than <see cref="NarrativeField" /><c>?</c>, must
/// not call it at all, and the day one of those names is made nullable the same expression starts
/// demanding the call. A hard-coded list of "the four that route" would keep passing through exactly
/// that change. Which properties are nullable columns and which are carriers is worked out the way
/// <c>NarrativeResealTests</c> works it out and for the reason it gives: <see cref="PropertyInfo.CanWrite" />
/// separates a column the store materialises from <see cref="IndexedName" />'s get-only member, which is
/// the shape a sealed name and its blind index travel in and not a column at all.
/// </para>
/// </remarks>
public sealed class NarrativeResealSurfaceTests
{
    /// <summary>
    /// Every entity carrying a narrative column declares exactly one reseal member, and no other type
    /// declares one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both sides are discovered and only the answer is written down, which is what makes this a
    /// census.</b> The entities come from the narrative properties they declare, the members come from a
    /// scan of the whole domain assembly, and the expected set below is the one hand-written list. A
    /// seventh sealed entity arriving without a reseal member reddens on the entity line; an existing
    /// entity growing a <em>second</em> reseal member reddens on the signature set; a reseal member
    /// appearing on something that is not a sealed entity reddens on its own line rather than being
    /// filtered away.
    /// </para>
    /// <para>
    /// <b>A member is a reseal by either of two independent marks, and the union is the point.</b> Its
    /// name begins with <c>Reseal</c>, or it takes a <see cref="Guid" /> called <c>rotationId</c> — the
    /// rotation's one way into an entity. Either hook alone is evadable by an author who did not know
    /// the census existed: a <c>Rewrite(NarrativeField?, Guid rotationId)</c> escapes a name test, and a
    /// <c>Reseal</c> that took its identifier under another name escapes a signature test. Both have to
    /// be evaded at once to land a rotation path here unnoticed.
    /// </para>
    /// <para>
    /// <b><see cref="Budget.ResealName" /> is <see langword="internal" /> and is in the set anyway.</b>
    /// The scan reads non-public instance methods for exactly that reason; accessibility is a different
    /// question, owned by <c>BudgetTests.ResealName_IsInternalToTheDomain</c>, and a census that could
    /// only see public members would lose the one entity whose reseal is deliberately not public.
    /// </para>
    /// </remarks>
    [Test]
    public async Task EveryEntityCarryingANarrativeColumn_DeclaresExactlyOneResealMember()
    {
        // Arrange — the entities, read off the columns they declare rather than listed.
        Type[] entities = SealedEntities();

        // Act
        MethodInfo[] resealMembers = ResealMembers();
        string[] entitiesWithoutOne = entities
            .Where(entity => resealMembers.Count(member => member.DeclaringType == entity) == 0)
            .Select(entity => entity.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] entitiesWithMoreThanOne = entities
            .Where(entity => resealMembers.Count(member => member.DeclaringType == entity) > 1)
            .Select(entity => entity.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] resealsOnSomethingElse = resealMembers
            .Where(member => !entities.Contains(member.DeclaringType))
            .Select(Describe)
            .Order(StringComparer.Ordinal)
            .ToArray();

        // Assert — the three derived buckets first, each on a collection rather than a joined string, so
        // a failure prints every offender. TUnit truncates a long string assertion after about a hundred
        // characters, which on a census of six would name one of them and hide the rest.
        await Assert.That(entitiesWithoutOne).IsEmpty();
        await Assert.That(entitiesWithMoreThanOne).IsEmpty();
        await Assert.That(resealsOnSomethingElse).IsEmpty();

        // The entity set itself, so that a seventh sealed entity is a line somebody writes rather than a
        // row that quietly joins the scan. IndexedName is absent because its narrative member is
        // get-only: a carrier, not a column, and nothing can reseal it.
        await Assert.That(entities.Select(entity => entity.Name).Order(StringComparer.Ordinal).ToArray())
            .IsEquivalentTo(
                new[]
                {
                    nameof(Account),
                    nameof(Budget),
                    nameof(Category),
                    nameof(CategoryGroup),
                    nameof(Payee),
                    nameof(Transaction),
                }.Order(StringComparer.Ordinal).ToArray(),
                CollectionOrdering.Matching);

        // The signatures, written out. Parameter names and CLR type names as reflection renders them —
        // "NarrativeField", not "NarrativeField?", because nullable annotations are erased from Type and
        // the nullable column set is pinned by NarrativeResealTests rather than restated here. Editing a
        // line to look like the C# declaration is how this starts failing for no reason.
        string[] signatures = resealMembers
            .Select(Describe)
            .Order(StringComparer.Ordinal)
            .ToArray();
        await Assert.That(signatures).IsEquivalentTo(
            new[]
            {
                "Account: Void Reseal(IndexedName name, Guid rotationId)",
                "Budget: Void ResealName(NarrativeField name, Guid rotationId)",
                "Category: Void Reseal(IndexedName name, NarrativeField description, Guid rotationId)",
                "CategoryGroup: Void Reseal(IndexedName name, NarrativeField description, "
                + "Guid rotationId)",
                "Payee: Void Reseal(IndexedName name, Guid rotationId)",
                "Transaction: Void ResealDescription(NarrativeField description, Guid rotationId)",
            }.Order(StringComparer.Ordinal).ToArray(),
            CollectionOrdering.Matching);
    }

    /// <summary>
    /// Every reseal member calls <see cref="NarrativeReseal.Resealed" /> exactly once per nullable
    /// narrative column its entity declares.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the assertion the file was written for.</b> Read the class remarks for what the
    /// mechanism can and cannot see before trusting it with anything; in particular it reads the call and
    /// not the arguments, so it says "the rule was consulted" and never "the rule was consulted
    /// correctly".
    /// </para>
    /// <para>
    /// <b>Both directions are the same expression.</b> An entity with a nullable narrative column must
    /// call the rule; an entity with none must not, because there is no absence for it to judge and a
    /// call would be a rule applied to a column whose type already carries its presence. Stating it as
    /// one derived count rather than as two lists means the day <see cref="Account.Name" /> or
    /// <see cref="Payee.Name" /> is made nullable, this census starts demanding a call that nothing else
    /// would ask for.
    /// </para>
    /// </remarks>
    [Test]
    public async Task EveryResealMember_CallsNarrativeReseal_OncePerNullableNarrativeColumn()
    {
        // Arrange
        MethodInfo[] resealMembers = ResealMembers();

        // Act — one row per member, so the failure names the entity that stopped routing and the
        // arithmetic that was expected of it rather than reporting a count that differs somewhere.
        string[] disagreeing = resealMembers
            .Select(member => new
            {
                Member = member,
                Calls = CallsToTheResealRuleIn(member),
                Expected = NullableNarrativeColumnsOf(member.DeclaringType!),
            })
            .Where(row => row.Calls != row.Expected)
            .Select(row =>
                $"{Describe(row.Member)} calls NarrativeReseal.Resealed {row.Calls} time(s), "
                + $"expected {row.Expected} — one per nullable narrative column.")
            .Order(StringComparer.Ordinal)
            .ToArray();

        // Assert
        await Assert.That(disagreeing).IsEmpty();

        // The scan reached something. A reflection query that came back empty — a renamed member, a
        // narrowed binding flag — would satisfy the line above by having nothing to disagree with.
        await Assert.That(resealMembers.Length).IsEqualTo(6);
    }

    /// <summary>
    /// The instruction walker resolves the calls a method really makes, and reports no call a method
    /// does not make.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The control for the census above, and it is not ceremony.</b> A walker that resolved nothing
    /// would report zero calls everywhere — which happens to be the right answer for
    /// <see cref="Account" /> and <see cref="Payee" />, so half the census would pass against a dead
    /// scanner. The positive assertions here are what make that impossible to mistake for a result.
    /// </para>
    /// <para>
    /// <b>The second call asserted is deliberately not the subject.</b>
    /// <c>ArgumentNullException.ThrowIfNull</c> lives in another assembly, so resolving it proves the
    /// walker follows a token across a module boundary rather than only finding methods next door to the
    /// caller. A walker that mis-sized one operand would drift out of step and resolve garbage, and the
    /// two named calls are how that shows up as a failure rather than as a plausible number.
    /// </para>
    /// </remarks>
    [Test]
    public async Task TheInstructionWalker_ResolvesTheCallsAMethodMakes()
    {
        // Arrange — Category.Reseal is the fullest of the six: a null guard, a rotation guard, the rule
        // itself, and four assignments.
        MethodInfo routes = ResealMembers().Single(member => member.DeclaringType == typeof(Category));
        MethodInfo doesNot = ResealMembers().Single(member => member.DeclaringType == typeof(Account));

        // Act
        MethodBase[] callees = CalleesOf(routes);

        // Assert
        await Assert.That(callees.Any(callee =>
            callee.DeclaringType == typeof(NarrativeReseal)
            && callee.Name == nameof(NarrativeReseal.Resealed))).IsTrue();
        await Assert.That(callees.Any(callee =>
            callee.DeclaringType == typeof(ArgumentNullException)
            && callee.Name == nameof(ArgumentNullException.ThrowIfNull))).IsTrue();
        await Assert.That(CallsToTheResealRuleIn(routes)).IsEqualTo(1);
        await Assert.That(CallsToTheResealRuleIn(doesNot)).IsEqualTo(0);
    }

    private const BindingFlags DeclaredInstance =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;

    /// <summary>
    /// Every public class in the domain assembly that declares at least one narrative column.
    /// </summary>
    /// <remarks>
    /// A column is a narrative property the store can write, which <see cref="PropertyInfo.CanWrite" />
    /// answers whatever the setter's accessibility. <see cref="IndexedName" /> falls out here rather than
    /// being named and excluded, because its member is get-only — set once by its own constructor — and
    /// the distinction is the one <c>NarrativeResealTests</c> derives and argues at length.
    /// </remarks>
    private static Type[] SealedEntities() => typeof(Budget).Assembly
        .GetTypes()
        .Where(type => type is { IsClass: true, IsPublic: true })
        .Where(type => type
            .GetProperties(PublicInstance)
            .Any(property => property.PropertyType == typeof(NarrativeField) && property.CanWrite))
        .ToArray();

    /// <summary>
    /// Every reseal member the domain assembly declares, wherever it lives.
    /// </summary>
    /// <remarks>
    /// The query is deliberately not narrowed to the entities: a reseal member on a type carrying no
    /// narrative column is a real design event, and it should arrive as a row somebody has to explain
    /// rather than as a filter quietly doing its job.
    /// </remarks>
    private static MethodInfo[] ResealMembers() => typeof(Budget).Assembly
        .GetTypes()
        .Where(type => type is { IsClass: true, IsPublic: true })
        .SelectMany(type => type.GetMethods(DeclaredInstance))
        .Where(method => !method.IsSpecialName && IsAReseal(method))
        .ToArray();

    /// <summary>
    /// Whether <paramref name="method" /> is a rotation's way into an entity, by either of the two
    /// independent marks the census remarks argue.
    /// </summary>
    private static bool IsAReseal(MethodInfo method) =>
        method.Name.StartsWith("Reseal", StringComparison.Ordinal)
        || method.GetParameters().Any(parameter =>
            parameter.ParameterType == typeof(Guid) && parameter.Name == "rotationId");

    /// <summary>
    /// How many narrative columns <paramref name="entity" /> declares that can be absent.
    /// </summary>
    private static int NullableNarrativeColumnsOf(Type entity) => entity
        .GetProperties(PublicInstance)
        .Count(property =>
            property.PropertyType == typeof(NarrativeField)
            && property.CanWrite
            && new NullabilityInfoContext().Create(property).ReadState == NullabilityState.Nullable);

    /// <summary>
    /// How many instructions in <paramref name="method" /> call
    /// <see cref="NarrativeReseal.Resealed" />.
    /// </summary>
    /// <remarks>
    /// Matched on the declaring type and the member name rather than on a resolved
    /// <see cref="MethodInfo" /> instance, so a future overload of the rule still counts as routing
    /// through it.
    /// </remarks>
    private static int CallsToTheResealRuleIn(MethodInfo method) => CalleesOf(method)
        .Count(callee =>
            callee.DeclaringType == typeof(NarrativeReseal)
            && callee.Name == nameof(NarrativeReseal.Resealed));

    /// <summary>
    /// Every method named by a call instruction in <paramref name="method" />'s own body.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One level deep and no further: what a callee goes on to call is not read. The class remarks say
    /// what that costs.
    /// </para>
    /// <para>
    /// The walk is a real instruction walk rather than a search for the <c>call</c> byte, because an
    /// operand can hold that byte and a byte search would resolve whatever token happened to follow it.
    /// The opcode tables are read off <see cref="OpCodes" /> rather than typed out, so the sizes come
    /// from the runtime's own definitions.
    /// </para>
    /// </remarks>
    private static MethodBase[] CalleesOf(MethodInfo method)
    {
        byte[] il = method.GetMethodBody()?.GetILAsByteArray()
            ?? throw new InvalidOperationException($"{Describe(method)} has no readable IL body.");

        Type[] typeArguments = method.DeclaringType!.IsGenericType
            ? method.DeclaringType.GetGenericArguments()
            : [];
        Type[] methodArguments = method.IsGenericMethodDefinition ? method.GetGenericArguments() : [];

        List<MethodBase> callees = [];
        int position = 0;

        while (position < il.Length)
        {
            OpCode opcode = ReadOpCode(il, ref position);

            if (opcode.OperandType == OperandType.InlineMethod)
            {
                int token = BitConverter.ToInt32(il, position);
                MethodBase? callee = ResolveOrThrow(method, token, typeArguments, methodArguments);

                if (callee is not null)
                {
                    callees.Add(callee);
                }
            }

            position += OperandSize(opcode, il, position);
        }

        return [.. callees];
    }

    /// <summary>
    /// Resolves a metadata token to the method it names, naming the caller if it cannot.
    /// </summary>
    /// <remarks>
    /// A swallowed resolution failure is the one way this scan could report "no call to the rule" for a
    /// method that makes one, so the failure is raised rather than skipped.
    /// </remarks>
    private static MethodBase? ResolveOrThrow(
        MethodInfo caller,
        int token,
        Type[] typeArguments,
        Type[] methodArguments)
    {
        try
        {
            return caller.Module.ResolveMethod(token, typeArguments, methodArguments);
        }
        catch (Exception failure)
        {
            throw new InvalidOperationException(
                $"Could not resolve token 0x{token:X8} in {Describe(caller)}.",
                failure);
        }
    }

    /// <summary>Reads the one- or two-byte opcode at <paramref name="position" /> and advances past it.</summary>
    private static OpCode ReadOpCode(byte[] il, ref int position)
    {
        byte first = il[position++];

        if (first != TwoBytePrefix)
        {
            return OneByteOpCodes[first]
                ?? throw new InvalidOperationException($"Unknown one-byte opcode 0x{first:X2}.");
        }

        byte second = il[position++];

        return TwoByteOpCodes[second]
            ?? throw new InvalidOperationException($"Unknown two-byte opcode 0xFE{second:X2}.");
    }

    /// <summary>How many operand bytes follow <paramref name="opcode" />.</summary>
    private static int OperandSize(OpCode opcode, byte[] il, int position) => opcode.OperandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI
            or OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString
            or OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
        OperandType.InlineI8 or OperandType.InlineR => 8,

        // A jump table: the count, then one four-byte target apiece.
        OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, position)),
        _ => throw new InvalidOperationException($"Unhandled operand type {opcode.OperandType}."),
    };

    private const byte TwoBytePrefix = 0xFE;

    private static readonly OpCode?[] OneByteOpCodes = BuildOpCodeTable(size: 1);

    private static readonly OpCode?[] TwoByteOpCodes = BuildOpCodeTable(size: 2);

    /// <summary>
    /// The runtime's own opcode definitions, indexed by the byte that selects them.
    /// </summary>
    private static OpCode?[] BuildOpCodeTable(int size)
    {
        OpCode?[] table = new OpCode?[256];

        foreach (FieldInfo field in typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(obj: null) is OpCode opcode && opcode.Size == size)
            {
                table[(ushort)opcode.Value & 0xFF] = opcode;
            }
        }

        return table;
    }

    /// <summary>
    /// Renders a method as the declaring type, its return type, its name and its parameters with names —
    /// a type-only signature would not show a <c>rotationId</c> arriving where a <c>name</c> already
    /// sits.
    /// </summary>
    private static string Describe(MethodInfo method)
    {
        string parameters = string.Join(
            ", ",
            method.GetParameters().Select(parameter =>
                $"{parameter.ParameterType.Name} {parameter.Name}"));

        return $"{method.DeclaringType!.Name}: {method.ReturnType.Name} {method.Name}({parameters})";
    }
}
