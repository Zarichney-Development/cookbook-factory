using System.Net;
using System.Text.Json;

namespace Cookbook.Factory.Middleware;

public class ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
{
    public async Task Invoke(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, 
                "{Middleware}: Unhandled exception occurred while processing {Method} {Path}",
                nameof(ErrorHandlingMiddleware),
                context.Request.Method,
                context.Request.Path);

            // Only handle truly unexpected errors
            var error = new
            {
                Error = new
                {
                    Message = "An unexpected error occurred",
                    Type = ex.GetType().Name,
                    ex.Source,
                },
                Request = new
                {
                    Path = context.Request.Path.Value, context.Request.Method,
                },
                TraceId = context.TraceIdentifier
            };

            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            context.Response.ContentType = "application/json";
            
            await context.Response.WriteAsJsonAsync(error, new JsonSerializerOptions 
            { 
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
        }
    }
}