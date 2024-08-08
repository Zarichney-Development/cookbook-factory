using System.Collections.Concurrent;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using PdfSharp.Pdf;
using Polly;
using Polly.Retry;
using Serilog;
using ILogger = Serilog.ILogger;

namespace Cookbook.Factory.Services;

public class FileService : IDisposable
{
    private readonly ILogger _log = Log.ForContext<FileService>();
    private readonly ConcurrentQueue<WriteOperation> _writeQueue = new();
    private readonly Task _processQueueTask;
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    private readonly AsyncRetryPolicy _retryPolicy = Policy
        .Handle<Exception>()
        .WaitAndRetryAsync(
            retryCount: 5,
            sleepDurationProvider: _ => TimeSpan.FromMilliseconds(200),
            onRetry: (exception, _, retryCount, context) =>
            {
                Log.Warning(exception, "Attempt {retryCount}: Retrying due to {exception}. Retry Context: {@Context}",
                    retryCount, exception.Message, context);
            }
        );

    private readonly JsonSerializerOptions _jsonSerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public FileService()
    {
        _processQueueTask = Task.Run(ProcessQueueAsync);
    }

    public void Dispose()
    {
        _cancellationTokenSource.Cancel();
        _processQueueTask.Wait();
        _cancellationTokenSource.Dispose();
    }

    public void WriteToFile(string directory, string filename, object data, string extension = "json")
    {
        object content;
        switch (extension.ToLower())
        {
            case "json":
                content = JsonSerializer.Serialize(data, _jsonSerializerOptions);
                break;
            case "md":
            case "txt":
                content = data.ToString()!;
                break;
            case "pdf":
                if (data is not PdfDocument)
                    throw new ArgumentException("PDF data must be provided as a pdf document");
                content = data;
                break;
            default:
                throw new ArgumentException($"Unsupported file extension: {extension}");
        }

        _writeQueue.Enqueue(new WriteOperation(directory, filename, content, extension));
    }

    private async Task ProcessQueueAsync()
    {
        while (!_cancellationTokenSource.Token.IsCancellationRequested)
        {
            if (_writeQueue.TryDequeue(out var operation))
            {
                await PerformWriteOperationAsync(operation);
            }
            else
            {
                await Task.Delay(100); // Wait a bit before checking the queue again
            }
        }
    }

    private async Task PerformWriteOperationAsync(WriteOperation operation)
    {
        try
        {
            if (!Directory.Exists(operation.Directory))
            {
                Directory.CreateDirectory(operation.Directory);
            }

            var fileNamePath = Path.Combine(operation.Directory,
                $"{SanitizeFileName(operation.Filename)}.{operation.Extension}");

            if (operation.Data is PdfDocument pdfDoc)
            {
                pdfDoc.Save(fileNamePath);
            }
            else
            {
                await using var fileStream = new FileStream(fileNamePath, FileMode.Create, FileAccess.Write,
                    FileShare.ReadWrite,
                    4096, FileOptions.Asynchronous);
                await using var streamWriter = new StreamWriter(fileStream, Encoding.UTF8);
                await streamWriter.WriteAsync(operation.Data.ToString());
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error writing file: {Filename}", operation.Filename);
        }
    }

    internal static string SanitizeFileName(string fileName)
    {
        var invalidChars = new List<char> { ' ', '-' };
        invalidChars.AddRange(Path.GetInvalidFileNameChars());
        return string.Join("_", fileName.Split(invalidChars.ToArray(), StringSplitOptions.RemoveEmptyEntries));
    }

    public async Task<T> ReadFromFile<T>(string directory, string filename, string extension = "json")
    {
        var data = await LoadExistingData(directory, filename, extension);

        if (data == null)
        {
            return default!;
        }

        if (data is not JsonElement jsonElement)
        {
            return (T)data;
        }

        return Utils.Deserialize<T>(jsonElement.GetRawText())!;
    }

    private async Task<object?> LoadExistingData(string directory, string filename, string extension = "json")
    {
        var filePath = Path.Combine(directory, $"{SanitizeFileName(filename)}.{extension}");

        if (!File.Exists(filePath)) return null;

        try
        {
            return await _retryPolicy.ExecuteAsync(async () =>
            {
                switch (extension.ToLower())
                {
                    case "json":
                        var jsonContent = await File.ReadAllTextAsync(filePath);
                        return JsonSerializer.Deserialize<object>(jsonContent);
                    case "md":
                    case "txt":
                        return await File.ReadAllTextAsync(filePath);
                    case "pdf":
                        return await File.ReadAllBytesAsync(filePath);
                    default:
                        throw new ArgumentException($"Unsupported file extension: {extension}");
                }
            });
        }
        catch (Exception ex)
        {
            _log.Error(ex, $"Error loading existing data from '{filePath}'");
            return null;
        }
    }
}

public class WriteOperation(string directory, string filename, object data, string extension)
{
    public string Directory { get; } = directory;
    public string Filename { get; } = filename;
    public object Data { get; } = data;
    public string Extension { get; } = extension;
}