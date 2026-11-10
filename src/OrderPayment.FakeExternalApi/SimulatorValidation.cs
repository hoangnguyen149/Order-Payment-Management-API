using System.Net.Mail;
using System.Text.RegularExpressions;

namespace OrderPayment.FakeExternalApi;

internal static partial class SimulatorValidation
{
    [GeneratedRegex("^[A-Za-z0-9_-]{1,100}$", RegexOptions.CultureInvariant)]
    private static partial Regex ReferencePattern();
    public static bool ValidReference(string? reference) => reference is not null && ReferencePattern().IsMatch(reference);

    public static Dictionary<string, string[]> Payment(CreatePaymentRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (!ValidReference(request.PaymentReference)) errors["paymentReference"] = ["Use 1–100 letters, digits, hyphens or underscores."];
        if (request.Amount <= 0 || request.Amount > 9_999_999_999_999_999.99m || decimal.Round(request.Amount, 2) != request.Amount)
            errors["amount"] = ["Amount must be positive, fit decimal(18,2), and have at most two decimal places."];
        return errors;
    }

    public static Dictionary<string, string[]> Marketplace(MarketplaceWriteRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.ExternalId) || request.ExternalId.Length > 100 || request.ExternalId != request.ExternalId.Trim())
            errors["externalId"] = ["External ID is required with at most 100 characters and no surrounding whitespace."];
        if (string.IsNullOrWhiteSpace(request.CustomerName) || request.CustomerName.Trim().Length > 200)
            errors["customerName"] = ["Customer name is required with at most 200 characters."];
        if (string.IsNullOrWhiteSpace(request.CustomerEmail) || request.CustomerEmail.Length > 254
            || !MailAddress.TryCreate(request.CustomerEmail, out var mail) || mail.Address != request.CustomerEmail)
            errors["customerEmail"] = ["A valid email up to 254 characters is required."];
        if (request.Items is null || request.Items.Count is < 1 or > 100)
            errors["items"] = ["Supply 1–100 distinct product SKUs."];
        else
        {
            var skus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in request.Items)
            {
                if (item is null || string.IsNullOrWhiteSpace(item.Sku) || item.Sku.Trim().Length > 64
                    || item.Quantity is < 1 or > 10000 || !skus.Add(item.Sku.Trim()))
                {
                    errors["items"] = ["Each line needs a distinct SKU up to 64 characters and quantity from 1 to 10000."];
                    break;
                }
            }
        }
        return errors;
    }
}
