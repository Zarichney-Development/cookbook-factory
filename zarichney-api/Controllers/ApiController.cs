using System.Text;
using Microsoft.AspNetCore.Mvc;
using Serilog;
using Zarichney.Config;
using Zarichney.Cookbook.Models;
using Zarichney.Cookbook.Services;
using Zarichney.Middleware;
using Zarichney.Services;
using ILogger = Serilog.ILogger;

namespace Zarichney.Controllers;

[ApiController]
[Route("api")]
public class ApiController(
  RecipeService recipeService,
  OrderService orderService,
  IEmailService emailService,
  IBackgroundTaskQueue taskQueue,
  IRecipeRepository recipeRepository,
  WebScraperService scraperService,
  ITranscribeService transcribeService,
  IGitHubService githubService,
  EmailConfig emailConfig
) : ControllerBase
{
  private readonly ILogger _log = Log.ForContext<ApiController>();

  [HttpPost("cookbook")]
  [ProducesResponseType(typeof(CookbookOrder), StatusCodes.Status201Created)]
  [ProducesResponseType(typeof(BadRequestObjectResult), StatusCodes.Status400BadRequest)]
  [ProducesResponseType(typeof(ApiErrorResult), StatusCodes.Status500InternalServerError)]
  public async Task<IActionResult> CreateCookbook([FromBody] CookbookOrderSubmission submission)
  {
    try
    {
      // reject if no email
      if (string.IsNullOrWhiteSpace(submission.Email))
      {
        _log.Warning("{Method}: No email provided in order", nameof(CreateCookbook));
        return BadRequest("Email is required");
      }

      await emailService.ValidateEmail(submission.Email);
      var order = await orderService.ProcessOrderSubmission(submission);

      // Queue the cookbook generation task
      _ = taskQueue.QueueBackgroundWorkItemAsync(async _ =>
      {
        try
        {
          await orderService.GenerateCookbookAsync(order, true);
          await orderService.CompilePdf(order);
          await orderService.EmailCookbook(order.OrderId);
        }
        catch (Exception ex)
        {
          _log.Error(ex, "{Method}: Background processing failed for order {OrderId}",
            nameof(CreateCookbook), order.OrderId);
        }
      });

      return Created($"/api/cookbook/order/{order.OrderId}", order);
    }
    catch (InvalidEmailException ex)
    {
      _log.Warning(ex, "{Method}: Invalid email validation for {Email}",
        nameof(CreateCookbook), submission.Email);
      return BadRequest(new { error = ex.Message, email = ex.Email, reason = ex.Reason.ToString() });
    }
    catch (Exception ex)
    {
      _log.Error(ex, "{Method}: Failed to create cookbook", nameof(CreateCookbook));
      return new ApiErrorResult(ex, $"{nameof(CreateCookbook)}: Failed to create cookbook");
    }
  }

  [HttpGet("cookbook/order/{orderId}")]
  [ProducesResponseType(typeof(CookbookOrder), StatusCodes.Status200OK)]
  [ProducesResponseType(typeof(NotFoundResult), StatusCodes.Status404NotFound)]
  [ProducesResponseType(typeof(ApiErrorResult), StatusCodes.Status500InternalServerError)]
  public async Task<IActionResult> GetOrder([FromRoute] string orderId)
  {
    try
    {
      var order = await orderService.GetOrder(orderId);
      return Ok(order);
    }
    catch (KeyNotFoundException ex)
    {
      _log.Warning(ex, "{Method}: Order not found: {OrderId}", nameof(GetOrder), orderId);
      return NotFound($"Order not found: {orderId}");
    }
    catch (Exception ex)
    {
      _log.Error(ex, "{Method}: Failed to retrieve order {OrderId}", nameof(GetOrder), orderId);
      return new ApiErrorResult(ex, $"{nameof(GetOrder)}: Failed to retrieve order");
    }
  }

  [HttpPost("cookbook/order/{orderId}")]
  [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
  [ProducesResponseType(typeof(NotFoundResult), StatusCodes.Status404NotFound)]
  [ProducesResponseType(typeof(ApiErrorResult), StatusCodes.Status500InternalServerError)]
  public async Task<IActionResult> ReprocessOrder([FromRoute] string orderId)
  {
    try
    {
      var order = await orderService.GetOrder(orderId);

      // Queue the cookbook generation task
      _ = taskQueue.QueueBackgroundWorkItemAsync(async _ =>
      {
        await orderService.GenerateCookbookAsync(order, true);
        await orderService.CompilePdf(order);
        await orderService.EmailCookbook(order.OrderId);
      });

      return Ok("Reprocessing order");
    }
    catch (KeyNotFoundException ex)
    {
      _log.Warning(ex, "{Method}: Order not found for reprocessing: {OrderId}",
        nameof(ReprocessOrder), orderId);
      return NotFound($"Order not found: {orderId}");
    }
    catch (Exception ex)
    {
      _log.Error(ex, "{Method}: Failed to reprocess order {OrderId}",
        nameof(ReprocessOrder), orderId);
      return new ApiErrorResult(ex, $"{nameof(ReprocessOrder)}: Failed to reprocess order");
    }
  }

  [HttpPost("cookbook/order/{orderId}/pdf")]
  [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
  [ProducesResponseType(typeof(BadRequestObjectResult), StatusCodes.Status400BadRequest)]
  [ProducesResponseType(typeof(NotFoundResult), StatusCodes.Status404NotFound)]
  [ProducesResponseType(typeof(ApiErrorResult), StatusCodes.Status500InternalServerError)]
  public async Task<IActionResult> RebuildPdf(
    [FromRoute] string orderId,
    [FromQuery] bool email = false)
  {
    try
    {
      if (string.IsNullOrWhiteSpace(orderId))
      {
        _log.Warning("{Method}: Empty orderId received", nameof(RebuildPdf));
        return BadRequest("OrderId parameter is required");
      }

      var order = await orderService.GetOrder(orderId);

      await orderService.CompilePdf(order, email);

      if (email)
      {
        await orderService.EmailCookbook(order.OrderId);
        return Ok("PDF rebuilt and email sent");
      }

      return Ok("PDF rebuilt");
    }
    catch (KeyNotFoundException ex)
    {
      _log.Warning(ex, "{Method}: Order not found for PDF rebuild: {OrderId}",
        nameof(RebuildPdf), orderId);
      return NotFound($"Order not found: {orderId}");
    }
    catch (Exception ex)
    {
      _log.Error(ex, "{Method}: Failed to rebuild PDF for order {OrderId}",
        nameof(RebuildPdf), orderId);
      return new ApiErrorResult(ex, $"{nameof(RebuildPdf)}: Failed to rebuild PDF");
    }
  }

  [HttpPost("cookbook/order/{orderId}/email")]
  [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
  [ProducesResponseType(typeof(NotFoundResult), StatusCodes.Status404NotFound)]
  [ProducesResponseType(typeof(ApiErrorResult), StatusCodes.Status500InternalServerError)]
  public async Task<IActionResult> ResendCookbook([FromRoute] string orderId)
  {
    try
    {
      await orderService.EmailCookbook(orderId);
      return Ok("Email sent");
    }
    catch (KeyNotFoundException ex)
    {
      _log.Warning(ex, "{Method}: Order not found for email resend: {OrderId}",
        nameof(ResendCookbook), orderId);
      return NotFound($"Order not found: {orderId}");
    }
    catch (Exception ex)
    {
      _log.Error(ex, "{Method}: Failed to resend email for order {OrderId}",
        nameof(ResendCookbook), orderId);
      return new ApiErrorResult(ex, $"{nameof(ResendCookbook)}: Failed to resend cookbook email");
    }
  }

  [HttpGet("recipe")]
  [ProducesResponseType(typeof(IEnumerable<Recipe>), StatusCodes.Status200OK)]
  [ProducesResponseType(typeof(BadRequestObjectResult), StatusCodes.Status400BadRequest)]
  [ProducesResponseType(typeof(NotFoundResult), StatusCodes.Status404NotFound)]
  [ProducesResponseType(typeof(ApiErrorResult), StatusCodes.Status500InternalServerError)]
  public async Task<IActionResult> GetRecipes([FromQuery] string query, [FromQuery] bool scrape = false)
  {
    try
    {
      if (string.IsNullOrWhiteSpace(query))
      {
        _log.Warning("{Method}: Empty query received", nameof(GetRecipes));
        return BadRequest("Query parameter is required");
      }

      var recipes = scrape
        ? await recipeService.GetRecipes(query) // include the feature of replacement name when scraping
        : await recipeService.GetRecipes(query, false);

      if (recipes.ToList().Count == 0)
      {
        return NotFound($"No recipes found for '{query}'");
      }

      return Ok(recipes);
    }
    catch (NoRecipeException e)
    {
      return NotFound(e.Message);
    }
    catch (Exception ex)
    {
      _log.Error(ex, "{Method}: Failed to get recipes for query: {Query}",
        nameof(GetRecipes), query);
      return new ApiErrorResult(ex, $"{nameof(GetRecipes)}: Failed to retrieve recipes");
    }
  }

  [HttpGet("recipe/scrape")]
  [ProducesResponseType(typeof(IEnumerable<Recipe>), StatusCodes.Status200OK)]
  [ProducesResponseType(typeof(BadRequestObjectResult), StatusCodes.Status400BadRequest)]
  [ProducesResponseType(typeof(NotFoundResult), StatusCodes.Status404NotFound)]
  [ProducesResponseType(typeof(ApiErrorResult), StatusCodes.Status500InternalServerError)]
  public async Task<IActionResult> ScrapeRecipes(
    [FromQuery] string query,
    [FromQuery] string? site = null,
    [FromQuery] bool? store = false)
  {
    try
    {
      if (string.IsNullOrWhiteSpace(query))
      {
        _log.Warning("{Method}: Empty query received", nameof(ScrapeRecipes));
        return BadRequest("Query parameter is required");
      }

      var recipes = await scraperService.ScrapeForRecipesAsync(query, site);

      if (recipes.ToList().Count == 0)
      {
        return NotFound($"No recipes found for '{query}'");
      }

      if (store != true)
      {
        return Ok(recipes);
      }

      // Further processing for ranking and storing recipes

      var newRecipes =
        await recipeService.RankUnrankedRecipesAsync(
          recipes.Where(r => !recipeRepository.ContainsRecipe(r.Id!)), query);

      // Process in the background
      _ = recipeRepository.AddUpdateRecipes(newRecipes);

      return Ok(newRecipes);
    }
    catch (Exception ex)
    {
      _log.Error(ex, "{Method}: Failed to scrape recipes for query: {Query}",
        nameof(ScrapeRecipes), query);
      return new ApiErrorResult(ex, $"{nameof(ScrapeRecipes)}: Failed to scrape recipes");
    }
  }

  [HttpPost("email/validate")]
  [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
  [ProducesResponseType(typeof(BadRequestObjectResult), StatusCodes.Status400BadRequest)]
  [ProducesResponseType(typeof(ApiErrorResult), StatusCodes.Status500InternalServerError)]
  public async Task<IActionResult> ValidateEmail([FromQuery] string email)
  {
    try
    {
      if (string.IsNullOrWhiteSpace(email))
      {
        _log.Warning("{Method}: Empty email received", nameof(ValidateEmail));
        return BadRequest("Email parameter is required");
      }

      await emailService.ValidateEmail(email);
      return Ok("Valid");
    }
    catch (InvalidEmailException ex)
    {
      _log.Warning(ex, "{Method}: Invalid email validation for {Email}",
        nameof(ValidateEmail), email);
      return BadRequest(new
      {
        error = ex.Message,
        email = ex.Email,
        reason = ex.Reason.ToString()
      });
    }
    catch (Exception ex)
    {
      _log.Error(ex, "{Method}: Failed to validate email: {Email}",
        nameof(ValidateEmail), email);
      return new ApiErrorResult(ex, $"{nameof(ValidateEmail)}: Failed to validate email");
    }
  }

  [HttpPost("transcribe")]
  [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
  [ProducesResponseType(typeof(BadRequestObjectResult), StatusCodes.Status400BadRequest)]
  [ProducesResponseType(typeof(ApiErrorResult), StatusCodes.Status500InternalServerError)]
  public async Task<IActionResult> TranscribeAudio([FromForm] IFormFile? audioFile)
  {
    try
    {
      _log.Information(
        "Received transcribe request. ContentType: {ContentType}, FileName: {FileName}, Length: {Length}, FormFile null: {IsNull}",
        audioFile?.ContentType,
        audioFile?.FileName,
        audioFile?.Length,
        audioFile == null);

      if (Request.Form.Files.Count == 0)
      {
        _log.Warning("No files found in form data. Form count: {Count}", Request.Form.Count);
        return BadRequest("No files found in request");
      }

      if (audioFile == null || audioFile.Length == 0)
      {
        _log.Warning("{Method}: No audio file received or empty file", nameof(TranscribeAudio));
        return BadRequest("Audio file is required and must not be empty");
      }

      // Log the content type we received
      if (!audioFile.ContentType.StartsWith("audio/"))
      {
        _log.Warning("{Method}: Invalid content type: {ContentType}",
          nameof(TranscribeAudio), audioFile.ContentType);
        return BadRequest($"Invalid content type: {audioFile.ContentType}. Expected audio/*");
      }

      // Generate timestamp-based filename
      var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH-mm-ssZ");
      var audioFileName = $"{timestamp}.webm";
      var transcriptFileName = $"{timestamp}.txt";

      // Read the audio file into memory
      using var ms = new MemoryStream();
      await audioFile.CopyToAsync(ms);
      var audioData = ms.ToArray();

      try
      {
        // First, commit the audio file
        await githubService.CommitFileAsync(
          audioFileName,
          audioData,
          "recordings",
          $"Add audio recording: {audioFileName}"
        );
      }
      catch (Exception ex)
      {
        await SendErrorNotification("GitHub Audio Commit", ex, audioFileName);
        throw;
      }

      string transcript;
      try
      {
        // Reset stream position for transcription
        ms.Position = 0;
        transcript = await transcribeService.TranscribeAudioAsync(ms);
      }
      catch (Exception ex)
      {
        await SendErrorNotification("Audio Transcription", ex, audioFileName);
        throw;
      }

      try
      {
        // Commit the transcript
        await githubService.CommitFileAsync(
          transcriptFileName,
          Encoding.UTF8.GetBytes(transcript),
          "transcripts",
          $"Add transcript: {transcriptFileName}"
        );
      }
      catch (Exception ex)
      {
        await SendErrorNotification("GitHub Transcript Commit", ex, transcriptFileName);
        throw;
      }

      return Ok(new
      {
        message = "Audio file processed and transcript stored successfully",
        audioFile = audioFileName,
        transcriptFile = transcriptFileName,
        timestamp = DateTimeOffset.UtcNow
      });
    }
    catch (Exception ex)
    {
      _log.Error(ex, "{Method}: Failed to process audio file", nameof(TranscribeAudio));
      return new ApiErrorResult(ex, "Failed to process audio file");
    }
  }

  private async Task SendErrorNotification(string stage, Exception ex, string fileName)
  {
    await emailService.SendEmail(
      emailConfig.FromEmail,
      $"Transcription Service Error - {stage}",
      "error-log",
      new Dictionary<string, object>
      {
        { "timestamp", DateTime.UtcNow.ToString("O") },
        { "fileName", fileName },
        { "errorType", ex.GetType().Name },
        { "errorMessage", ex.Message },
        { "stage", stage },
        { "stackTrace", ex.StackTrace ?? "No stack trace available" },
        {
          "additionalContext", new Dictionary<string, string>
          {
            { "ProcessStage", stage },
            { "MachineName", Environment.MachineName },
            { "OsVersion", Environment.OSVersion.ToString() }
          }
        }
      }
    );
  }

  [HttpGet("health/secure")]
  public IActionResult HealthCheck()
  {
    return Ok(new
    {
      Success = true,
      Time = DateTime.Now.ToLocalTime()
    });
  }
}

public record KeyValidationRequest(string Key);

[ApiController]
[Route("api")]
public class PublicController(
  ILogger logger,
  ApiKeyConfig apiKeyConfig
) : ControllerBase
{
  [HttpGet("health")]
  public IActionResult HealthCheck()
  {
    return Ok(new
    {
      Success = true,
      Time = DateTime.Now.ToLocalTime()
    });
  }

  [HttpPost("key/validate")]
  [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
  [ProducesResponseType(typeof(BadRequestObjectResult), StatusCodes.Status400BadRequest)]
  [ProducesResponseType(typeof(ApiErrorResult), StatusCodes.Status401Unauthorized)]
  public IActionResult ValidateKey([FromBody] KeyValidationRequest request)
  {
    try
    {
      if (string.IsNullOrWhiteSpace(request.Key))
      {
        logger.Warning("{Method}: Empty password received", nameof(ValidateKey));
        return BadRequest("Password is required");
      }

      // Check if the password matches any valid API key
      if (!apiKeyConfig.ValidApiKeys.Contains(request.Key))
      {
        logger.Warning("{Method}: Invalid password attempt", nameof(ValidateKey));
        return Unauthorized(new
        {
          error = "Invalid password",
          timestamp = DateTimeOffset.UtcNow
        });
      }

      return Ok(new
      {
        message = "Valid password",
        timestamp = DateTimeOffset.UtcNow
      });
    }
    catch (Exception ex)
    {
      logger.Error(ex, "{Method}: Unexpected error during key validation", nameof(ValidateKey));
      return new ApiErrorResult(ex, $"{nameof(ValidateKey)}: Failed to validate key");
    }
  }
}