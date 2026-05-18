using System.Diagnostics.Metrics;

namespace BookingStateMachine.Metrics;

/// <summary>
/// Custom metrics for the booking state machine.
/// All counters and histograms are exposed via OpenTelemetry / Prometheus.
///
/// Counters:
///   booking.transitions.success   {event}   - successful state transitions
///   booking.transitions.failure   {event}   - failed step attempts
///   booking.deliveries.duplicate            - re-delivered idempotency keys ignored
///   booking.compensations                   - compensation executions
///
/// Histograms:
///   booking.step.duration_ms      {event}   - step wall-clock time in ms
/// </summary>
public sealed class BookingMetrics : IDisposable
{
    public const string MeterName = "BookingStateMachine";

    private readonly Meter _meter;
    private readonly Counter<long> _successCounter;
    private readonly Counter<long> _failureCounter;
    private readonly Counter<long> _duplicateCounter;
    private readonly Counter<long> _compensationCounter;
    private readonly Histogram<double> _stepDurationHistogram;

    public BookingMetrics(IMeterFactory meterFactory)
    {
        _meter = meterFactory.Create(MeterName);

        _successCounter = _meter.CreateCounter<long>(
            name:        "booking.transitions.success",
            unit:        "{transitions}",
            description: "Number of successful state-machine transitions.");

        _failureCounter = _meter.CreateCounter<long>(
            name:        "booking.transitions.failure",
            unit:        "{transitions}",
            description: "Number of failed step executions (before compensation).");

        _duplicateCounter = _meter.CreateCounter<long>(
            name:        "booking.deliveries.duplicate",
            unit:        "{deliveries}",
            description: "Number of duplicate event deliveries suppressed by idempotency.");

        _compensationCounter = _meter.CreateCounter<long>(
            name:        "booking.compensations",
            unit:        "{compensations}",
            description: "Number of compensation actions executed.");

        _stepDurationHistogram = _meter.CreateHistogram<double>(
            name:        "booking.step.duration_ms",
            unit:        "ms",
            description: "Wall-clock time of each processing step in milliseconds.");
    }

    public void IncrementSuccessfulTransitions(string eventName)
        => _successCounter.Add(1, new KeyValuePair<string, object?>("event", eventName));

    public void IncrementFailedTransitions(string eventName)
        => _failureCounter.Add(1, new KeyValuePair<string, object?>("event", eventName));

    public void IncrementDuplicateDeliveries()
        => _duplicateCounter.Add(1);

    public void IncrementCompensations()
        => _compensationCounter.Add(1);

    public void RecordStepLatency(string eventName, double durationMs)
        => _stepDurationHistogram.Record(durationMs,
               new KeyValuePair<string, object?>("event", eventName));

    public void Dispose() => _meter.Dispose();
}
