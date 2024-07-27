using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Serilog;
using ILogger = Serilog.ILogger;

namespace Cookbook.Factory.Services;

public class FileService
{
    private readonly ILogger _log = Log.ForContext<FileService>();

    public async Task WriteToFile(string directory, string filename, object data, string extension = "json")
    {
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        
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

    public async Task<List<T>> LoadExistingData<T>(string directory, string filename, string filetype = "json")
    {
        var filePath = Path.Combine(directory, $"{SanitizeFileName(filename)}.{filetype}");
        var data = new List<T>();

        if (!File.Exists(filePath)) return data;

        try
        {
            data = JsonSerializer.Deserialize<List<T>>(await File.ReadAllTextAsync(filePath)) ?? new List<T>();
        }
        catch (Exception ex)
        {
            _log.Error(ex, $"Error loading existing data from '{filePath}'");
        }

        return data;
    }
}