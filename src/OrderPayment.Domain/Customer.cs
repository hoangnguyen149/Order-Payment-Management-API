namespace OrderPayment.Domain;

public sealed class Customer
{
    private Customer() { }
    public Customer(string fullName, string email, string? phone)
    {
        Id = Guid.NewGuid();
        Update(fullName, email, phone);
    }
    public Guid Id { get; private set; }
    public string FullName { get; private set; } = string.Empty;
    public string Email { get; private set; } = string.Empty;
    public string? Phone { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public void Update(string fullName, string email, string? phone)
    {
        if (string.IsNullOrWhiteSpace(fullName) || string.IsNullOrWhiteSpace(email))
            throw new DomainRuleException("Tên và email khách hàng không được trống.");
        FullName = fullName.Trim();
        Email = email.Trim().ToLowerInvariant();
        Phone = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim();
    }
}
