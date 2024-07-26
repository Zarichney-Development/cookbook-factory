using Cookbook.Factory;
using Cookbook.Factory.Services;
using Cookbook.Factory.Logging;
using OpenAI;
using Microsoft.OpenApi.Models;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.Seq("http://localhost:5341/")
    .CreateLogger();

builder.Host.UseSerilog();

builder.Services.AddSingleton(Log.Logger);

Log.Information("Starting Cookbook Factory web application");

builder.Configuration.AddJsonFile("appsettings.json");

builder.Services.AddHttpContextAccessor();

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new TypeConverter());
    });

builder.Services.AddAutoMapper(typeof(Program));

builder.Services.AddScoped<RecipeService>();
builder.Services.AddScoped<WebScraperService>();
builder.Services.AddScoped<IModelService, ModelService>();

builder.Services.AddEndpointsApiExplorer();

var apiKey = builder.Configuration["OPENAI_API_KEY"]
             ?? throw new InvalidOperationException("Missing required configuration for Azure/OpenAI API key.");

builder.Services.AddSingleton(new OpenAIClient(apiKey));

builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Cookbook Factory API", Version = "v1" });
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowSpecificOrigin",
        policyBuilder =>
        {
            policyBuilder.WithOrigins("http://localhost:4200")
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

app.UseMiddleware<ErrorHandlingMiddleware>();

app.UseMiddleware<RequestResponseLoggerMiddleware>();

// app.UseSerilogRequestLogging();

app.UseCors("AllowSpecificOrigin");

app.UseDeveloperExceptionPage();

app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Cookbook Factory API v1"));

app.UseHttpsRedirection();
app.UseAuthorization();

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