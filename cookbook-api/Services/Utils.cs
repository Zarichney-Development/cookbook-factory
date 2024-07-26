using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AngleSharp.Dom;
using ILogger = Cookbook.Factory.Logging.ILogger;

namespace Cookbook.Factory.Services;

public static class Utils
{
    public static async Task WriteToFile(string directory, string filename, object data, string extension = "json")
    {
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        var json = JsonSerializer.Serialize(data, options);
        var fileNamePath = Path.Combine(directory, $"{SanitizeFileName(filename)}.{extension}");

        await using var fileStream = new FileStream(fileNamePath, FileMode.Create, FileAccess.Write, FileShare.None,
            4096, FileOptions.Asynchronous);
        await using var streamWriter = new StreamWriter(fileStream, Encoding.UTF8);
        await streamWriter.WriteAsync(json);
    }

    private static string SanitizeFileName(string fileName)
    {
        return string.Join("_", fileName.Split(Path.GetInvalidFileNameChars()));
    }

    public static string? ExtractTextFromHtmlDoc(ILogger logger, IDocument document, string selector,
        string? attribute = null)
    {
        if (string.IsNullOrEmpty(selector)) return null;

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

        return null;
    }

    public static async Task<string> SendGetRequestForHtml(string url, ILogger logger)
    {
        try
        {
            logger.LogInformation("Running GET request for URL: {url}", url);
            var client = new HttpClient();
            var response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }
        catch (HttpRequestException e)
        {
            logger.LogWarning(e, "HTTP error occurred for url {urL}", url);
        }
        catch (TaskCanceledException e)
        {
            logger.LogWarning(e, "Timeout occurred for url {urL}", url);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Error occurred during GetHtmlAsync for url {urL}", url);
        }

        return null!;
    }

    public static async Task<List<T>> LoadExistingData<T>(ILogger logger, string directory, string filename,
        string filetype = "json")
    {
        var filePath = Path.Combine(directory, $"{Utils.SanitizeFileName(filename)}.{filetype}");
        var data = new List<T>();

        if (File.Exists(filePath))
        {
            try
            {
                data = JsonSerializer.Deserialize<List<T>>(
                           await File.ReadAllTextAsync(filePath))
                       ?? new List<T>();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Error loading existing data from '{filePath}'");
            }
        }

        return data;
    }
}