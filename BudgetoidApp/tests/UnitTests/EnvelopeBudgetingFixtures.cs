using System.Linq.Expressions;
using Domain.Security;

// The synthetic subjects the CON-005 detector is proved against. They live in this assembly, under a
// namespace prefix no production code uses, so the permanent controls in
// EnvelopeBudgetingIsolationTests scan UnitTests.dll while the real gate scans Application.dll and
// Domain.dll. A control that had to edit a real computation to watch the detector go red would be a
// measurement of a change somebody reverted, not a guard.
//
// They are top-level types and not nested ones on purpose: a nested type carries an empty namespace
// in metadata, so nesting them inside the test class would put every fixture outside every prefix and
// the whole subject would be the empty set — the exact failure this file exists to make impossible.
namespace UnitTests.Fixtures.EnvelopeSubjects
{
    /// <summary>
    /// The row an envelope-budgeting computation would read: three columns it may read and one it may not.
    /// </summary>
    /// <remarks>
    /// Deliberately outside the budgeting prefix. It <i>declares</i> a narrative member, and a fixture
    /// living inside the prefix would be reported on its own account — which would make the clean case
    /// below unable to tell "the detector found nothing here" from "the detector found the row this
    /// computation reads".
    /// </remarks>
    public sealed class BudgetRow
    {
        public Guid Id { get; init; }

        public decimal Amount { get; init; }

        public DateOnly Date { get; init; }

        public NarrativeField? Note { get; init; }
    }
}

namespace UnitTests.Fixtures.Budgeting.BodyRead
{
    /// <summary>
    /// Reads a narrative field in a method body and nowhere else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the shape that defeats a signature census, and it is the reason the detector decodes
    /// the signature of every member a body references.</b> Nothing about this type mentions
    /// <see cref="NarrativeField" />: the field it declares is a <see langword="decimal" />, the method
    /// takes an <see cref="IEnumerable{T}" /> of rows and returns an <see langword="int" />. The touch
    /// exists only as a <c>callvirt</c> to <c>BudgetRow::get_Note</c>, whose <i>declaring</i> type is
    /// <c>BudgetRow</c> — so a body scan that reads only the tokens' declaring types reports zero hits
    /// over this type, silently, which is what a naive scan was measured doing. <b>Zero over this
    /// type and not over the assembly, which is the noun an earlier draft got wrong</b>: measured,
    /// that scan still names 3 of the 6 computations under the fixture prefix — the ones whose token
    /// owner is the narrative type or names it structurally — and this one is not among them, which
    /// is what makes it the fixture for the signature half. Both fixtures live in
    /// this assembly, so that read is a <c>MethodDefinition</c> token; the cross-assembly form is a
    /// <c>MemberReference</c>, and the detector decodes both because the real gate meets the second.
    /// </para>
    /// <para>
    /// The name is the requirement's own vocabulary. CON-005 names assignments, activity, available
    /// and "to allocate"; a computation that decides availability from a note is exactly the thing the
    /// constraint says the envelope layer may never grow into.
    /// </para>
    /// </remarks>
    public sealed class AvailableFromNotes
    {
        // A direct read in this method's own body, deliberately not inside a lambda: a lambda would
        // put the touch in a display class and make this case redden for the reason the display-class
        // case beside it already covers. One mechanism per fixture.
        public int Available(EnvelopeSubjects.BudgetRow row) => row.Note is null ? 0 : 1;
    }
}

namespace UnitTests.Fixtures.Budgeting.Declared
{
    /// <summary>
    /// Declares narrative members and reads none of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The complement of the body-read fixture, and the reason the detector keeps a declaration
    /// half at all.</b> Both members are unreachable from any IL in this assembly: a plain field is
    /// written and read by nobody, and <see cref="Describe" /> compiles to <c>ldnull; ret</c> — no
    /// token, so no body token to decode. A detector that walked bodies alone would report this type
    /// clean while it sits there holding the column.
    /// </para>
    /// <para>
    /// A field rather than an auto-property, deliberately. An auto-property's accessors carry
    /// <c>ldfld</c>/<c>stfld</c> against a backing field whose signature names the type, so the body
    /// half would catch it and the declaration half would look load-bearing without being so. The
    /// field and the method are the two shapes that leave the body half nothing to find.
    /// </para>
    /// </remarks>
    public sealed class CarriesANote
    {
        // Intentionally never read: see the remarks. A never-read field is a warning in some
        // configurations and a fact here.
        public NarrativeField? Note;

