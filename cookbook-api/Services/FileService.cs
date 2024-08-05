using System.Collections.Concurrent;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Serilog;
using ILogger = Serilog.ILogger;

namespace Cookbook.Factory.Services;

public class WriteOperation(string directory, string filename, string data, string extension)
{
    public string Directory { get; } = directory;
    public string Filename { get; } = filename;
    public string Data { get; } = data;
    public string Extension { get; } = extension;
}

public class FileService
{
    private readonly ILogger _log = Log.ForContext<FileService>();
    private readonly ConcurrentQueue<WriteOperation> _writeQueue = new();
    private readonly Task _processQueueTask;
    private readonly CancellationTokenSource _cancellationTokenSource = new();

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
        string content;
        if (extension == "json")
        {
            content = JsonSerializer.Serialize(data, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
        }
        else
        {
            content = data.ToString()!
                ;
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

            var fileNamePath = Path.Combine(operation.Directory, $"{SanitizeFileName(operation.Filename)}.{operation.Extension}");

            await using var fileStream = new FileStream(fileNamePath, FileMode.Create, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous);
            await using var streamWriter = new StreamWriter(fileStream, Encoding.UTF8);
            await streamWriter.WriteAsync(operation.Data);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error writing file: {Filename}", operation.Filename);
        }
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