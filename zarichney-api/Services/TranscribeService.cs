using OpenAI.Audio;
using Polly;
using Polly.Retry;
using Zarichney.Config;
using ILogger = Serilog.ILogger;

namespace Zarichney.Services;

public class TranscribeConfig : IConfig
{
  public string ModelName { get; init; } = "whisper-1";
  public int RetryAttempts { get; init; } = 5;
}

public interface ITranscribeService
{
  Task<string> TranscribeAudioAsync(Stream audioStream);
}

public class TranscribeService : ITranscribeService
{
  private static readonly ILogger Log = Serilog.Log.ForContext<TranscribeService>();
  private readonly AudioClient _client;
  private readonly AsyncRetryPolicy _retryPolicy;

  public TranscribeService(AudioClient client, TranscribeConfig config)
  {
    _client = client;

    _retryPolicy = Policy
      .Handle<Exception>()
      .WaitAndRetryAsync(
        retryCount: config.RetryAttempts,
        sleepDurationProvider: _ => TimeSpan.FromSeconds(1),
        onRetry: (exception, _, retryCount, context) =>
        {
          Log.Warning(exception,
            "Transcription attempt {retryCount}: Retrying due to {exception}. Retry Context: {@Context}",
            retryCount, exception.Message, context);
        }
      );
  }

  public async Task<string> TranscribeAudioAsync(Stream audioStream)
  {
    try
    {
      Log.Information("Starting audio transcription");

      return await _retryPolicy.ExecuteAsync(async () =>
      {
        try
        {
          var tempFile = await SaveStreamToTempFile(audioStream);

          try
          {
            var transcriptionResult = await _client.TranscribeAudioAsync(tempFile);
            var transcription = transcriptionResult.Value;
            Log.Information("Audio transcription completed successfully");
            return transcription.Text;
          }
          finally
          {
            // Clean up temp file
            if (File.Exists(tempFile))
            {
              File.Delete(tempFile);
            }
          }
        }
        catch (Exception e)
        {
          Log.Error(e, "Error occurred during audio transcription");
          throw;
        }
      });
    }
    catch (Exception e)
    {
      Log.Error(e, "Failed to transcribe audio after all retry attempts");
      throw;
    }
  }

  public async Task<AudioTranscription> TranscribeAudioVerboseAsync(Stream audioStream,
    AudioTranscriptionOptions? options = null)
  {
    try
    {
      Log.Information("Starting verbose audio transcription");

      return await _retryPolicy.ExecuteAsync(async () =>
      {
        try
        {
          var tempFile = await SaveStreamToTempFile(audioStream);

          try
          {
            options ??= new AudioTranscriptionOptions
            {
              ResponseFormat = AudioTranscriptionFormat.Verbose,
              TimestampGranularities = AudioTimestampGranularities.Word | AudioTimestampGranularities.Segment
            };

            var transcriptionResult = await _client.TranscribeAudioAsync(tempFile, options);

            var transcription = transcriptionResult.Value;

            Log.Information(
              "Verbose audio transcription completed successfully. Segments: {SegmentCount}, Words: {WordCount}",
              transcription.Segments?.Count ?? 0,
              transcription.Words?.Count ?? 0);

            return transcription;
          }
          finally
          {
            // Clean up temp file
            if (File.Exists(tempFile))
            {
              File.Delete(tempFile);
            }
          }
        }
        catch (Exception e)
        {
          Log.Error(e, "Error occurred during verbose audio transcription");
          throw;
        }
      });
    }
    catch (Exception e)
    {
      Log.Error(e, "Failed to transcribe audio verbosely after all retry attempts");
      throw;
    }
  }

  private static async Task<string> SaveStreamToTempFile(Stream stream)
  {
    var tempFile = Path.Combine(Path.GetTempPath(), $"audio_{Guid.NewGuid()}.webm");

    try
    {
      await using var fileStream = File.Create(tempFile);
      stream.Position = 0; // Reset stream position
      await stream.CopyToAsync(fileStream);
      return tempFile;
    }
    catch (Exception e)
    {
      // Clean up on error
      if (File.Exists(tempFile))
      {
        File.Delete(tempFile);
      }

      Log.Error(e, "Failed to save audio stream to temporary file");
      throw;
    }
  }
}