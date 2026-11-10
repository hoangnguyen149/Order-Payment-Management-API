using FluentValidation;
using OrderPayment.Application.Abstractions;
using OrderPayment.Application.Contracts;
using OrderPayment.Domain;

namespace OrderPayment.Application.Services;

public sealed class CustomerService(ICustomerRepository repository, IValidator<CustomerRequest> validator, IValidator<PageRequest> pageValidator) : ICustomerService
{
    public async Task<PageResult<CustomerResponse>> ListAsync(PageRequest query, CancellationToken ct)
    {
        await pageValidator.ValidateAndThrowAsync(query, ct);
        QueryRules.ValidateSort(query.Sort, "fullName", "email");
        return (await repository.ListAsync(query, ct)).MapPage(x => x.ToResponse());
    }
    public async Task<CustomerResponse> GetAsync(Guid id, CancellationToken ct) => (await FindAsync(id, ct)).ToResponse();
    public async Task<CustomerResponse> CreateAsync(CustomerRequest request, CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(request, ct);
        var customer = new Customer(request.FullName, request.Email, request.Phone);
        await repository.AddAsync(customer, ct);
        return customer.ToResponse();
    }
    public async Task<CustomerResponse> UpdateAsync(Guid id, CustomerRequest request, CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(request, ct);
        var customer = await FindAsync(id, ct);
        customer.Update(request.FullName, request.Email, request.Phone);
        await repository.UpdateAsync(customer, ct);
        return customer.ToResponse();
    }
    public async Task DeleteAsync(Guid id, CancellationToken ct) => await repository.DeleteAsync(await FindAsync(id, ct), ct);
    private async Task<Customer> FindAsync(Guid id, CancellationToken ct) =>
        await repository.GetAsync(id, ct) ?? throw new NotFoundException("Không tìm thấy khách hàng.");
}
