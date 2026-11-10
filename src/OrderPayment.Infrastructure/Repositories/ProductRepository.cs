using Microsoft.EntityFrameworkCore;
using OrderPayment.Application.Abstractions;
using OrderPayment.Application.Contracts;
using OrderPayment.Domain;
using OrderPayment.Infrastructure.Persistence;

namespace OrderPayment.Infrastructure.Repositories;

internal sealed class ProductRepository(OrderPaymentDbContext db) : IProductRepository
{
    public async Task<PageResult<Product>> ListAsync(PageRequest query, CancellationToken ct)
    {
        var products = db.Products.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            products = products.Where(x => x.Name.Contains(search) || x.Sku.Contains(search));
        }
        var total = await products.CountAsync(ct);
        var ordered = (query.Sort?.ToLowerInvariant(), query.Descending) switch
        {
            ("price", false) => products.OrderBy(x => x.Price),
            ("price", true) => products.OrderByDescending(x => x.Price),
            ("stockquantity", false) => products.OrderBy(x => x.StockQuantity),
            ("stockquantity", true) => products.OrderByDescending(x => x.StockQuantity),
            ("sku", false) => products.OrderBy(x => x.Sku),
            ("sku", true) => products.OrderByDescending(x => x.Sku),
            (_, true) => products.OrderByDescending(x => x.Name),
            _ => products.OrderBy(x => x.Name)
        };
        var items = await ordered.ThenBy(x => x.Id).Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize).ToListAsync(ct);
        return new(items, total, query.Page, query.PageSize);
    }

    public Task<Product?> GetAsync(Guid id, CancellationToken ct) => db.Products.SingleOrDefaultAsync(x => x.Id == id, ct);

    public async Task AddAsync(Product product, CancellationToken ct)
    {
        db.Products.Add(product);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(Product product, CancellationToken ct)
    {
        db.Products.Update(product);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Product product, CancellationToken ct)
    {
        db.Products.Remove(product);
        await db.SaveChangesAsync(ct);
    }
}
