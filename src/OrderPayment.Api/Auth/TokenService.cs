using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace OrderPayment.Api.Auth;

// Chỉ bootstrap tài khoản demo từ secrets cấu hình. Production cần user store/rotation/lockout.
public sealed class TokenService
{
    private sealed record Account(string Username, string Role, string PasswordHash);
    private readonly Account[] _accounts;
    private readonly PasswordHasher<string> _hasher = new();
    private readonly JwtOptions _jwt;

    public TokenService(IOptions<JwtOptions> jwt, IOptions<AuthOptions> auth)
    {
        _jwt = jwt.Value;
        var options = auth.Value;
        _accounts =
        [
            new(options.AdminUsername, "Admin", _hasher.HashPassword(options.AdminUsername, options.AdminPassword)),
            new(options.StaffUsername, "Staff", _hasher.HashPassword(options.StaffUsername, options.StaffPassword))
        ];
    }

    public TokenResponse? Login(LoginRequest request)
    {
        var account = _accounts.FirstOrDefault(a => string.Equals(a.Username, request.Username, StringComparison.Ordinal));
        // Luôn chạy PBKDF2 để giảm chênh lệch thời gian khi username không tồn tại.
        var candidate = account ?? _accounts[0];
        var result = _hasher.VerifyHashedPassword(candidate.Username, candidate.PasswordHash, request.Password);
        if (account is null || result == PasswordVerificationResult.Failed) return null;

        var expires = DateTimeOffset.UtcNow.AddMinutes(_jwt.LifetimeMinutes);
        var token = new JwtSecurityToken(_jwt.Issuer, _jwt.Audience,
        [
            new(JwtRegisteredClaimNames.Sub, account.Username),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, account.Role)
        ], expires: expires.UtcDateTime,
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.SigningKey)), SecurityAlgorithms.HmacSha256));
        return new(new JwtSecurityTokenHandler().WriteToken(token), expires, account.Role);
    }
}
