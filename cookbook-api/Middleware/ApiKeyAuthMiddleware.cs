using System.Net;
using Cookbook.Factory.Services;
using ILogger = Serilog.ILogger;

namespace Cookbook.Factory.Middleware;

public class ApiKeyAuthMiddleware(RequestDelegate next,
    ILogger logger,
    ApiKeyConfig apiKeyConfig)
{
    private const string ApiKeyHeader = "X-Api-Key";

    // Paths that don't require authentication
    private static readonly HashSet<string> BypassPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/factory/health"
    };

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        // Check if the path should bypass authentication
        if (BypassPaths.Any(bypassPath => path.Equals(bypassPath, StringComparison.OrdinalIgnoreCase) ||
                                          path.Contains("swagger", StringComparison.OrdinalIgnoreCase)))
        {
            await next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue(ApiKeyHeader, out var extractedApiKey))
        {
            logger.Warning("Request missing API key header for path: {Path}", path);
            context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "API key is required",
                path,
                timestamp = DateTimeOffset.UtcNow
            });
            return;
        }

        if (!apiKeyConfig.ValidApiKeys.Contains(extractedApiKey.ToString()))
        {
            logger.Warning("Invalid API key attempted for path: {Path}", path);
            context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "Invalid API key",
                path,
                timestamp = DateTimeOffset.UtcNow
            });
            return;
        }

        // Add the API key to the HttpContext items for potential use in controllers
        context.Items["ApiKey"] = extractedApiKey.ToString();

        await next(context);
    }
}

public static class ApiKeyAuthMiddlewareExtensions
{
    public static IApplicationBuilder UseApiKeyAuth(
        this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<ApiKeyAuthMiddleware>();
    }
}