        public NarrativeField? Describe() => null;
    }
}

namespace UnitTests.Fixtures.Budgeting.Projecting
{
    /// <summary>
    /// Touches a narrative field inside a lambda, so the touch lives in a compiler-generated type.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The least obvious property of the whole detector.</b> The projection compiles into a display
    /// class nested inside this type — <c>&lt;&gt;c</c> — and a nested type carries an <i>empty</i>
    /// namespace in metadata. A detector that filtered on each type definition's own namespace would
    /// therefore skip every lambda in the subject, which is where a real projection over a row puts
    /// its column reads. Attribution walks <c>GetDeclaringType()</c> to the outermost type and judges
    /// the prefix there, which is what makes both the filter and the reported name right.
    /// </para>
    /// <para>
    /// The anonymous type the projection builds is a third compiler-generated type, top-level and in
    /// the global namespace. It is outside every prefix and is not reported, which is correct: it is a
    /// carrier, not a computation, and reporting it would name a type no reader can find in the source.
    /// </para>
    /// </remarks>
    public sealed class ProjectsANote
    {
        public int Activity(IEnumerable<EnvelopeSubjects.BudgetRow> rows) =>
            rows.Select(row => new { row.Note }).Count();
    }

    /// <summary>Puts the touch in an expression tree rather than a delegate body.</summary>
    /// <remarks>
    /// The <c>IQueryable</c> shape, and the one an envelope computation reading the database would
    /// take. The property read survives as an <c>ldtoken</c> of <c>BudgetRow::get_Note</c> handed to
    /// <c>Expression.Property</c>, so it is found by the same signature decoding as the direct read —
    /// which is worth a fixture rather than an assumption, because "an expression tree is not IL" is
    /// the reasonable-sounding thing a reader would conclude.
    /// </remarks>
    public sealed class QueriesANote
    {
        public Expression<Func<EnvelopeSubjects.BudgetRow, bool>> Predicate() =>
            row => row.Note != null;
    }
}

namespace UnitTests.Fixtures.Budgeting.CrossAssembly
{
    /// <summary>
    /// Reads a narrative column declared in <c>Domain</c>, so the touch is a cross-assembly reference.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The shape the real gate actually runs on, and the one every other fixture here misses.</b>
    /// A property read inside one assembly compiles to a <c>MethodDefinition</c> token; a read across
    /// an assembly boundary compiles to a <c>MemberReference</c>. Every other fixture in this file
    /// lives in <c>UnitTests.dll</c> beside the detector, so every other case exercises the first
    /// branch — while <c>Application</c> reading <c>Domain.Accounts.Account.Name</c>, which is what
    /// the gate meets in anger, exercises the second. Measured: a detector with signature decoding
    /// removed from the <c>MemberReference</c> branch <i>alone</i> passed all eight earlier cases.
    /// The branch carrying production's verdict was the untested one.
    /// </para>
    /// <para>
    /// <b>One method, two different arms of that branch, and they are asserted separately.</b>
    /// <c>account.Name</c> names the target only in the referenced member's <i>signature</i> — the
    /// owner is <c>Account</c>. <c>.Envelope</c> names it only as the referenced member's
    /// <i>owner</i> — the signature returns <c>ReadOnlyMemory&lt;byte&gt;</c>. Neither arm covers the
    /// other, and both are shapes <c>AccountDto.FromAccount</c> emits today.
    /// </para>
    /// <para>
    /// A real production type rather than a local stand-in, deliberately: a copy of <c>Account</c>
    /// declared in this assembly would compile to a <c>MethodDefinition</c> token again and put the
    /// fixture straight back in the branch it was written to leave.
    /// </para>
    /// </remarks>
    public sealed class AccountEnvelopeLength
    {
        public int NameLength(Domain.Accounts.Account account) => account.Name.Envelope.Length;
    }
}

