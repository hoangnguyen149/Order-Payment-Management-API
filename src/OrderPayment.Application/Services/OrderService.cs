using FluentValidation;
using OrderPayment.Application.Abstractions;
using OrderPayment.Application.Contracts;

namespace OrderPayment.Application.Services;

public sealed class OrderService(IOrderRepository repository, IValidator<OrderRequest> validator, IValidator<OrderQuery> queryValidator) : IOrderService
{
    public async Task<PageResult<OrderResponse>> ListAsync(OrderQuery query, CancellationToken ct)
    {
        await queryValidator.ValidateAndThrowAsync(query, ct);
        QueryRules.ValidateSort(query.Sort, "createdAt", "totalAmount", "status");
        return (await repository.ListAsync(query, ct)).MapPage(x => x.ToResponse());
    }
    public async Task<OrderResponse> GetAsync(Guid id, CancellationToken ct) =>
        (await repository.GetAsync(id, ct) ?? throw new NotFoundException("Không tìm thấy đơn hàng.")).ToResponse();
    public async Task<OrderResponse> CreateAsync(OrderRequest request, CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(request, ct);
        return (await repository.CreateAsync(request, ct)).ToResponse();
    }
    public async Task<OrderResponse> UpdateAsync(Guid id, OrderRequest request, CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(request, ct);
        return (await repository.UpdatePendingAsync(id, request, ct)).ToResponse();
    }
    public async Task DeleteAsync(Guid id, CancellationToken ct) => await repository.CancelAsync(id, ct);
}
