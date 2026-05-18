using System.Net;
using System.Net.Http.Json;
using BookingStateMachine.Models;
using BookingStateMachine.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BookingStateMachine.Tests;

// ─── Shared WebApplicationFactory ─────────────────────────────────────────────

public sealed class BookingApiFactory : WebApplicationFactory<Program>
{
    // Reset the singleton repository between test classes by using a fresh scope.
}

// ─── Tests ────────────────────────────────────────────────────────────────────

public sealed class HappyPathTests : IClassFixture<BookingApiFactory>
{
    private readonly HttpClient _client;
    private static int _counter;

    public HappyPathTests(BookingApiFactory factory)
        => _client = factory.CreateClient();

    private static string UniqueKey() =>
        $"booking-{System.Threading.Interlocked.Increment(ref _counter)}";

    /// <summary>
    /// Scenario 1 – Happy path: Created → RoomReserved → NotificationSent → Confirmed
    /// </summary>
    [Fact]
    public async Task HappyPath_FourTransitions_ReachesConfirmed()
    {
        var key = UniqueKey();
        var corr = Guid.NewGuid().ToString();

        // Create
        var createResp = await _client.PostAsJsonAsync("/api/bookings",
            new CreateBookingRequest(key, corr));
        createResp.StatusCode.Should().Be(HttpStatusCode.Created);

        // ReserveRoom
        await TransitionAsync(key, BookingEvent.ReserveRoom, corr);

        // SendNotification
        await TransitionAsync(key, BookingEvent.SendNotification, corr);

        // Confirm
        var status = await TransitionAsync(key, BookingEvent.Confirm, corr);

        status!.State.Should().Be(BookingState.Confirmed);
        status.History.Should().HaveCount(3);
    }

    private async Task<BookingStatusResponse?> TransitionAsync(
        string processKey, BookingEvent @event, string corr, bool simulateFailure = false)
    {
        var req = new TransitionRequest(
            ProcessKey:      processKey,
            Event:           @event,
            IdempotencyKey:  Guid.NewGuid().ToString(),
            CorrelationId:   corr,
            SimulateFailure: simulateFailure);

        var resp = await _client.PostAsJsonAsync($"/api/bookings/{processKey}/transitions", req);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<BookingStatusResponse>();
    }
}

public sealed class IdempotencyTests : IClassFixture<BookingApiFactory>
{
    private readonly HttpClient _client;
    private static int _counter = 100;

    public IdempotencyTests(BookingApiFactory factory)
        => _client = factory.CreateClient();

    private static string UniqueKey() =>
        $"idm-{System.Threading.Interlocked.Increment(ref _counter)}";

    /// <summary>
    /// Scenario 2 – Same IdempotencyKey delivered twice must NOT change state twice.
    /// </summary>
    [Fact]
    public async Task DuplicateIdempotencyKey_DoesNotChangeStateTwice()
    {
        var key  = UniqueKey();
        var corr = Guid.NewGuid().ToString();
        var ikey = Guid.NewGuid().ToString();   // same key for both deliveries

        await _client.PostAsJsonAsync("/api/bookings",
            new CreateBookingRequest(key, corr));

        var req = new TransitionRequest(key, BookingEvent.ReserveRoom, ikey, corr);

        // First delivery
        var r1 = await _client.PostAsJsonAsync($"/api/bookings/{key}/transitions", req);
        r1.EnsureSuccessStatusCode();
        var s1 = await r1.Content.ReadFromJsonAsync<BookingStatusResponse>();

        // Second delivery (duplicate)
        var r2 = await _client.PostAsJsonAsync($"/api/bookings/{key}/transitions", req);
        r2.EnsureSuccessStatusCode();
        var s2 = await r2.Content.ReadFromJsonAsync<BookingStatusResponse>();

        // State must still be RoomReserved (not Created again)
        s1!.State.Should().Be(BookingState.RoomReserved);
        s2!.State.Should().Be(BookingState.RoomReserved);
        // Only one history entry — duplicate did not add another
        s2.History.Should().HaveCount(1);
    }
}

public sealed class CompensationTests : IClassFixture<BookingApiFactory>
{
    private readonly HttpClient _client;
    private static int _counter = 200;

    public CompensationTests(BookingApiFactory factory)
        => _client = factory.CreateClient();

    private static string UniqueKey() =>
        $"comp-{System.Threading.Interlocked.Increment(ref _counter)}";

    /// <summary>
    /// Scenario 3 – Failure in SendNotification triggers compensation → state becomes Cancelled.
    /// </summary>
    [Fact]
    public async Task FailedSendNotification_TriggersCompensation_StateIsCancelled()
    {
        var key  = UniqueKey();
        var corr = Guid.NewGuid().ToString();

        await _client.PostAsJsonAsync("/api/bookings",
            new CreateBookingRequest(key, corr));

        // Step 1 – succeed
        await _client.PostAsJsonAsync($"/api/bookings/{key}/transitions",
            new TransitionRequest(key, BookingEvent.ReserveRoom,
                Guid.NewGuid().ToString(), corr));

        // Step 2 – fail intentionally
        var failResp = await _client.PostAsJsonAsync($"/api/bookings/{key}/transitions",
            new TransitionRequest(key, BookingEvent.SendNotification,
                Guid.NewGuid().ToString(), corr, SimulateFailure: true));

        failResp.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        // The process should now be Cancelled (compensation ran)
        var statusResp = await _client.GetFromJsonAsync<BookingStatusResponse>(
            $"/api/bookings/{key}");

        statusResp!.State.Should().Be(BookingState.Cancelled);
        statusResp.History.Should().Contain(h => h.EventName == "Compensate");
    }
}

public sealed class HealthCheckTests : IClassFixture<BookingApiFactory>
{
    private readonly HttpClient _client;

    public HealthCheckTests(BookingApiFactory factory)
        => _client = factory.CreateClient();

    [Fact]
    public async Task LivenessEndpoint_ReturnsHealthy()
    {
        var resp = await _client.GetAsync("/health/live");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ReadinessEndpoint_ReturnsHealthy_WhenNoProcesses()
    {
        var resp = await _client.GetAsync("/health/ready");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

public sealed class InvalidTransitionTests : IClassFixture<BookingApiFactory>
{
    private readonly HttpClient _client;
    private static int _counter = 300;

    public InvalidTransitionTests(BookingApiFactory factory)
        => _client = factory.CreateClient();

    private static string UniqueKey() =>
        $"inv-{System.Threading.Interlocked.Increment(ref _counter)}";

    /// <summary>
    /// Scenario 4 – Applying Confirm directly after Created (skipping steps) must be rejected.
    /// </summary>
    [Fact]
    public async Task InvalidTransition_OutOfOrder_Returns422()
    {
        var key  = UniqueKey();
        var corr = Guid.NewGuid().ToString();

        await _client.PostAsJsonAsync("/api/bookings",
            new CreateBookingRequest(key, corr));

        // Try to Confirm without going through the prior states
        var resp = await _client.PostAsJsonAsync($"/api/bookings/{key}/transitions",
            new TransitionRequest(key, BookingEvent.Confirm,
                Guid.NewGuid().ToString(), corr));

        resp.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    /// <summary>
    /// Scenario 5 – Transition on non-existent process returns 404.
    /// </summary>
    [Fact]
    public async Task Transition_UnknownProcess_Returns404()
    {
        var resp = await _client.PostAsJsonAsync("/api/bookings/ghost-process/transitions",
            new TransitionRequest("ghost-process", BookingEvent.ReserveRoom,
                Guid.NewGuid().ToString(), Guid.NewGuid().ToString()));

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
