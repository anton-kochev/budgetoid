using Application.Abstractions;

namespace UnitTests.Fakes;

/// <summary>
/// An <see cref="IPersistenceState"/> that forwards the discard to the fakes standing in for the
/// change tracker's contents, and records which attempt each discard was made on.
/// </summary>
/// <remarks>
/// The attempt number is recorded rather than a plain call count because the call being made is not
/// the property under test — where it is made is. A discard performed before the executor is entered
/// would run once, on attempt zero, and would not survive the rollback it exists to clean up after;
/// one performed at the top of the delegate runs on every attempt.
/// </remarks>
public sealed class RecordingPersistenceState(Func<int> currentAttempt, params Action[] onDiscard)
    : IPersistenceState
{
    private readonly List<int> _discardedOnAttempt = [];

    /// <summary>The attempt number each discard was made on, oldest first.</summary>
    public IReadOnlyList<int> DiscardedOnAttempt => _discardedOnAttempt;

    public void DiscardTrackedEntities()
    {
        _discardedOnAttempt.Add(currentAttempt());

        foreach (Action discard in onDiscard)
        {
            discard();
        }
    }
}
