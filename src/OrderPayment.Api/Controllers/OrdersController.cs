using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OrderPayment.Application.Abstractions;
using OrderPayment.Application.Contracts;

namespace OrderPayment.Api.Controllers;

[ApiController, Route("api/orders"), Authorize(Roles = "Admin,Staff")]
public sealed class OrdersController(IOrderService service) : ControllerBase
{
    [HttpGet]
    public Task<PageResult<OrderResponse>> List([FromQuery] OrderQuery query, CancellationToken ct) => service.ListAsync(query, ct);

    [HttpGet("{id:guid}")]
    public Task<OrderResponse> Get(Guid id, CancellationToken ct) => service.GetAsync(id, ct);

    [HttpPost]
    public async Task<ActionResult<OrderResponse>> Create(OrderRequest request, CancellationToken ct)
    {
        var response = await service.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = response.Id }, response);
    }

    [HttpPut("{id:guid}")]
    public Task<OrderResponse> Update(Guid id, OrderRequest request, CancellationToken ct) => service.UpdateAsync(id, request, ct);

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        await service.DeleteAsync(id, ct);
        return NoContent();
    }
}
