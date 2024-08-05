using System.Text.Json;

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
}