using System.Collections.Concurrent;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Polly;
using Polly.Retry;
using Serilog;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using ILogger = Serilog.ILogger;

namespace Zarichney.Services;

public interface IFileService : IDisposable
{
  Task WriteToFile(string directory, string filename, object data, string extension = "json");
  void WriteToFileAsync(string directory, string filename, object data, string extension = "json");
  Task<T> ReadFromFile<T>(string directory, string filename, string extension = "json");
  string[] GetFiles(string directoryPath);
  string GetFile(string filePath);
  Task<string> GetFileAsync(string filePath);
  Task<byte[]> GetFileBytes(string filePath);
  Task CreateFile(string filePath, object data, string fileType);
  void DeleteFile(string filePath);
  bool FileExists(string? filePath);
}

public class FileService : IFileService
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
        Log.Warning(exception,
          "Read attempt {retryCount}: Retrying due to {exception}. Retry Context: {@Context}",
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

  public async Task WriteToFile(string directory, string filename, object data, string extension = "json")
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
        if (data is not byte[])
          throw new ArgumentException("PDF data must be provided as a byte array");
        content = data;
        break;
      default:
        throw new ArgumentException($"Unsupported file extension: {extension}");
    }

    await PerformWriteOperationAsync(new WriteOperation(directory, filename, content, extension));
  }

  public void WriteToFileAsync(string directory, string filename, object data, string extension = "json")
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
        if (data is not byte[])
          throw new ArgumentException("PDF data must be provided as a byte array");
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

      _log.Verbose("Writing file: {Filename}", fileNamePath);

      if (operation.Data is byte[] pdfData)
      {
        await File.WriteAllBytesAsync(fileNamePath, pdfData);
      }
      else
      {
        await using var fileStream = new FileStream(fileNamePath, FileMode.Create, FileAccess.Write,
          FileShare.ReadWrite,
          4096, FileOptions.Asynchronous);
        await using var streamWriter = new StreamWriter(fileStream, Encoding.UTF8);
        await streamWriter.WriteAsync(operation.Data.ToString());
      }

      _log.Information("Successfully wrote file: {Filename}", fileNamePath);
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

    if (extension.ToLower() == "pdf")
    {
      return (T)data;
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

    _log.Verbose("Loading existing data from '{FilePath}'", filePath);

    if (!File.Exists(filePath)) return null;

    try
    {
      return await _retryPolicy.ExecuteAsync(async () =>
      {
        switch (extension.ToLower())
        {
          case "json":
            var jsonContent = await GetFileAsync(filePath);
            return Utils.Deserialize<object>(jsonContent);
          case "md":
          case "txt":
            return await GetFileAsync(filePath);
          case "pdf":
            return await GetFileBytes(filePath);
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

  /// <summary>
  /// Returns an array of file paths in the specified directory
  /// </summary>
  /// <param name="directoryPath"></param>
  /// <returns></returns>
  public string[] GetFiles(string directoryPath)
  {
    if (!Directory.Exists(directoryPath))
    {
      Directory.CreateDirectory(directoryPath);
    }

    return Directory.GetFiles(directoryPath);
  }

  public string GetFile(string filePath)
  {
    var task = GetFileAsync(filePath);
    task.Wait();
    return task.Result;
  }

  public async Task<string> GetFileAsync(string filePath)
  {
    _log.Verbose("Read All Text from '{FilePath}'", filePath);
    return await File.ReadAllTextAsync(filePath);
  }

  public async Task<byte[]> GetFileBytes(string filePath)
  {
    _log.Verbose("Read All Bytes from '{FilePath}'", filePath);
    return await File.ReadAllBytesAsync(filePath);
  }

  public async Task CreateFile(string filePath, object data, string fileType)
  {
    switch (fileType.ToLower())
    {
      case "image/jpeg":
        await using (var fileStream = File.Create(filePath))
        {
          await ((Image?)data).SaveAsJpegAsync(fileStream, new JpegEncoder { Quality = 90 });
          _log.Information("Created JPEG file: {FilePath}", filePath);
        }

        return;

      default:
        throw new ArgumentException($"Unsupported file type: {fileType}");
    }
  }

  public void DeleteFile(string filePath)
  {
    var retryPolicy = Policy
      .Handle<IOException>()
      .Or<UnauthorizedAccessException>()
      .WaitAndRetry(
        retryCount: 3,
        sleepDurationProvider: retryAttempt => TimeSpan.FromMilliseconds(200),
        onRetry: (exception, timeSpan, retryCount, context) =>
        {
          _log.Warning(exception, "Retry {RetryCount}: Unable to delete file: {FilePath}. Retrying in {RetryTime}ms",
            retryCount, filePath, timeSpan.TotalMilliseconds);
        });

    retryPolicy.Execute(() =>
    {
      if (IsFileLocked(filePath))
      {
        _log.Warning("File is locked: {FilePath}", filePath);
        throw new UnauthorizedAccessException();
      }

      File.Delete(filePath);
      _log.Information("Deleted file: {FilePath}", filePath);
    });
  }

  private bool IsFileLocked(string filePath)
  {
    try
    {
      using var stream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
      return false; // File is not locked
    }
    catch (IOException)
    {
      return true; // File is locked
    }
  }


  public bool FileExists(string? filePath)
    => !string.IsNullOrEmpty(filePath) && File.Exists(filePath);
}

public class WriteOperation(string directory, string filename, object data, string extension)
{
  public string Directory { get; } = directory;
  public string Filename { get; } = filename;
  public object Data { get; } = data;
  public string Extension { get; } = extension;
}