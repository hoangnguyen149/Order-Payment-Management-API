using FluentValidation;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OrderPayment.Application.Payments;
using OrderPayment.Domain;

namespace OrderPayment.Api.Controllers;

[ApiController, Route("api/orders/{orderId:guid}/payments"), Authorize(Roles = "Admin,Staff")]
public sealed class PaymentsController(IPaymentService service, IValidator<PaymentCreateRequest> validator) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<PaymentResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<PaymentResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PaymentResponse>> Create(Guid orderId,
        [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey, CancellationToken ct)
    {
        var header = Request.Headers["Idempotency-Key"];
        var key = header.Count == 1 ? idempotencyKey ?? "" : "";
        await validator.ValidateAndThrowAsync(new PaymentCreateRequest(orderId, key), ct);
        var payment = await service.CreateAsync(orderId, key, ct);
        return payment.Status == PaymentStatus.Pending
            ? AcceptedAtAction(nameof(Get), new { orderId }, payment)
            : Ok(payment);
    }

    [HttpGet]
    public Task<IReadOnlyList<PaymentResponse>> Get(Guid orderId, CancellationToken ct) => service.GetAsync(orderId, ct);
}
