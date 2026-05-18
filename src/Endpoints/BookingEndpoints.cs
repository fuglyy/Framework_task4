using BookingStateMachine.Models;
using BookingStateMachine.Services;
using Microsoft.AspNetCore.Mvc;

namespace BookingStateMachine.Endpoints;

public static class BookingEndpoints
{
    public static IEndpointRouteBuilder MapBookingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/bookings")
                       .WithTags("Bookings");

        // POST /api/bookings — create a new booking process
        group.MapPost("/", CreateBooking)
             .WithName("CreateBooking")
             .WithSummary("Create a new booking process.");

        // POST /api/bookings/{processKey}/transitions — fire an event
        group.MapPost("/{processKey}/transitions", ApplyTransition)
             .WithName("ApplyTransition")
             .WithSummary("Apply a state-machine event to an existing process.");

        // GET /api/bookings/{processKey} — inspect state + audit trail
        group.MapGet("/{processKey}", GetStatus)
             .WithName("GetBookingStatus")
             .WithSummary("Get the current state and audit history of a process.");

        // GET /api/bookings — list all processes
        group.MapGet("/", ListAll)
             .WithName("ListAll")
             .WithSummary("List all booking processes (for testing).");

        return app;
    }

    // ─── Handlers ─────────────────────────────────────────────────────────────

    private static IResult CreateBooking(
        [FromBody] CreateBookingRequest request,
        BookingStateMachineService svc,
        ILogger<BookingStateMachineService> logger)
    {
        try
        {
            var process = svc.CreateProcess(request.ProcessKey, request.CorrelationId);
            return Results.Created($"/api/bookings/{process.ProcessKey}",
                new BookingStatusResponse(process.ProcessKey, process.State, process.History));
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new ErrorResponse(request.CorrelationId, ex.Message));
        }
    }

    private static async Task<IResult> ApplyTransition(
        string processKey,
        [FromBody] TransitionRequest request,
        BookingStateMachineService svc)
    {
        if (processKey != request.ProcessKey)
            return Results.BadRequest(new ErrorResponse(request.CorrelationId,
                "processKey in URL must match ProcessKey in body."));

        try
        {
            var process = await svc.ApplyAsync(request);
            return Results.Ok(new BookingStatusResponse(process.ProcessKey, process.State, process.History));
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(new ErrorResponse(request.CorrelationId, ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return Results.UnprocessableEntity(new ErrorResponse(request.CorrelationId, ex.Message));
        }
        catch (StepFailedException ex)
        {
            // Compensation was already executed; return 500 so the caller retries correctly.
            return Results.Problem(
                title:      "Step failed — compensation executed.",
                detail:     ex.Message,
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static IResult GetStatus(
        string processKey,
        BookingRepository repo)
    {
        var process = repo.Find(processKey);
        return process is null
            ? Results.NotFound(new ErrorResponse("-", $"Process '{processKey}' not found."))
            : Results.Ok(new BookingStatusResponse(process.ProcessKey, process.State, process.History));
    }

    private static IResult ListAll(BookingRepository repo)
    {
        var result = repo.All()
            .Select(p => new BookingStatusResponse(p.ProcessKey, p.State, p.History))
            .ToList();
        return Results.Ok(result);
    }
}
