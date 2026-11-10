using System.Text.Json;
using System.Text.Json.Serialization;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OrderPayment.Application.Payments;

namespace OrderPayment.Api.Controllers;

[ApiController, Route("api/webhooks/payment")]
public sealed class PaymentWebhooksController(
    IWebhookSignatureVerifier signatures,
    IValidator<PaymentCallback> validator,
    IPaymentService service) : ControllerBase
{
    private const int MaxBytes = 65536;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    [HttpPost, AllowAnonymous, RequestSizeLimit(MaxBytes)]
    public async Task<ActionResult<WebhookResult>> Receive(
        [FromHeader(Name = "X-Payment-Signature")] string? suppliedSignature, CancellationToken ct)
    {
        if (!Request.HasJsonContentType()) return Problem(statusCode: 415, title: "Content-Type phải là application/json.");
        if (Request.ContentLength > MaxBytes) return Problem(statusCode: 413, title: "Webhook vượt quá 64 KiB.");
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await Request.Body.ReadAsync(buffer, ct)) != 0)
        {
            if (body.Length + count > MaxBytes) return Problem(statusCode: 413, title: "Webhook vượt quá 64 KiB.");
            await body.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        var bytes = body.ToArray();
        var signature = Request.Headers["X-Payment-Signature"];
        if (signature.Count != 1 || !signatures.Verify(bytes, suppliedSignature))
            return Problem(statusCode: 401, title: "Chữ ký webhook không hợp lệ.");

        PaymentCallback? callback;
        try { callback = JsonSerializer.Deserialize<PaymentCallback>(bytes, JsonOptions); }
        catch (JsonException) { return Problem(statusCode: 400, title: "Nội dung webhook không hợp lệ."); }
        if (callback is null) return Problem(statusCode: 400, title: "Nội dung webhook không hợp lệ.");
        await validator.ValidateAndThrowAsync(callback, ct);
        return Ok(await service.ApplyWebhookAsync(callback, ct));
    }
}
