using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OrderPayment.Application.Contracts;
using OrderPayment.Application.Orders;

namespace OrderPayment.Api.Controllers;

[ApiController, Route("api/orders"), Authorize(Roles = "Admin,Staff")]
public sealed class OrderStatusesController(IOrderStatusService service) : ControllerBase
{
    [HttpPatch("{id:guid}/status")]
    [ProducesResponseType<OrderResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<OrderResponse> Transition(Guid id, OrderStatusRequest request, CancellationToken ct) =>
        service.TransitionAsync(id, request.Status, ct);
}
