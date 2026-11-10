using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OrderPayment.Application.Abstractions;
using OrderPayment.Application.Contracts;

namespace OrderPayment.Api.Controllers;

[ApiController, Route("api/customers"), Authorize(Roles = "Admin,Staff")]
public sealed class CustomersController(ICustomerService service) : ControllerBase
{
    [HttpGet]
    public Task<PageResult<CustomerResponse>> List([FromQuery] PageRequest query, CancellationToken ct) => service.ListAsync(query, ct);

    [HttpGet("{id:guid}")]
    public Task<CustomerResponse> Get(Guid id, CancellationToken ct) => service.GetAsync(id, ct);

    [HttpPost, Authorize(Roles = "Admin")]
    public async Task<ActionResult<CustomerResponse>> Create(CustomerRequest request, CancellationToken ct)
    {
        var response = await service.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = response.Id }, response);
    }

    [HttpPut("{id:guid}"), Authorize(Roles = "Admin")]
    public Task<CustomerResponse> Update(Guid id, CustomerRequest request, CancellationToken ct) => service.UpdateAsync(id, request, ct);

    [HttpDelete("{id:guid}"), Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await service.DeleteAsync(id, ct);
        return NoContent();
    }
}
