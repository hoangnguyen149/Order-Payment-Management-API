using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace OrderPayment.FakeExternalApi;

public partial class FakeExternalProgram
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 64 * 1024);
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole();
        builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
            context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier);
        builder.Services.AddOptions<FakeOptions>().BindConfiguration("Fake").ValidateDataAnnotations()
            .Validate(options => Uri.TryCreate(options.ApiBaseUrl, UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo)
                && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment), "Fake:ApiBaseUrl must be an HTTP(S) base URI.")
            .ValidateOnStart();
        builder.Services.AddOptions<FakePaymentOptions>().BindConfiguration("Payments").ValidateDataAnnotations().ValidateOnStart();
        builder.Services.AddSingleton<SimulatorStore>();
        builder.Services.AddSingleton<SimulatorSecurity>();
        builder.Services.AddHttpClient<CallbackSender>((provider, client) =>
        {
            client.BaseAddress = new Uri(provider.GetRequiredService<IOptions<FakeOptions>>().Value.ApiBaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(3);
        });

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            try { await next(context); }
            catch (SimulatorConflictException exception) when (!context.Response.HasStarted)
            { await Results.Problem(statusCode: 409, title: exception.Message).ExecuteAsync(context); }
            catch (BadHttpRequestException exception) when (!context.Response.HasStarted)
            { await Results.Problem(statusCode: exception.StatusCode, title: "Invalid request body or query parameters.").ExecuteAsync(context); }
            catch (SqliteException) when (!context.Response.HasStarted)
            { await Results.Problem(statusCode: 503, title: "Simulator storage unavailable.").ExecuteAsync(context); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            { context.Response.StatusCode = 499; }
            catch (Exception exception) when (!context.Response.HasStarted)
            {
                app.Logger.LogError("Simulator error of type {ExceptionType}; trace {TraceId}", exception.GetType().Name, context.TraceIdentifier);
                await Results.Problem(statusCode: 500, title: "Simulator request failed.").ExecuteAsync(context);
            }
        });
        app.UseStatusCodePages(async context => await Results.Problem(statusCode: context.HttpContext.Response.StatusCode)
            .ExecuteAsync(context.HttpContext));
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/admin", StringComparison.OrdinalIgnoreCase)
                && !context.RequestServices.GetRequiredService<SimulatorSecurity>().Authorize(context.Request.Headers["X-Simulator-Key"].ToString()))
            {
                await Results.Problem(statusCode: 401, title: "A valid X-Simulator-Key is required.").ExecuteAsync(context);
                return;
            }
            await next(context);
        });

        app.MapPost("/payments", async (CreatePaymentRequest request, HttpContext context, SimulatorStore store, CancellationToken ct) =>
        {
            var errors = SimulatorValidation.Payment(request);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var existing = await store.GetPaymentAsync(request.PaymentReference, ct);
            if (existing is not null)
            {
                if (existing.Amount != request.Amount) return Results.Problem(statusCode: 409, title: "Payment reference is already bound to another amount.");
                return Results.Ok(existing.View());
            }
            if (app.Environment.IsDevelopment())
            {
                var mode = context.Request.Headers["X-Fake-Mode"].ToString();
                if (mode is "always500" || mode == "flaky" && await store.RecordAttemptAsync(request.PaymentReference, ct) == 1)
                    return Results.Problem(statusCode: 500, title: "Simulated gateway failure.");
                if (mode is "delay" or "timeout") await Task.Delay(mode == "timeout" ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(5), ct);
            }
            var payment = await store.CreatePaymentAsync(request.PaymentReference, request.Amount, ct);
            return Results.Ok(payment.View());
        });
        app.MapGet("/payments/{reference}", async (string reference, SimulatorStore store, CancellationToken ct) =>
        {
            if (!SimulatorValidation.ValidReference(reference)) return Results.Problem(statusCode: 400, title: "Invalid payment reference.");
            var payment = await store.GetPaymentAsync(reference, ct);
            return payment is null ? Results.NotFound() : Results.Ok(payment.View());
        });
        app.MapPost("/admin/payments/{reference}/settle", async (string reference, SettlePaymentRequest request,
            SimulatorStore store, CallbackSender callbacks, CancellationToken ct) =>
        {
            if (!SimulatorValidation.ValidReference(reference) || request.Status is not ("Succeeded" or "Failed"))
                return Results.Problem(statusCode: 400, title: "Use a valid reference and terminal status Succeeded or Failed.");
            var payment = await store.SettlePaymentAsync(reference, request.Status, ct);
            if (payment is null) return Results.NotFound();
            var delivered = await callbacks.SendAsync(payment, ct);
            return Results.Ok(new { payment.PaymentReference, payment.Status, payment.Amount, payment.EventId, webhookDelivered = delivered });
        });
        app.MapPost("/admin/payments/{reference}/callback", async (string reference, int? count,
            SimulatorStore store, CallbackSender callbacks, CancellationToken ct) =>
        {
            var attempts = count ?? 3;
            if (!SimulatorValidation.ValidReference(reference) || attempts is < 1 or > 5)
                return Results.Problem(statusCode: 400, title: "Use a valid reference and count from 1 to 5.");
            var payment = await store.GetPaymentAsync(reference, ct);
            if (payment is null) return Results.NotFound();
            if (payment.EventId is null) return Results.Problem(statusCode: 409, title: "Settle this payment before sending its callback.");
            var delivered = 0;
            for (var attempt = 0; attempt < attempts; attempt++)
                if (await callbacks.SendAsync(payment, ct)) delivered++;
            return Results.Ok(new { payment.PaymentReference, payment.EventId, attempted = attempts, delivered });
        });
        app.MapPost("/admin/marketplace/orders", async (MarketplaceWriteRequest request, SimulatorStore store, CancellationToken ct) =>
        {
            var errors = SimulatorValidation.Marketplace(request);
            return errors.Count > 0 ? Results.ValidationProblem(errors) : Results.Ok(await store.UpsertMarketplaceAsync(request, ct));
        });
        app.MapGet("/marketplace/orders", async (DateTimeOffset since, DateTimeOffset until, string? cursor, int? pageSize,
            SimulatorStore store, SimulatorSecurity security, CancellationToken ct) =>
        {
            var take = pageSize ?? 100;
            if (since > until || take is < 1 or > 100) return Results.Problem(statusCode: 400, title: "Require since <= until and pageSize from 1 to 100.");
            var state = cursor is null ? null : security.DecodeCursor(cursor, since, until);
            if (cursor is not null && state is null) return Results.Problem(statusCode: 400, title: "Invalid cursor or changed time bounds.");
            var result = await store.ReadMarketplaceAsync(since, until, state, take, ct);
            var nextCursor = result.NextRevision is { } revision
                ? security.EncodeCursor(new(result.SnapshotRevision, revision, since.UtcTicks, until.UtcTicks)) : null;
            return Results.Ok(new MarketplacePage(result.Orders, nextCursor));
        });
        app.MapGet("/health", async (SimulatorStore store, CancellationToken ct) =>
        {
            await store.CheckAsync(ct);
            return Results.Ok(new { status = "healthy" });
        });
        app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));

        await app.Services.GetRequiredService<SimulatorStore>().InitializeAsync(app.Lifetime.ApplicationStopping);
        await app.RunAsync();
    }
}
