using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OrderPayment.Application;
using OrderPayment.Application.Abstractions;
using OrderPayment.Application.Contracts;
using OrderPayment.Domain;
using OrderPayment.Infrastructure.Persistence;

namespace OrderPayment.Infrastructure.Repositories;

internal sealed class OrderRepository(OrderPaymentDbContext db) : IOrderRepository
{
    public async Task<PageResult<Order>> ListAsync(OrderQuery query, CancellationToken ct)
    {
        var orders = db.Orders.AsNoTracking();
        if (query.CustomerId is { } customerId) orders = orders.Where(x => x.CustomerId == customerId);
        if (query.Status is { } status) orders = orders.Where(x => x.Status == status);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            if (Guid.TryParse(query.Search.Trim(), out var orderId)) orders = orders.Where(x => x.Id == orderId);
            else orders = orders.Where(_ => false);
        }
        var total = await orders.CountAsync(ct);
        var ordered = (query.Sort?.ToLowerInvariant(), query.Descending) switch
        {
            ("totalamount", false) => orders.OrderBy(x => x.TotalAmount),
            ("totalamount", true) => orders.OrderByDescending(x => x.TotalAmount),
            ("status", false) => orders.OrderBy(x => x.Status),
            ("status", true) => orders.OrderByDescending(x => x.Status),
            (_, true) => orders.OrderByDescending(x => x.CreatedAt),
            _ => orders.OrderBy(x => x.CreatedAt)
        };
        var items = await ordered.ThenBy(x => x.Id).Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize).Include(x => x.Items).ToListAsync(ct);
        return new(items, total, query.Page, query.PageSize);
    }

    public Task<Order?> GetAsync(Guid id, CancellationToken ct) =>
        db.Orders.AsNoTracking().Include(x => x.Items).SingleOrDefaultAsync(x => x.Id == id, ct);

    public async Task<Order> CreateAsync(OrderRequest request, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await EnsureCustomerAsync(request.CustomerId, ct);
        var products = await LoadProductsAsync(request.Items.Select(x => x.ProductId), ct);
        var lines = ReserveItems(request.Items, products);
        var order = new Order(request.CustomerId, lines);
        db.Orders.Add(order);
        AddOrderOutbox("OrderCreated", order);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return order;
    }

    public async Task<Order> UpdatePendingAsync(Guid id, OrderRequest request, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await DatabaseLocks.AcquireOrderAsync(db, id, ct);
        var order = await LoadOrderAsync(id, ct);
        if (order.Status != OrderStatus.Pending) throw new ConflictException("Chỉ được sửa đơn đang Pending.");
        if (await db.Payments.AnyAsync(x => x.OrderId == id, ct))
            throw new ConflictException("Không được sửa đơn đã khởi tạo thanh toán.");
        await EnsureCustomerAsync(request.CustomerId, ct);
        var products = await LoadProductsAsync(order.Items.Select(x => x.ProductId)
            .Concat(request.Items.Select(x => x.ProductId)), ct);
        foreach (var line in order.Items) products[line.ProductId].Release(line.Quantity);
        var oldItems = order.Items.ToArray();
        order.ReplaceItems(request.CustomerId, ReserveItems(request.Items, products));
        db.OrderItems.RemoveRange(oldItems);
        db.OrderItems.AddRange(order.Items);
        // Buộc UPDATE kể cả khi tổng tiền không đổi: RowVersion chặn hai lần sửa cùng một đơn.
        db.Entry(order).Property(x => x.TotalAmount).IsModified = true;
        AddOrderOutbox("OrderUpdated", order);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return order;
    }

    public async Task CancelAsync(Guid id, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await DatabaseLocks.AcquireOrderAsync(db, id, ct);
        var order = await LoadOrderAsync(id, ct);
        if (order.Status == OrderStatus.Cancelled) return; // DELETE lặp lại không hoàn kho lần hai.
        if (order.Status != OrderStatus.Pending) throw new ConflictException("Chỉ được hủy đơn đang Pending.");
        var products = await LoadProductsAsync(order.Items.Select(x => x.ProductId), ct);
        foreach (var line in order.Items) products[line.ProductId].Release(line.Quantity);
        order.Cancel();
        AddOrderOutbox("OrderCancelled", order);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task<Order> LoadOrderAsync(Guid id, CancellationToken ct) =>
        await db.Orders.Include(x => x.Items).SingleOrDefaultAsync(x => x.Id == id, ct)
        ?? throw new NotFoundException("Không tìm thấy đơn hàng.");

    private async Task EnsureCustomerAsync(Guid id, CancellationToken ct)
    {
        if (!await db.Customers.AnyAsync(x => x.Id == id, ct))
            throw new NotFoundException("Không tìm thấy khách hàng.");
    }

    private async Task<Dictionary<Guid, Product>> LoadProductsAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var distinctIds = ids.Distinct().ToArray();
        var products = await db.Products.Where(x => distinctIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        if (products.Count != distinctIds.Length) throw new NotFoundException("Một hoặc nhiều sản phẩm không tồn tại.");
        return products;
    }

    private static List<OrderItem> ReserveItems(IEnumerable<OrderLineRequest> request, Dictionary<Guid, Product> products)
    {
        var lines = new List<OrderItem>();
        foreach (var line in request)
        {
            var product = products[line.ProductId];
            if (product.StockQuantity < line.Quantity) throw new ConflictException($"Sản phẩm {product.Sku} không đủ tồn kho.");
            product.Reserve(line.Quantity);
            lines.Add(new OrderItem(product.Id, product.Name, product.Price, line.Quantity));
        }
        return lines;
    }

    private void AddOrderOutbox(string type, Order order) => db.OutboxMessages.Add(new OutboxMessage(type,
        JsonSerializer.Serialize(new { order.Id, order.CustomerId, order.TotalAmount, Status = order.Status.ToString() })));
}
