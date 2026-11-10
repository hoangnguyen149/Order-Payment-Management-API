using FluentValidation;
using FluentValidation.Results;

namespace OrderPayment.Application.Services;

internal static class QueryRules
{
    public static void ValidateSort(string? sort, params string[] allowed)
    {
        if (!string.IsNullOrEmpty(sort) && !allowed.Contains(sort, StringComparer.OrdinalIgnoreCase))
            throw new ValidationException([new ValidationFailure("Sort", $"Sort phải là một trong: {string.Join(", ", allowed)}.")]);
    }
}
