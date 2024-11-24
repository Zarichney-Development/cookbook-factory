using System.Text;
using Azure.Identity;
using Cookbook.Factory.Config;
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

    serverOptions.ListenAnyIP(5000,
        options => { options.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1AndHttp2; });
});

builder.Configuration
    .AddJsonFile("appsettings.json")
    .AddUserSecrets<Program>()
    .AddEnvironmentVariables()
    .AddSystemsManager("/cookbook-api", new Amazon.Extensions.NETCore.Setup.AWSOptions
    {
        Region = Amazon.RegionEndpoint.USEast2
    })
    ;

var logger = new LoggerConfiguration()
    // .MinimumLevel.Debug()
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

builder.Services.RegisterConfigurationServices(builder.Configuration);

builder.Host.UseSerilog();

builder.Services.AddSingleton(Log.Logger);

builder.Services.AddHttpContextAccessor();

builder.Services.AddControllers()
    .AddJsonOptions(options => { options.JsonSerializerOptions.Converters.Add(new TypeConverter()); });

builder.Services.AddAutoMapper(typeof(Program));
builder.Services.AddMemoryCache();

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
builder.Services.AddSingleton<IFileService, FileService>();
builder.Services.AddSingleton<IEmailService, EmailService>();
builder.Services.AddSingleton<ITemplateService, TemplateService>();
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
    c.SwaggerDoc("swagger", new OpenApiInfo
    {
        Title = "Cookbook Factory API",
        Version = "v1",
        Description =
            "API for the Cookbook Factory application. Authenticate using the 'Authorize' button and provide your API key."
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
            policyBuilder
                .WithOrigins("http://localhost:5000")
                .WithOrigins("https://zarichney.com")
                .AllowAnyHeader()
                .AllowAnyMethod();
        });
});

builder.Services.AddRequestResponseLogger(options =>
{
    options.LogRequests = true;
    options.LogResponses = true;
    options.SensitiveHeaders = ["Authorization", "Cookie", "X-API-Key"];
    options.RequestFilter = context => !context.Request.Path.StartsWithSegments("/api/factory/swagger");
    options.LogDirectory = Path.Combine(builder.Environment.ContentRootPath, "Logs");
});

var app = builder.Build();

app.UseMiddleware<RequestResponseLoggerMiddleware>();

app.UseMiddleware<ErrorHandlingMiddleware>();

app.UseCors("AllowSpecificOrigin");

app
    .UseSwagger(c =>
    {
        Log.Information("Configuring Swagger JSON at: api/factory/swagger/swagger.json");
        c.RouteTemplate = "api/factory/swagger/{documentName}.json";
        c.SerializeAsV2 = false;
    })
    .UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/api/factory/swagger/swagger.json", "Cookbook Factory API");
        c.RoutePrefix = "api/factory/swagger";
    });

app.UseHttpsRedirection();
app.UseAuthorization();

if (app.Environment.IsProduction())
{
    app.UseApiKeyAuth();
}

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