using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OrderPayment.Api.Auth;

namespace OrderPayment.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(TokenService tokens) : ControllerBase
{
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    [HttpPost("login")]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public ActionResult<TokenResponse> Login(LoginRequest request)
    {
        var token = tokens.Login(request);
        return token is null
            ? Problem(statusCode: 401, title: "Thông tin đăng nhập không hợp lệ.")
            : Ok(token);
    }
}
