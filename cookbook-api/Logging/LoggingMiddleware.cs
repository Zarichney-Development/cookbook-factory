using Microsoft.Extensions.Options;

namespace Cookbook.Factory.Logging;

public class RequestResponseLoggerOptions
{
    public bool LogRequests { get; set; } = true;
    public bool LogResponses { get; set; } = true;
    public string[] SensitiveHeaders { get; set; } = { "Authorization", "Cookie" };
    public Func<HttpContext, bool> RequestFilter { get; set; } = _ => true;
    public string LogDirectory { get; set; } = "Logs";
}

public class RequestResponseLoggerMiddleware(RequestDelegate next, Serilog.ILogger logger,
    IOptions<RequestResponseLoggerOptions> options)
{
    private readonly Serilog.ILogger _logger = logger.ForContext<RequestResponseLoggerMiddleware>();
    private readonly RequestResponseLoggerOptions _options = options.Value;

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_options.RequestFilter(context))
        {
            await next(context);
            return;
        }

        var requestId = Guid.NewGuid().ToString("N").Substring(0, 5);

        // Replace the current logger in the logging context
        using (Serilog.Context.LogContext.PushProperty("RequestId", requestId))
        {
            if (_options.LogRequests)
            {
                await LogRequestAsync(context);
            }

            var originalBodyStream = context.Response.Body;
            using var responseBody = new MemoryStream();
            context.Response.Body = responseBody;

            try
            {
                await next(context);
            }
            finally
            {
                if (_options.LogResponses)
                {
                    await LogResponseAsync(context, responseBody);
                }

                responseBody.Position = 0;
                await responseBody.CopyToAsync(originalBodyStream);
            }
        }
    }

    private async Task LogRequestAsync(HttpContext context)
    {
        context.Request.EnableBuffering();

        var requestBody = await new StreamReader(context.Request.Body).ReadToEndAsync();
        context.Request.Body.Position = 0;

        var logContext = new
        {
            RequestMethod = context.Request.Method,
            RequestPath = context.Request.Path,
            RequestQueryString = context.Request.QueryString.ToString(),
            RequestHeaders = MaskSensitiveHeaders(context.Request.Headers),
            RequestBody = requestBody
        };

        _logger.Information("HTTP Request: {@RequestDetails}", logContext);
    }

    private async Task LogResponseAsync(HttpContext context, MemoryStream responseBody)
    {
        responseBody.Position = 0;
        var responseContent = await new StreamReader(responseBody).ReadToEndAsync();

        var logContext = new
        {
            ResponseStatusCode = context.Response.StatusCode,
            ResponseHeaders = MaskSensitiveHeaders(context.Response.Headers),
            ResponseBody = responseContent
        };

        _logger.Information("HTTP Response: {@RequestDetails}", logContext);
    }

    private object MaskSensitiveHeaders(IHeaderDictionary headers)
    {
        return headers.ToDictionary(
            h => h.Key,
            h => _options.SensitiveHeaders.Contains(h.Key) ? "******" : h.Value.ToString());
    }
}

public static class ServiceCollectionExtensions
{
    public static void AddRequestResponseLogger(this IServiceCollection services,
        Action<RequestResponseLoggerOptions>? configureOptions = null)
    {
        services.AddHttpContextAccessor();
        services.AddSingleton<ILogger>(sp =>
        {
            var logger = sp.GetRequiredService<Serilog.ILogger>();
            return new Logger(logger);
        });

        if (configureOptions != null)
        {
            services.Configure(configureOptions);
        }
        else
        {
            services.Configure<RequestResponseLoggerOptions>(_ => { });
        }
    }
}