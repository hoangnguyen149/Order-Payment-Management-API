using Microsoft.EntityFrameworkCore;
using OrderPayment.Application.Abstractions;
using OrderPayment.Application.Contracts;
using OrderPayment.Domain;
using OrderPayment.Infrastructure.Persistence;

namespace OrderPayment.Infrastructure.Repositories;

internal sealed class CustomerRepository(OrderPaymentDbContext db) : ICustomerRepository
{
    public async Task<PageResult<Customer>> ListAsync(PageRequest query, CancellationToken ct)
    {
        var customers = db.Customers.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            customers = customers.Where(x => x.FullName.Contains(search) || x.Email.Contains(search));
        }
        var total = await customers.CountAsync(ct);
        var ordered = (query.Sort?.ToLowerInvariant(), query.Descending) switch
        {
            ("email", false) => customers.OrderBy(x => x.Email),
            ("email", true) => customers.OrderByDescending(x => x.Email),
            (_, true) => customers.OrderByDescending(x => x.FullName),
            _ => customers.OrderBy(x => x.FullName)
        };
        var items = await ordered.ThenBy(x => x.Id).Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize).ToListAsync(ct);
        return new(items, total, query.Page, query.PageSize);
    }

    public Task<Customer?> GetAsync(Guid id, CancellationToken ct) => db.Customers.SingleOrDefaultAsync(x => x.Id == id, ct);

    public async Task AddAsync(Customer customer, CancellationToken ct)
    {
        db.Customers.Add(customer);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(Customer customer, CancellationToken ct)
    {
        db.Customers.Update(customer);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Customer customer, CancellationToken ct)
    {
        db.Customers.Remove(customer);
        await db.SaveChangesAsync(ct);
    }
}
