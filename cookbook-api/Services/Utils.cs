using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Protocols.Configuration;

namespace Cookbook.Factory.Services;

public static class Utils
{
    public static T Deserialize<T>(string content)
        => JsonSerializer.Deserialize<T>(content,
               new JsonSerializerOptions
               {
                   PropertyNameCaseInsensitive = true
               })
           ?? throw new JsonException($"Failed to deserialize function arguments: {content} for type {typeof(T).Name}");

    public static string SplitCamelCase(string input)
    {
        return string.Concat(input.Select((x, i) => i > 0 && char.IsUpper(x) ? " " + x : x.ToString()));
    }
}

public static class ObjectExtensions
{
    public static string ToMarkdown(this object obj, string title)
    {
        var sb = new StringBuilder($"## {title}\n");
        var properties = obj.GetType().GetProperties();

        foreach (var prop in properties)
        {
            AppendPropertyIfNotEmpty(sb, obj, prop);
        }

        return sb.ToString();
    }

    private static void AppendPropertyIfNotEmpty(StringBuilder sb, object obj, PropertyInfo prop)
    {
        var value = prop.GetValue(obj);
        if (IsNullOrEmpty(value)) return;

        var propertyName = Utils.SplitCamelCase(prop.Name);

        sb.Append($"{propertyName}: ");

        if (value is IEnumerable<object> list)
        {
            sb.AppendLine(string.Join(", ", list));
        }
        else
        {
            sb.AppendLine(value?.ToString());
        }
    }

    private static bool IsNullOrEmpty(object? value)
    {
        if (value == null) return true;
        if (value is string str) return string.IsNullOrEmpty(str);
        if (value is IEnumerable<object> list) return !list.Any();
        if (value.GetType().IsValueType) return value.Equals(Activator.CreateInstance(value.GetType()));
        return false;
    }
}

public class AtomicCounter
{
    private int _value;
    public int Increment() => Interlocked.Increment(ref _value);
    public int Value => _value;
}

public interface IConfig;

public static class ConfigurationExtensions
{
    private const string PlaceholderValue = "recommended to set in app secrets";

    public static void AddConfigurations(this IServiceCollection services, IConfiguration configuration)
    {
        var configTypes = Assembly.GetExecutingAssembly()
            .GetTypes()
            .Where(t => typeof(IConfig).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

        foreach (var configType in configTypes)
        {
            var sectionName = configType.Name;
            var config = configuration.GetSection(sectionName).Get(configType);

            if (config == null)
            {
                throw new InvalidOperationException($"Configuration section '{sectionName}' is missing or invalid.");
            }

            ValidateAndReplaceProperties(config, sectionName);

            services.AddSingleton(configType, config);
        }
    }

    public static T GetConfig<T>(this IServiceCollection services) where T : class, IConfig
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