using System.ComponentModel.DataAnnotations;

namespace OrderPayment.Api.Auth;

public sealed class JwtOptions
{
    [Required, MinLength(32)] public string SigningKey { get; set; } = "";
    [Required] public string Issuer { get; set; } = "OrderPayment";
    [Required] public string Audience { get; set; } = "OrderPayment.Client";
    [Range(1, 120)] public int LifetimeMinutes { get; set; } = 30;
}

public sealed class AuthOptions
{
    [Required] public string AdminUsername { get; set; } = "admin";
    [Required, MinLength(12)] public string AdminPassword { get; set; } = "";
    [Required] public string StaffUsername { get; set; } = "staff";
    [Required, MinLength(12)] public string StaffPassword { get; set; } = "";
}

public sealed record LoginRequest(
    [Required, MaxLength(100)] string Username,
    [Required, MaxLength(200)] string Password);

public sealed record TokenResponse(string AccessToken, DateTimeOffset ExpiresAt, string Role);
