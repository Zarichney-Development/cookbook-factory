using Cookbook.Factory.Controllers;
using Cookbook.Factory.Services;
using OpenAI;
using Microsoft.OpenApi.Models;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .WriteTo.File(
        path: $"logs/log-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt",
        restrictedToMinimumLevel: LogEventLevel.Information
    )
    .CreateLogger();

builder.Host.UseSerilog(); // Use Serilog for hosting

try
{
    Log.Information("Starting web application");

    builder.Configuration.AddJsonFile("appsettings.json");

    builder.Services.AddControllers();
    builder.Services.AddAutoMapper(typeof(ApiController));
    
    builder.Services.AddScoped<RecipeService>();
    builder.Services.AddScoped<ModelService>();

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

    var app = builder.Build();

    app.UseSerilogRequestLogging();

    app.UseCors("AllowSpecificOrigin");

    app.UseDeveloperExceptionPage();

    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "SDLC Toolkit API v1"));

    app.UseHttpsRedirection();
    app.UseAuthorization();

    app.MapControllers();

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