using FluentValidation;
using OrderPayment.Application.Abstractions;
using OrderPayment.Application.Contracts;
using OrderPayment.Domain;

namespace OrderPayment.Application.Services;

public sealed class ProductService(IProductRepository repository, IValidator<ProductRequest> validator, IValidator<PageRequest> pageValidator) : IProductService
{
    public async Task<PageResult<ProductResponse>> ListAsync(PageRequest query, CancellationToken ct)
    {
        await pageValidator.ValidateAndThrowAsync(query, ct);
        QueryRules.ValidateSort(query.Sort, "name", "sku", "price", "stockQuantity");
        return (await repository.ListAsync(query, ct)).MapPage(x => x.ToResponse());
    }
    public async Task<ProductResponse> GetAsync(Guid id, CancellationToken ct) => (await FindAsync(id, ct)).ToResponse();
    public async Task<ProductResponse> CreateAsync(ProductRequest request, CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(request, ct);
        var product = new Product(request.Name, request.Sku, request.Price, request.StockQuantity);
        await repository.AddAsync(product, ct);
        return product.ToResponse();
    }
    public async Task<ProductResponse> UpdateAsync(Guid id, ProductRequest request, CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(request, ct);
        var product = await FindAsync(id, ct);
        product.Update(request.Name, request.Sku, request.Price, request.StockQuantity);
        await repository.UpdateAsync(product, ct);
        return product.ToResponse();
    }
    public async Task DeleteAsync(Guid id, CancellationToken ct) => await repository.DeleteAsync(await FindAsync(id, ct), ct);
    private async Task<Product> FindAsync(Guid id, CancellationToken ct) =>
        await repository.GetAsync(id, ct) ?? throw new NotFoundException("Không tìm thấy sản phẩm.");
}
