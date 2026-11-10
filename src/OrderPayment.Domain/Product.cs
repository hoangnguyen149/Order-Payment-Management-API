namespace OrderPayment.Domain;

public sealed class Product
{
    private Product() { }

    public Product(string name, string sku, decimal price, int stockQuantity)
    {
        Id = Guid.NewGuid();
        Update(name, sku, price, stockQuantity);
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Sku { get; private set; } = string.Empty;
    public decimal Price { get; private set; }
    public int StockQuantity { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public void Update(string name, string sku, decimal price, int stockQuantity)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(sku))
            throw new DomainRuleException("Tên và SKU sản phẩm không được trống.");
        if (price <= 0 || price > 1_000_000_000m || decimal.Round(price, 2) != price)
            throw new DomainRuleException("Giá phải lớn hơn 0, tối đa 1 tỷ và có tối đa 2 chữ số thập phân.");
        if (stockQuantity < 0) throw new DomainRuleException("Tồn kho không được âm.");
        Name = name.Trim();
        Sku = sku.Trim().ToUpperInvariant();
        Price = price;
        StockQuantity = stockQuantity;
    }

    public void Reserve(int quantity)
    {
        if (quantity <= 0) throw new DomainRuleException("Số lượng phải lớn hơn 0.");
        if (StockQuantity < quantity) throw new DomainRuleException($"Sản phẩm {Sku} không đủ tồn kho.");
        StockQuantity -= quantity;
    }

    public void Release(int quantity)
    {
        if (quantity <= 0 || quantity > int.MaxValue - StockQuantity)
            throw new DomainRuleException("Số lượng hoàn kho không hợp lệ.");
        StockQuantity += quantity;
    }
}
