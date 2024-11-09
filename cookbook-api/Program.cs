using System.Reflection;
using System.Text;
using Azure.Identity;
using Cookbook.Factory.Services;
using Cookbook.Factory.Middleware;
using Cookbook.Factory.Prompts;
using Microsoft.Graph;
using Microsoft.IdentityModel.Protocols.Configuration;
using OpenAI;
using Microsoft.OpenApi.Models;
using Serilog;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

var builder = WebApplication.CreateBuilder(args);

// Configure Kestrel
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.ConfigureEndpointDefaults(listenOptions =>
    {
        // Clear all defaults
        listenOptions.KestrelServerOptions.ConfigureEndpointDefaults(_ => { });
    });
    
    // Explicitly bind to port 80 on all interfaces
    serverOptions.ListenAnyIP(80, options =>
    {
        options.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1AndHttp2;
    });
});

builder.Configuration.AddJsonFile("appsettings.json");
builder.Configuration.AddUserSecrets<Program>();
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddSystemsManager("/cookbook-api", new Amazon.Extensions.NETCore.Setup.AWSOptions
{
    Region = Amazon.RegionEndpoint.USEast2
});

var logger = new LoggerConfiguration()
    .WriteTo.Console()
    .Enrich.FromLogContext();

var seqUrl = builder.Configuration["LoggingConfig:SeqUrl"];
if (!string.IsNullOrEmpty(seqUrl) && Uri.IsWellFormedUriString(seqUrl, UriKind.Absolute))
{
    logger = logger.WriteTo.Seq(seqUrl); 
}
else
{
    // Add file logging for containerized environment
    var logPath = Path.Combine("logs", "cookbook-factory.log");
    logger = logger.WriteTo.File(logPath, 
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7);
}

Log.Logger = logger.CreateLogger();
Log.Information("Starting up the Cookbook Factory application...");

builder.Host.UseSerilog();

builder.Services.AddSingleton(Log.Logger);

builder.Services.AddHttpContextAccessor();

builder.Services.AddControllers()
    .AddJsonOptions(options => { options.JsonSerializerOptions.Converters.Add(new TypeConverter()); });

builder.Services.AddAutoMapper(typeof(Program));
builder.Services.AddMemoryCache();

builder.Services.AddConfigurations(builder.Configuration);

var emailConfig = builder.Services.GetService<EmailConfig>();

var graphClient = new GraphServiceClient(new ClientSecretCredential(
    emailConfig.AzureTenantId,
    emailConfig.AzureAppId,
    emailConfig.AzureAppSecret,
    new TokenCredentialOptions
    {
        AuthorityHost = AzureAuthorityHosts.AzurePublicCloud
    }), new[] { "https://graph.microsoft.com/.default" });
builder.Services.AddSingleton(graphClient);

var apiKey = builder.Configuration["LlmConfig:ApiKey"]
             ?? throw new InvalidConfigurationException("Missing required configuration for Azure/OpenAI API key.");
builder.Services.AddSingleton(new OpenAIClient(apiKey));

builder.Services.AddPrompts(typeof(PromptBase).Assembly);
builder.Services.AddSingleton<FileService>();
builder.Services.AddSingleton<IEmailService, EmailService>();
// Add this near the start of your Program.cs, after the builder configuration
builder.Services.AddSingleton<ITemplateService>(provider =>
{
    var fileService = provider.GetRequiredService<FileService>();
    var templateLogger = provider.GetRequiredService<ILogger<TemplateService>>();
    
    // Log the template directory path
    templateLogger.LogInformation("Template directory: {TemplateDirectory}", emailConfig.TemplateDirectory);
    
    // Verify template directory exists
    if (!Directory.Exists(emailConfig.TemplateDirectory))
    {
        templateLogger.LogError("Template directory not found: {TemplateDirectory}", emailConfig.TemplateDirectory);
        throw new DirectoryNotFoundException($"Template directory not found: {emailConfig.TemplateDirectory}");
    }
    
    // Verify base template exists
    var baseTemplatePath = Path.Combine(emailConfig.TemplateDirectory, "base.html");
    if (!File.Exists(baseTemplatePath))
    {
        templateLogger.LogError("Base template not found: {BaseTemplatePath}", baseTemplatePath);
        throw new FileNotFoundException($"Base template not found: {baseTemplatePath}");
    }
    
    return new TemplateService(emailConfig, fileService);
});
// builder.Services.AddSingleton<ITemplateService, TemplateService>();
builder.Services.AddSingleton<IBackgroundTaskQueue>(_ => new BackgroundTaskQueue(100));
builder.Services.AddHostedService<BackgroundTaskService>();

