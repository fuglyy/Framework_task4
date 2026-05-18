using System.Collections.Concurrent;
using BookingStateMachine.Models;

namespace BookingStateMachine.Services;

/// <summary>
/// Thread-safe in-memory store for all booking processes.
/// Replace with a database-backed implementation in production.
/// </summary>
public sealed class BookingRepository
{
    private readonly ConcurrentDictionary<string, BookingProcess> _store = new(StringComparer.Ordinal);

    public BookingProcess? Find(string processKey)
        => _store.TryGetValue(processKey, out var p) ? p : null;

    public BookingProcess Create(string processKey)
    {
        var process = new BookingProcess { ProcessKey = processKey };
        if (!_store.TryAdd(processKey, process))
            throw new InvalidOperationException($"Process '{processKey}' already exists.");
        return process;
    }

    public IReadOnlyCollection<BookingProcess> All() => (IReadOnlyCollection<BookingProcess>)_store.Values;
}
