using BookingStateMachine.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BookingStateMachine.HealthChecks;

/// <summary>
/// Liveness probe: returns Healthy as long as the process is running.
/// A load balancer can restart the pod if this fails.
/// </summary>
public sealed class LivenessHealthCheck : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
        => Task.FromResult(HealthCheckResult.Healthy("Service is alive."));
}

/// <summary>
/// Readiness probe: marks the service as not ready when too many processes
/// are stuck in a terminal-failure state (simulated critical degradation).
/// A load balancer stops routing traffic when this returns Unhealthy.
/// </summary>
public sealed class ReadinessHealthCheck : IHealthCheck
{
    private readonly BookingRepository _repo;
    private const int MaxCancelledRatio = 80; // percent

    public ReadinessHealthCheck(BookingRepository repo)
    {
        _repo = repo;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var all = _repo.All();
        if (!all.Any())
            return Task.FromResult(HealthCheckResult.Healthy("No processes yet."));

        var cancelledPct = all.Count(p => p.State == Models.BookingState.Cancelled) * 100 / all.Count;

        if (cancelledPct >= MaxCancelledRatio)
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"Critical degradation: {cancelledPct}% of processes are Cancelled."));

        return Task.FromResult(HealthCheckResult.Healthy(
            $"Ready. Cancelled={cancelledPct}% (threshold {MaxCancelledRatio}%)."));
    }
}
