namespace Exo.Engine;

public enum OperationKind
{
    Detect,
    Plan,
    Snapshot,
    Apply,
    Verify,
    Restore,
}

public sealed record TweakSnapshot<TSnapshot>(
    Guid SnapshotId,
    string TweakId,
    DateTimeOffset CapturedAt,
    TSnapshot Value);

public interface ISnapshotStore
{
    ValueTask Save<TSnapshot>(
        TweakSnapshot<TSnapshot> snapshot,
        CancellationToken cancellationToken = default);

    ValueTask<TweakSnapshot<TSnapshot>?> LoadLatest<TSnapshot>(
        string tweakId,
        CancellationToken cancellationToken = default);
}

public sealed record OperationJournalEntry(
    Guid OperationId,
    string TweakId,
    OperationKind Kind,
    OperationOutcome Outcome,
    DateTimeOffset Timestamp,
    string? Message = null);

public interface IOperationJournal
{
    ValueTask Append(
        OperationJournalEntry entry,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<OperationJournalEntry>> Read(
        string tweakId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A process-local implementation for tests and composition roots. Durable implementations
/// can replace it without changing adapter contracts.
/// </summary>
public sealed class RecordingSnapshotStore : ISnapshotStore
{
    private readonly object _gate = new();
    private readonly Dictionary<(string TweakId, Type SnapshotType), object> _latest = [];

    public ValueTask Save<TSnapshot>(
        TweakSnapshot<TSnapshot> snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _latest[(snapshot.TweakId, typeof(TSnapshot))] = snapshot;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<TweakSnapshot<TSnapshot>?> LoadLatest<TSnapshot>(
        string tweakId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tweakId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(
                _latest.TryGetValue((tweakId, typeof(TSnapshot)), out var value)
                    ? (TweakSnapshot<TSnapshot>)value
                    : null);
        }
    }
}

/// <summary>
/// A process-local journal for tests and composition roots. Entries remain ordered by append.
/// </summary>
public sealed class RecordingOperationJournal : IOperationJournal
{
    private readonly object _gate = new();
    private readonly List<OperationJournalEntry> _entries = [];

    public ValueTask Append(
        OperationJournalEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _entries.Add(entry);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<OperationJournalEntry>> Read(
        string tweakId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tweakId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyList<OperationJournalEntry> result = _entries
                .Where(entry => string.Equals(entry.TweakId, tweakId, StringComparison.Ordinal))
                .ToArray();
            return ValueTask.FromResult(result);
        }
    }
}
