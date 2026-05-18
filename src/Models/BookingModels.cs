namespace BookingStateMachine.Models;

// ─── States ───────────────────────────────────────────────────────────────────

/// <summary>
/// Four-state lifecycle of a conference-room booking.
/// </summary>
public enum BookingState
{
    /// <summary>Process created, no work done yet.</summary>
    Created,

    /// <summary>Room availability checked and reserved (holds a slot).</summary>
    RoomReserved,

    /// <summary>Notification sent to the requester.</summary>
    NotificationSent,

    /// <summary>Booking confirmed and visible to everyone.</summary>
    Confirmed,

    /// <summary>Terminal: booking cancelled / rolled back.</summary>
    Cancelled
}

// ─── Events ───────────────────────────────────────────────────────────────────

public enum BookingEvent
{
    ReserveRoom,
    SendNotification,
    Confirm,
    Cancel          // compensation trigger
}

// ─── Process record ───────────────────────────────────────────────────────────

/// <summary>
/// Represents a single booking process held in memory.
/// </summary>
public sealed class BookingProcess
{
    /// <summary>Business key that uniquely identifies this booking process.</summary>
    public string ProcessKey { get; init; } = default!;

    public BookingState State { get; set; } = BookingState.Created;

    /// <summary>
    /// Set of idempotency keys already applied to this process.
    /// Prevents double-application of the same event delivery.
    /// </summary>
    public HashSet<string> ProcessedIdempotencyKeys { get; } = new(StringComparer.Ordinal);

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Audit trail (in-memory only, good for demo/tests).</summary>
    public List<AuditEntry> History { get; } = new();
}

/// <summary>One line in the audit trail of a booking process.</summary>
public sealed record AuditEntry(
    DateTimeOffset Timestamp,
    string CorrelationId,
    string IdempotencyKey,
    BookingState? FromState,
    BookingState ToState,
    string EventName,
    string Note
);

// ─── API request / response DTOs ─────────────────────────────────────────────

public sealed record CreateBookingRequest(
    string ProcessKey,
    string CorrelationId);

public sealed record TransitionRequest(
    string ProcessKey,
    BookingEvent Event,
    /// <summary>Caller-supplied unique token for this exact delivery.</summary>
    string IdempotencyKey,
    string CorrelationId,
    /// <summary>When true the step will throw intentionally (chaos testing).</summary>
    bool SimulateFailure = false);

public sealed record BookingStatusResponse(
    string ProcessKey,
    BookingState State,
    List<AuditEntry> History);

public sealed record ErrorResponse(string CorrelationId, string Error);
