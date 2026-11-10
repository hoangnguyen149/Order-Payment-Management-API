using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using OrderPayment.Api.Auth;
using OrderPayment.Api.Middleware;
using OrderPayment.Application;
using OrderPayment.Infrastructure;
using OrderPayment.Infrastructure.Persistence;
using OrderPayment.Infrastructure.Payments;
using OrderPayment.Infrastructure.Jobs;
using OrderPayment.Application.Payments;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using OrderPayment.Api;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Host.UseSerilog((_, _, logging) => logging
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    // Duplicate-key SQL exception messages can contain customer email; app logs safe error codes instead.
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Fatal)
    .Enrich.FromLogContext()
    .WriteTo.Console(new RenderedCompactJsonFormatter()));
builder.Services.AddControllers().AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = ctx =>
{
    ctx.ProblemDetails.Extensions["traceId"] = ctx.HttpContext.TraceIdentifier;
    ctx.ProblemDetails.Instance = ctx.HttpContext.Request.Path;
});
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Configure ConnectionStrings__DefaultConnection via environment or user secrets."));
builder.Services.AddPaymentInfrastructure(builder.Configuration);
builder.Services.AddJobInfrastructure(builder.Configuration);
builder.Services.AddOptions<PaymentOptions>().BindConfiguration("Payments").ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddSingleton<IWebhookSignatureVerifier>(sp =>
    new PaymentSignatureVerifier(sp.GetRequiredService<IOptions<PaymentOptions>>().Value.WebhookSecret));
builder.Services.AddOptions<JwtOptions>().BindConfiguration("Jwt").ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<AuthOptions>().BindConfiguration("Auth").ValidateDataAnnotations()
    .Validate(o => o.AdminUsername != o.StaffUsername, "Admin and Staff must have different usernames.").ValidateOnStart();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<JwtOptions>>((options, jwt) =>
    {
        var settings = jwt.Value;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = settings.Issuer,
            ValidateAudience = true, ValidAudience = settings.Audience,
            ValidateLifetime = true, ClockSkew = TimeSpan.FromSeconds(15),
            ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.SigningKey)),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256]
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
    .AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ =>
            new FixedWindowRateLimiterOptions { PermitLimit = builder.Configuration.GetValue("RateLimiting:GlobalPermitLimit", 120), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ =>
            new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.OnRejected = async (ctx, ct) =>
    {
        if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry))
            ctx.HttpContext.Response.Headers.RetryAfter = Math.Ceiling(retry.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        await ctx.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new()
        {
            HttpContext = ctx.HttpContext,
            ProblemDetails = new ProblemDetails { Status = 429, Title = "Quá nhiều yêu cầu. Vui lòng thử lại sau." }
        });
    };
});
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("sqlserver");
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.OperationFilter<PaymentsOpenApiFilter>();
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "Order & Payment Management API", Version = "v1" });
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT",
        Description = "Login tại /api/auth/login, sau đó nhập accessToken."
    });
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = []
    });
});

var app = builder.Build();
app.Use(async (context, next) =>
{
    // Chỉ nhận correlation ID dạng GUID; không phản chiếu chuỗi tuỳ ý từ client vào log/header.
    var supplied = context.Request.Headers["X-Correlation-ID"].ToString();
    context.TraceIdentifier = Guid.TryParse(supplied, out var id) ? id.ToString() : Guid.NewGuid().ToString();
    context.Response.Headers["X-Correlation-ID"] = context.TraceIdentifier;
    using var scope = app.Logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = context.TraceIdentifier });
    await next(context);
});
app.UseSerilogRequestLogging();
app.UseMiddleware<ExceptionMiddleware>();
app.UseStatusCodePages(async context => await context.HttpContext.RequestServices
    .GetRequiredService<IProblemDetailsService>().WriteAsync(new() { HttpContext = context.HttpContext }));
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
app.UseRouting();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health").AllowAnonymous();
app.MapGet("/health/live", () => Results.Ok(new { status = "alive" })).AllowAnonymous();

if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<OrderPaymentDbContext>().Database.MigrateAsync();
}
await app.RunAsync();

public partial class Program;
