using BookingStateMachine.Models;
using BookingStateMachine.Metrics;
using Microsoft.Extensions.Logging;

namespace BookingStateMachine.Services;

/// <summary>
/// Implements the four-state booking state machine.
///
/// Allowed transitions:
///   Created        --ReserveRoom-->      RoomReserved
///   RoomReserved   --SendNotification--> NotificationSent
///   NotificationSent --Confirm-->        Confirmed
///   RoomReserved   --Cancel-->           Cancelled   (compensation: release the room)
///   NotificationSent --Cancel-->         Cancelled   (compensation: retract notification + release room)
///
/// Idempotency: each event delivery carries a unique IdempotencyKey.
///   If the key was already recorded the method returns without changing state.
///
/// Compensation: if SendNotification fails, the machine automatically
///   fires the Cancel path which releases the reserved room.
/// </summary>
public sealed class BookingStateMachineService
{
    private readonly BookingRepository _repo;
    private readonly BookingMetrics _metrics;
    private readonly ILogger<BookingStateMachineService> _logger;

    // Simulated per-step latency in milliseconds (for realistic metrics).
    private static readonly Dictionary<BookingEvent, int> _simulatedLatencyMs = new()
    {
        [BookingEvent.ReserveRoom]       = 80,
        [BookingEvent.SendNotification]  = 50,
        [BookingEvent.Confirm]           = 30,
        [BookingEvent.Cancel]            = 20,
    };

    public BookingStateMachineService(
        BookingRepository repo,
        BookingMetrics metrics,
        ILogger<BookingStateMachineService> logger)
    {
        _repo    = repo;
        _metrics = metrics;
        _logger  = logger;
    }

    // ─── Public API ───────────────────────────────────────────────────────────

    public BookingProcess CreateProcess(string processKey, string correlationId)
    {
        var process = _repo.Create(processKey);

        _logger.LogInformation(
            "[{CorrelationId}] Process '{ProcessKey}' created. State={State}",
            correlationId, processKey, process.State);

        return process;
    }

    /// <summary>
    /// Applies <paramref name="request"/> to the process identified by ProcessKey.
    /// Returns the updated process.
    /// </summary>
    public async Task<BookingProcess> ApplyAsync(TransitionRequest request)
    {
        var process = _repo.Find(request.ProcessKey)
            ?? throw new KeyNotFoundException($"Process '{request.ProcessKey}' not found.");

        // ── Idempotency check ────────────────────────────────────────────────
        lock (process)
        {
            if (process.ProcessedIdempotencyKeys.Contains(request.IdempotencyKey))
            {
                _metrics.IncrementDuplicateDeliveries();
                _logger.LogWarning(
                    "[{CorrelationId}] Duplicate delivery ignored. ProcessKey={ProcessKey} IdempotencyKey={IdempotencyKey} State={State}",
                    request.CorrelationId, request.ProcessKey, request.IdempotencyKey, process.State);
                return process;
            }
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await ExecuteStepAsync(process, request);

            sw.Stop();
            _metrics.RecordStepLatency(request.Event.ToString(), sw.Elapsed.TotalMilliseconds);
            _metrics.IncrementSuccessfulTransitions(request.Event.ToString());

            // Mark idempotency key only after successful execution.
            lock (process)
            {
                process.ProcessedIdempotencyKeys.Add(request.IdempotencyKey);
            }
        }
        catch (StepFailedException ex)
        {
            sw.Stop();
            _metrics.RecordStepLatency(request.Event.ToString(), sw.Elapsed.TotalMilliseconds);
            _metrics.IncrementFailedTransitions(request.Event.ToString());

            _logger.LogError(
                "[{CorrelationId}] Step '{Event}' failed on ProcessKey={ProcessKey}: {Reason}. Initiating compensation.",
                request.CorrelationId, request.Event, request.ProcessKey, ex.Message);

            await CompensateAsync(process, request.CorrelationId, ex.Message);
            throw;
        }

        return process;
    }

    // ─── Private helpers ──────────────────────────────────────────────────────

    private async Task ExecuteStepAsync(BookingProcess process, TransitionRequest req)
    {
        // Validate transition against the current state.
        var (expectedState, nextState) = req.Event switch
        {
            BookingEvent.ReserveRoom      => (BookingState.Created,          BookingState.RoomReserved),
            BookingEvent.SendNotification => (BookingState.RoomReserved,     BookingState.NotificationSent),
            BookingEvent.Confirm          => (BookingState.NotificationSent, BookingState.Confirmed),
            BookingEvent.Cancel           => (process.State,                 BookingState.Cancelled),   // allowed from several states
            _ => throw new InvalidOperationException($"Unknown event: {req.Event}")
        };

        lock (process)
        {
            if (req.Event != BookingEvent.Cancel && process.State != expectedState)
                throw new InvalidOperationException(
                    $"Cannot apply {req.Event} in state {process.State}. Expected {expectedState}.");
        }

        // Simulate async work (e.g. external call).
        await Task.Delay(_simulatedLatencyMs.GetValueOrDefault(req.Event, 10));

        // Chaos: intentional failure for testing compensation.
        if (req.SimulateFailure)
            throw new StepFailedException($"Simulated failure in step '{req.Event}'.");

        // Commit state transition.
        lock (process)
        {
            var from = process.State;
            process.State     = nextState;
            process.UpdatedAt = DateTimeOffset.UtcNow;

            process.History.Add(new AuditEntry(
                Timestamp:       DateTimeOffset.UtcNow,
                CorrelationId:   req.CorrelationId,
                IdempotencyKey:  req.IdempotencyKey,
                FromState:       from,
                ToState:         nextState,
                EventName:       req.Event.ToString(),
                Note:            "Transition applied."));
        }

        _logger.LogInformation(
            "[{CorrelationId}] Transition applied. ProcessKey={ProcessKey} Event={Event} State={State}",
            req.CorrelationId, req.ProcessKey, req.Event, nextState);
    }

    /// <summary>
    /// Compensation path: rolls back to Cancelled and records the reason.
    /// </summary>
    private async Task CompensateAsync(BookingProcess process, string correlationId, string reason)
    {
        _metrics.IncrementCompensations();

        await Task.Delay(20); // simulate async rollback work

        lock (process)
        {
            var from = process.State;
            process.State     = BookingState.Cancelled;
            process.UpdatedAt = DateTimeOffset.UtcNow;

            process.History.Add(new AuditEntry(
                Timestamp:      DateTimeOffset.UtcNow,
                CorrelationId:  correlationId,
                IdempotencyKey: $"compensation-{correlationId}",
                FromState:      from,
                ToState:        BookingState.Cancelled,
                EventName:      "Compensate",
                Note:           $"Compensation triggered. Reason: {reason}"));
        }

        _logger.LogWarning(
            "[{CorrelationId}] Compensation executed. ProcessKey={ProcessKey} NewState=Cancelled Reason={Reason}",
            correlationId, process.ProcessKey, reason);
    }
}

/// <summary>Thrown when a step encounters a recoverable error that triggers compensation.</summary>
public sealed class StepFailedException : Exception
{
    public StepFailedException(string message) : base(message) { }
}
