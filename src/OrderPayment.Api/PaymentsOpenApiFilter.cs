using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi.Models;
using OrderPayment.Api.Controllers;
using OrderPayment.Application.Payments;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace OrderPayment.Api;

public sealed class PaymentsOpenApiFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (context.MethodInfo.GetCustomAttributes(true).OfType<IAllowAnonymous>().Any()
            || context.ApiDescription.ActionDescriptor.EndpointMetadata.OfType<IAllowAnonymous>().Any())
            operation.Security = [];

        if (context.MethodInfo.DeclaringType != typeof(PaymentWebhooksController)) return;
        // Body is read as bytes for HMAC, so ApiExplorer cannot infer its JSON contract.
        operation.RequestBody = new OpenApiRequestBody
        {
            Required = true,
            Description = "Ký chính xác raw UTF-8 body bằng HMAC-SHA256; gửi hex qua X-Payment-Signature.",
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/json"] = new()
                {
                    Schema = context.SchemaGenerator.GenerateSchema(typeof(PaymentCallback), context.SchemaRepository)
                }
            }
        };
    }
}