builder.Services.AddTransient<RecipeService>();
builder.Services.AddTransient<OrderService>();
builder.Services.AddTransient<WebScraperService>();
builder.Services.AddTransient<PdfCompiler>();
builder.Services.AddTransient<ILlmService, LlmService>();

builder.Services.AddSingleton<IRecipeRepository, RecipeRepository>();
var recipeRepository = builder.Services.GetService<IRecipeRepository>();
await recipeRepository.InitializeAsync();

builder.Services.Configure<ApiKeyConfig>(config =>
{
    config.AllowedKeys = builder.Configuration["ApiKeyConfig:AllowedKeys"] ?? string.Empty;
});

builder.Services.AddSingleton(_ =>
{
    var config = new ApiKeyConfig
    {
        AllowedKeys = builder.Configuration["ApiKeyConfig:AllowedKeys"] ?? string.Empty
    };
    
    if (!config.ValidApiKeys.Any())
    {
        throw new InvalidOperationException("No valid API keys configured");
    }
    
    return config;
});

builder.Services.AddEndpointsApiExplorer();

builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo 
    { 
        Title = "Cookbook Factory API", 
        Version = "v1",
        Description = "API for the Cookbook Factory application. Authenticate using the 'Authorize' button and provide your API key."
    });

    // Add security definition for API key
    c.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Name = "X-Api-Key",
        Description = "API key authentication. Enter your API key here.",
    });

    // Add security requirement for all operations
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "ApiKey"
                }
            },
            Array.Empty<string>()
        }
    });

    // Optional: Add custom filter to exclude health check endpoint from requiring authentication
    c.OperationFilter<SwaggerSecuritySchemeFilter>();
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowSpecificOrigin",
        policyBuilder =>
        {
            policyBuilder.WithOrigins("http://localhost:8080")
                .AllowAnyHeader()
                .AllowAnyMethod();
        });
});

builder.Services.AddRequestResponseLogger(options =>
{
    options.LogRequests = true;
    options.LogResponses = true;
    options.SensitiveHeaders = new[] { "Authorization", "Cookie", "X-API-Key" };
    options.RequestFilter = context => !context.Request.Path.StartsWithSegments("/swagger");
    options.LogDirectory = Path.Combine(builder.Environment.ContentRootPath, "Logs");
});

var app = builder.Build();

app.Use(async (context, next) =>
{
    Log.Information(
        "Request {Method} {Url} starting",
        context.Request.Method,
        context.Request.Path);
    
    await next();
    
    Log.Information(
        "Request {Method} {Url} completed with status {StatusCode}",
        context.Request.Method,
        context.Request.Path,
        context.Response.StatusCode);
});

app.UseMiddleware<ErrorHandlingMiddleware>();

app.UseMiddleware<RequestResponseLoggerMiddleware>();

app.UseCors("AllowSpecificOrigin");

app.UseDeveloperExceptionPage();

app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Cookbook Factory API v1"));

app.UseHttpsRedirection();
app.UseAuthorization();

app.UseApiKeyAuth();

app.MapControllers();

try
{
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}


public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPrompts(this IServiceCollection services, params Assembly[] assemblies)
    {
        var promptTypes = assemblies
            .SelectMany(a => a.GetTypes())
            .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(PromptBase).IsAssignableFrom(type));

        foreach (var promptType in promptTypes)
        {
            services.AddSingleton(promptType);
        }

        return services;
    }
}