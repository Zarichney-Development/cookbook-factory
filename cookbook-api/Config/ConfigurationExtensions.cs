using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Cookbook.Factory.Prompts;
using Microsoft.IdentityModel.Protocols.Configuration;
using Serilog;

namespace Cookbook.Factory.Config;

public static class ServiceCollectionExtensions
{
    public static void AddPrompts(this IServiceCollection services, params Assembly[] assemblies)
    {
        var promptTypes = assemblies
            .SelectMany(a => a.GetTypes())
            .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(PromptBase).IsAssignableFrom(type));

        foreach (var promptType in promptTypes)
        {
            services.AddSingleton(promptType);
        }
    }
}

public static class ConfigurationExtensions
{
    private const string PlaceholderValue = "recommended to set in app secrets";
    private const string DataFolderName = "Data";

    public static void RegisterConfigurationServices(this IServiceCollection services, IConfiguration configuration)
    {
        var dataPath = Environment.GetEnvironmentVariable("APP_DATA_PATH") ?? "Data";
        Log.Debug("APP_DATA_PATH environment variable: {DataPath}", dataPath);

        var pathConfigs = configuration.AsEnumerable()
            .Where(kvp => kvp.Value?.StartsWith($"{DataFolderName}/") == true)
            .ToList();

        Log.Debug("Found {Count} Data/ paths in configuration:", pathConfigs.Count);
        foreach (var kvp in pathConfigs)
        {
            var newPath = Path.Combine(dataPath, kvp.Value![$"{DataFolderName}/".Length..]);
            Log.Debug("Transforming path: {OldPath} -> {NewPath}", kvp.Value, newPath);
        }

        var transformedPaths = pathConfigs
            .Select(kvp => new KeyValuePair<string, string>(
                kvp.Key,
                Path.Combine(dataPath, kvp.Value![$"{DataFolderName}/".Length..])
            ))
            .ToList();

        if (transformedPaths.Any())
        {
            ((IConfigurationBuilder)configuration)
                .AddInMemoryCollection(transformedPaths!);

            // Verify final configuration
            Log.Debug("Final configuration paths:");
            foreach (var kvp in pathConfigs)
            {
                var finalValue = configuration[kvp.Key];
                Log.Information("{Key}: {Value}", kvp.Key, finalValue);
            }
        }
        else
        {
            Log.Debug("No paths were transformed!");
        }

        var configTypes = Assembly.GetExecutingAssembly()
            .GetTypes()
            .Where(t => typeof(IConfig).IsAssignableFrom(t) && t is { IsInterface: false, IsAbstract: false });

        foreach (var configType in configTypes)
        {
            // Use the config type name as the section name
            var sectionName = configType.Name;

            // Create an instance of the config object
            var config = Activator.CreateInstance(configType)!;

            // Attempt to bind the section to the config object
            configuration.GetSection(sectionName).Bind(config);

            // Explicitly reapply environment variable values (manually override if set)
            var properties = configType.GetProperties();
            foreach (var property in properties)
            {
                var envVariableName = $"{sectionName}__{property.Name}";
                var envValue = Environment.GetEnvironmentVariable(envVariableName);

                if (!string.IsNullOrEmpty(envValue))
                {
                    var convertedValue = Convert.ChangeType(envValue, property.PropertyType);
                    property.SetValue(config, convertedValue);
                }
            }

            ValidateAndReplaceProperties(config, sectionName);

            // Register the configuration as a singleton service
            services.AddSingleton(configType, config);
        }
    }

    public static T GetService<T>(this IServiceCollection services) where T : class
    {
        return services.BuildServiceProvider().GetRequiredService<T>();
    }

    private static void ValidateAndReplaceProperties(object config, string sectionName)
    {
        var properties = config.GetType().GetProperties();

        foreach (var property in properties)
        {
            var value = property.GetValue(config);

            if (property.GetCustomAttribute<RequiredAttribute>() == null || value is not (null or PlaceholderValue))
            {
                continue;
            }

            var exceptionMessage = $"Required property '{property.Name}' in configuration section '{sectionName}'";

            exceptionMessage += value switch
            {
                null => " is missing.",
                PlaceholderValue => " has a placeholder value. Please set it in your user secrets.",
                _ => string.Empty
            };

            throw new InvalidConfigurationException(exceptionMessage);
        }
    }
}