namespace UnitTests.Fixtures.Budgeting.NearMiss
{
    /// <summary>
    /// Touches a real type whose name <i>starts with</i> the target's and is not it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The control that holds the comparison ordinal-equal rather than a substring test.</b>
    /// <c>Domain.Security.NarrativeFieldLimits</c> is a real type and its full name contains
    /// <c>Domain.Security.NarrativeField</c> in full, so a detector matching by
    /// <c>Contains</c> reports this fixture and every future neighbour named the same way. Measured:
    /// a substring-matching detector passed all eight earlier cases, because none of them touched a
    /// near miss.
    /// </para>
    /// <para>
    /// <b>The <c>typeof</c> is load-bearing and the constant beside it is not.</b> Reading
    /// <c>NameBytes</c> leaves no token at all — a <c>const int</c> is folded to <c>ldc.i4</c> at the
    /// call site — so a fixture built only from the constant would be invisible to the detector under
    /// every implementation, substring or not, and would prove nothing. The <c>typeof</c> emits an
    /// <c>ldtoken</c> naming the type, which is the only thing here a substring test can trip over.
    /// The constant is kept beside it as the written record of that, because an absent token cannot
    /// be found by reading the report.
    /// </para>
    /// <para>
    /// <c>NarrativeFieldMisuseError</c> would be the second near miss and is not reachable: it is a
    /// client type, declared in TypeScript, with nothing of that name anywhere in the solution.
    /// </para>
    /// </remarks>
    public sealed class CapsFromLimits
    {
        public string Cap() => typeof(NarrativeFieldLimits).Name;

        public int Bytes() => NarrativeFieldLimits.NameBytes;
    }
}

namespace UnitTests.Fixtures.Budgeting.Generic
{
    /// <summary>
    /// Names the narrative type only inside a generic instantiation, never on its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The <c>TypeSpecification</c> shape.</b> <c>new List&lt;NarrativeField&gt;()</c> and
    /// <c>notes.Count</c> both compile to member references whose <i>parent</i> is a type
    /// specification — an encoded signature for <c>List`1&lt;NarrativeField&gt;</c> — and whose own
    /// signatures say <c>Void()</c> and <c>Int32()</c>. So neither the member signature nor a plain
    /// type-reference name carries the target: the only place it appears is inside the parent's
    /// encoded generic instantiation, and reading that means decoding a third kind of handle.
    /// </para>
    /// <para>
    /// The fixture is deliberately barren of every other narrative touch — no parameter, no field, no
    /// property read — so that this case reddens for the type-specification decode and for nothing
    /// else. An earlier draft passed an <c>Account</c> in to fill the list, which put an ordinary
    /// cross-assembly signature touch beside it and made the case pass whether or not the decode
    /// existed.
    /// </para>
    /// </remarks>
    public sealed class CollectsNotes
    {
        public int Count()
        {
            List<NarrativeField> notes = new();
            return notes.Count;
        }
    }
}

namespace UnitTests.Fixtures.Budgeting.Clean
{
    /// <summary>
    /// What CON-005 says an envelope computation is: arithmetic over amounts, dates and identifiers.
    /// </summary>
    /// <remarks>
    /// Without this fixture a detector that reported every type it walked would satisfy both violation
    /// cases and the real gate would fire on a clean tree. It reads three of
    /// <c>BudgetRow</c>'s four members and never the fourth.
    /// </remarks>
    public sealed class ToAllocate
    {
        public decimal Available(IEnumerable<EnvelopeSubjects.BudgetRow> rows, DateOnly asOf) =>
            rows.Where(row => row.Date <= asOf && row.Id != Guid.Empty).Sum(row => row.Amount);
    }
}

namespace UnitTests.Fixtures.OutsideBudgeting
{
    /// <summary>
    /// The body-read violation again, one namespace over, where the detector may not see it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A written-down limit made visible, not a defect.</b> The subject of CON-005 is a namespace
    /// convention — envelope-budgeting computations live under <c>Application.Budgeting</c> and
    /// <c>Domain.Budgeting</c> — so a computation filed anywhere else is invisible to this gate by
    /// construction. The alternative, an opt-in marker interface, moves the same blind spot behind a
    /// forgotten attribute, and this repository has already argued which polarity to prefer: a
    /// forgotten opt-in hands content over with nothing going red.
    /// </para>
    /// <para>
    /// The remedy when this bites is never to widen the prefix list until it matches wherever the code
    /// drifted to. It is to move the computation under the prefix, or to state a new prefix in the gate
    /// and say why the layer grew a third home.
    /// </para>
    /// </remarks>
    public sealed class AvailableFromNotes
    {
        public int Available(EnvelopeSubjects.BudgetRow row) => row.Note is null ? 0 : 1;
    }
}
