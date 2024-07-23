using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AngleSharp.Dom;
using Cookbook.Factory.Models;

namespace Cookbook.Factory.Services;

public static class Utils
{
    private const string OutputDirectoryName = "Recipes";

    public static async Task SaveToJsonAsync(string filename, List<Recipe> data)
    {
        if (!Directory.Exists(OutputDirectoryName))
        {
            Directory.CreateDirectory(OutputDirectoryName);
        }

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        var json = JsonSerializer.Serialize(data, options);
        var filePath = Path.Combine(OutputDirectoryName, $"{SanitizeFileName(filename)}.json");

        using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
        using (var streamWriter = new StreamWriter(fileStream, Encoding.UTF8))
        {
            await streamWriter.WriteAsync(json);
        }
    }

    public static string SanitizeFileName(string fileName)
    {
        return string.Join("_", fileName.Split(Path.GetInvalidFileNameChars()));
    }

    public static async Task<Dictionary<string, Dictionary<string, string>>> LoadSiteSelectors()
    {
        string json = await File.ReadAllTextAsync("site_selectors.json");
        return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json)!;
    }
    
    public static string ExtractText(ILogger logger, IDocument document, string selector, string? attribute = null)
    {
        if (string.IsNullOrEmpty(selector))
        {
            return string.Empty;
        }

        try
        {
            var element = document.QuerySelector(selector);
            if (element != null)
            {
                return attribute != null ? element.GetAttribute(attribute)! : element.TextContent.Trim();
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, $"Error occurred with selector {selector} during extract_text");
        }

        return null!;
    }

    public static async Task<string> GetHtmlAsync(string url, ILogger logger)
    {
        try
        {
            logger.LogInformation($"Running GET request for URL: {url}");
            var client = new HttpClient();
            var response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }
        catch (HttpRequestException e)
        {
            logger.LogError(e, "HTTP error occurred");
        }
        catch (TaskCanceledException e)
        {
            logger.LogError(e, "Timeout occurred");
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error occurred during GetHtmlAsync");
        }

        return null!;
    }
}