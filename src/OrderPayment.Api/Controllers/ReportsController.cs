using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OrderPayment.Application.Abstractions;
using OrderPayment.Application.Contracts;

namespace OrderPayment.Api.Controllers;

[ApiController, Route("api/reports"), Authorize(Roles = "Admin")]
public sealed class ReportsController(IRevenueReportService service) : ControllerBase
{
    /// <summary>Doanh thu đơn Paid/Shipped/Completed theo ngày tạo đơn UTC; chỉ trả các ngày có dữ liệu.</summary>
    [HttpGet("revenue/daily")]
    [ProducesResponseType<IReadOnlyList<RevenueDayResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public Task<IReadOnlyList<RevenueDayResponse>> DailyRevenue([FromQuery] RevenueQuery query, CancellationToken ct) =>
        service.GetDailyAsync(query, ct);
}
