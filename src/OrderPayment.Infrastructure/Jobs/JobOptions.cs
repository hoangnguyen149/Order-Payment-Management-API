using System.ComponentModel.DataAnnotations;

namespace OrderPayment.Infrastructure.Jobs;

public sealed class JobOptions
{
    public bool Enabled { get; set; } = true;
    [Range(1, 3600)] public int PollSeconds { get; set; } = 30;
    [Range(1, 1440)] public int PendingPaymentMinutes { get; set; } = 2;
    [Range(1, 86400)] public int MarketplaceIntervalSeconds { get; set; } = 120;
}

public sealed class MarketplaceOptions
{
    [Required] public string BaseUrl { get; set; } = "http://localhost:8081";
}
