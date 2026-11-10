namespace OrderPayment.Domain;

public enum OrderStatus { Pending, Paid, Shipped, Completed, Cancelled, PaymentFailed }

public sealed class Order
{
    private List<OrderItem> _items = [];
    private Order() { }
    public Order(Guid customerId, IEnumerable<OrderItem> items)
    {
        if (customerId == Guid.Empty) throw new DomainRuleException("Khách hàng không hợp lệ.");
        Id = Guid.NewGuid();
        CustomerId = customerId;
        CreatedAt = DateTimeOffset.UtcNow;
        Status = OrderStatus.Pending;
        ReplaceItems(customerId, items);
    }

    public Guid Id { get; private set; }
    public Guid CustomerId { get; private set; }
    public string? ExternalMarketplaceId { get; private set; }
    public DateTimeOffset? MarketplaceUpdatedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public OrderStatus Status { get; private set; }
    public decimal TotalAmount { get; private set; }
    public IReadOnlyList<OrderItem> Items => _items.AsReadOnly();
    public byte[] RowVersion { get; private set; } = [];

    public void ReplaceItems(Guid customerId, IEnumerable<OrderItem> items)
    {
        if (Status != OrderStatus.Pending)
            throw new DomainRuleException("Chỉ được sửa đơn đang Pending.");
        if (customerId == Guid.Empty) throw new DomainRuleException("Khách hàng không hợp lệ.");
        var lines = items.ToList();
        if (lines.Count is < 1 or > 100 || lines.Select(x => x.ProductId).Distinct().Count() != lines.Count)
            throw new DomainRuleException("Đơn phải có 1–100 sản phẩm khác nhau.");
        CustomerId = customerId;
        foreach (var line in lines) line.AssignToOrder(Id);
        _items = lines;
        // Chụp giá từng dòng tại thời điểm đặt; không nhận tổng tiền từ client.
        TotalAmount = lines.Sum(x => x.LineTotal);
    }

    public void ChangeStatus(OrderStatus next)
    {
        var allowed = (Status, next) switch
        {
            (OrderStatus.Pending, OrderStatus.Paid or OrderStatus.Cancelled or OrderStatus.PaymentFailed) => true,
            (OrderStatus.Paid, OrderStatus.Shipped) => true,
            (OrderStatus.Shipped, OrderStatus.Completed) => true,
            _ => false
        };
        if (!allowed) throw new DomainRuleException($"Không thể chuyển đơn từ {Status} sang {next}.");
        Status = next;
    }

    public void AssignMarketplaceId(string externalId, DateTimeOffset? updatedAt = null)
    {
        if (string.IsNullOrWhiteSpace(externalId) || externalId.Length > 100 || externalId != externalId.Trim())
            throw new DomainRuleException("Mã đơn Marketplace không hợp lệ.");
        if (ExternalMarketplaceId is not null && ExternalMarketplaceId != externalId)
            throw new DomainRuleException("Không được thay đổi mã đơn Marketplace đã gán.");
        ExternalMarketplaceId = externalId;
        if (updatedAt.HasValue) MarkMarketplaceUpdated(updatedAt.Value);
    }

    public void MarkMarketplaceUpdated(DateTimeOffset updatedAt)
    {
        if (ExternalMarketplaceId is null || (MarketplaceUpdatedAt.HasValue && updatedAt < MarketplaceUpdatedAt.Value))
            throw new DomainRuleException("Thứ tự cập nhật Marketplace không hợp lệ.");
        MarketplaceUpdatedAt = updatedAt.ToUniversalTime();
    }

    public void Cancel() => ChangeStatus(OrderStatus.Cancelled);
}

public sealed class OrderItem
{
    private OrderItem() { }
    public OrderItem(Guid productId, string productName, decimal unitPrice, int quantity)
    {
        if (productId == Guid.Empty || string.IsNullOrWhiteSpace(productName))
            throw new DomainRuleException("Sản phẩm không hợp lệ.");
        if (unitPrice <= 0 || unitPrice > 1_000_000_000m || decimal.Round(unitPrice, 2) != unitPrice)
            throw new DomainRuleException("Giá dòng đơn không hợp lệ.");
        if (quantity is < 1 or > 10000) throw new DomainRuleException("Số lượng phải từ 1 đến 10000.");
        Id = Guid.NewGuid();
        ProductId = productId;
        ProductName = productName;
        UnitPrice = unitPrice;
        Quantity = quantity;
    }
    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid ProductId { get; private set; }
    public string ProductName { get; private set; } = string.Empty;
    public decimal UnitPrice { get; private set; }
    public int Quantity { get; private set; }
    public decimal LineTotal => UnitPrice * Quantity;
    internal void AssignToOrder(Guid orderId) => OrderId = orderId;
}
