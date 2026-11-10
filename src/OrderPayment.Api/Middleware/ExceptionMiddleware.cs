using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OrderPayment.Application;
using OrderPayment.Domain;

namespace OrderPayment.Api.Middleware;

public sealed class ExceptionMiddleware(RequestDelegate next, ILogger<ExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, IProblemDetailsService problems)
    {
        try { await next(context); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            context.Response.StatusCode = 499;
        }
        catch (Exception ex) when (!context.Response.HasStarted)
        {
            var (status, title) = ex switch
            {
                BadHttpRequestException bad => (bad.StatusCode, "Yêu cầu HTTP không hợp lệ."),
                ValidationException => (400, "Dữ liệu đầu vào không hợp lệ."),
                NotFoundException => (404, ex.Message),
                ConflictException or DomainRuleException => (409, ex.Message),
                DbUpdateConcurrencyException => (409, "Dữ liệu vừa thay đổi. Hãy đọc lại và thử lại."),
                DbUpdateException { InnerException: SqlException { Number: 2601 or 2627 } } => (409, "Mã SKU hoặc email đã tồn tại."),
                DbUpdateException { InnerException: SqlException { Number: 547 } } => (409, "Dữ liệu đang được tham chiếu hoặc tham chiếu không còn tồn tại."),
                _ => (500, "Đã xảy ra lỗi hệ thống.")
            };
            // Không log payload, exception message SQL hoặc config để tránh lộ thông tin khách hàng/secrets.
            if (status == 500) logger.LogError("Unhandled error of type {ExceptionType}; trace {TraceId}", ex.GetType().Name, context.TraceIdentifier);
            var detail = new ProblemDetails();
            // IProblemDetailsService ghi theo kiểu ProblemDetails; extension giữ đầy đủ errors của FluentValidation.
            if (ex is ValidationException validation)
                detail.Extensions["errors"] = validation.Errors.GroupBy(e => e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).Distinct().ToArray());
            detail.Status = status;
            detail.Title = title;
            context.Response.StatusCode = status;
            await problems.WriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = detail });
        }
    }
}
