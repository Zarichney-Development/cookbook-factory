using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using HandlebarsDotNet;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using RestSharp;
using Serilog;
using Zarichney.Config;
using ILogger = Serilog.ILogger;
using SendMailPostRequestBody = Microsoft.Graph.Users.Item.SendMail.SendMailPostRequestBody;

namespace Zarichney.Services;

public class EmailConfig : IConfig
{
  [Required] public required string AzureTenantId { get; init; }
  [Required] public required string AzureAppId { get; init; }
  [Required] public required string AzureAppSecret { get; init; }
  [Required] public required string FromEmail { get; init; }
  public string TemplateDirectory { get; init; } = "EmailTemplates";
  [Required] public required string MailCheckApiKey { get; init; }
}

public interface IEmailService
{
  Task SendEmail(string recipient, string subject, string templateName,
    Dictionary<string, object> templateData, FileAttachment? attachment = null);

  Task<bool> ValidateEmail(string email);
}

public class EmailService(
  GraphServiceClient graphClient,
  EmailConfig config,
  ITemplateService templateService,
  IMemoryCache cache)
  : IEmailService
{
  private readonly ILogger _log = Log.ForContext<EmailService>();

  public async Task SendEmail(string recipient, string subject, string templateName,
    Dictionary<string, object> templateData, FileAttachment? attachment = null)
  {
    string bodyContent;
    try
    {
      bodyContent = await templateService.ApplyTemplate(templateName, templateData);

      if (string.IsNullOrEmpty(bodyContent))
      {
        throw new Exception("Email body content is empty");
      }
    }
    catch (Exception e)
    {
      _log.Error(e, "Error applying email template: {TemplateName} for recipient {Recipient}", templateName, recipient);
      throw;
    }

    var message = new Message
    {
      Subject = subject,
      From = new Recipient
      {
        EmailAddress = new EmailAddress
        {
          Address = config.FromEmail
        }
      },
      Body = new ItemBody
      {
        ContentType = BodyType.Html,
        Content = bodyContent
      },
      ToRecipients =
      [
        new Recipient
        {
          EmailAddress = new EmailAddress
          {
            Address = recipient
          }
        }
      ],
      Attachments = []
    };

    if (attachment != null)
    {
      message.Attachments.Add(attachment);
    }

    var requestBody = new SendMailPostRequestBody
    {
      Message = message,
      SaveToSentItems = message.ToRecipients.Any(r => r.EmailAddress!.Address != config.FromEmail)
    };

    try
    {
      _log.Information("Attempting to send email with configuration: {@EmailDetails}", new
      {
        config.FromEmail,
        ToEmail = recipient,
        Subject = subject,
        HasContent = !string.IsNullOrEmpty(message.Body.Content),
        ContentLength = message.Body.Content?.Length,
        AttachmentSize = attachment?.Size,
        config.AzureAppId
      });

      var emailAccount = graphClient.Users[config.FromEmail];

      await emailAccount.SendMail.PostAsync(requestBody);
    }
    catch (Exception e)
    {
      _log.Error(e, "Error sending email. Request details: {@RequestDetails}", new
      {
        config.FromEmail,
        ToEmail = recipient,
        Subject = subject,
        MessageId = message.Id,
        ErrorType = e.GetType().Name,
        ErrorMessage = e.Message,
        InnerError = e.InnerException?.Message
      });
      throw;
    }
  }

  public async Task<bool> ValidateEmail(string email)
  {
    var domain = email.Split('@').Last();

    // Check cache first
    if (cache.TryGetValue(domain, out EmailValidationResponse? cachedResult))
    {
      return ValidateWithCachedResult(email, cachedResult!);
    }

    _log.Information("Validating email {Email}", email);

    var client = new RestClient("https://mailcheck.p.rapidapi.com");
    var request = new RestRequest($"/?domain={domain}");
    request.AddHeader("x-rapidapi-host", "mailcheck.p.rapidapi.com");
    request.AddHeader("x-rapidapi-key", config.MailCheckApiKey);
    var response = await client.ExecuteAsync(request);

    if (response.StatusCode != System.Net.HttpStatusCode.OK)
    {
      _log.Error("Email validation service returned non-success status code: {StatusCode}", response.StatusCode);
      throw new InvalidOperationException("Email validation service error");
    }

    var result = Utils.Deserialize<EmailValidationResponse>(response.Content!);

    if (result == null)
    {
      _log.Error("Failed to deserialize email validation response");
      throw new InvalidOperationException("Email validation response deserialization error");
    }

    // Cache the result
    cache.Set(domain, result, TimeSpan.FromHours(24));

    return ValidateWithCachedResult(email, result);
  }

  private bool ValidateWithCachedResult(string email, EmailValidationResponse result)
  {
    try
    {
      if (!result.Valid)
      {
        ThrowInvalidEmailException("Invalid email", email, DetermineInvalidReason(result));
      }

      if (result.Block)
      {
        ThrowInvalidEmailException("Blocked email detected", email, InvalidEmailReason.InvalidDomain);
      }

      if (result.Disposable)
      {
        ThrowInvalidEmailException("Disposable email detected", email, InvalidEmailReason.DisposableEmail);
      }

      if (result.Risk > 70) // Adjust this threshold as needed
      {
        ThrowInvalidEmailException($"High risk email detected. Risk score: {result.Risk}", email,
          InvalidEmailReason.InvalidDomain);
      }
    }
    catch (Exception e)
    {
      _log.Error(e, "Invalid email detected: {@Result}", result);
      throw;
    }

    return true;
  }

  private void ThrowInvalidEmailException(string message, string email, InvalidEmailReason reason)
  {
    _log.Warning("{Message}: {Email} ({Reason})", message, email, reason);
    throw new InvalidEmailException(message, email, reason);
  }

  private InvalidEmailReason DetermineInvalidReason(EmailValidationResponse result)
  {
    if (result.Reason.Contains("syntax", StringComparison.OrdinalIgnoreCase))
    {
      return InvalidEmailReason.InvalidSyntax;
    }

    if (result.PossibleTypo.Length > 0)
    {
      return InvalidEmailReason.PossibleTypo;
    }

    if (result.Reason.Contains("domain", StringComparison.OrdinalIgnoreCase))
    {
      return InvalidEmailReason.InvalidDomain;
    }

    // Default to InvalidDomain if we can't determine a more specific reason
    return InvalidEmailReason.InvalidDomain;
  }
}

public class EmailValidationResponse
{
  [JsonPropertyName("valid")] public bool Valid { get; set; }
  [JsonPropertyName("block")] public bool Block { get; set; }
  [JsonPropertyName("disposable")] public bool Disposable { get; set; }
  [JsonPropertyName("email_forwarder")] public bool EmailForwarder { get; set; }
  [JsonPropertyName("domain")] public required string Domain { get; set; }
  [JsonPropertyName("text")] public required string Text { get; set; }
  [JsonPropertyName("reason")] public required string Reason { get; set; }
  [JsonPropertyName("risk")] public int Risk { get; set; }
  [JsonPropertyName("mx_host")] public required string MxHost { get; set; }
  [JsonPropertyName("possible_typo")] public required string[] PossibleTypo { get; set; }
  [JsonPropertyName("mx_ip")] public required string MxIp { get; set; }
  [JsonPropertyName("mx_info")] public required string MxInfo { get; set; }
  [JsonPropertyName("last_changed_at")] public DateTime LastChangedAt { get; set; }
}

public enum InvalidEmailReason
{
  InvalidSyntax,
  PossibleTypo,
  InvalidDomain,
  DisposableEmail
}

public class InvalidEmailException(string message, string email, InvalidEmailReason reason) : Exception(message)
{
  public string Email { get; } = email;
  public InvalidEmailReason Reason { get; } = reason;
}

public interface ITemplateService
{
  Task<string> ApplyTemplate(string templateName, Dictionary<string, object> templateData);
}

public class TemplateService : ITemplateService
{
  private readonly string _templateDirectory;
  private readonly Dictionary<string, HandlebarsTemplate<object, object>> _compiledTemplates;
  private readonly IFileService _fileService;

  public TemplateService(EmailConfig config, IFileService fileService)
  {
    _fileService = fileService;
    _templateDirectory = config.TemplateDirectory;
    _compiledTemplates = new Dictionary<string, HandlebarsTemplate<object, object>>();
    CompileBaseTemplate();
  }

  private void CompileBaseTemplate()
  {
    var baseTemplatePath = Path.Combine(_templateDirectory, "base.html");
    var baseTemplateContent = _fileService.GetFile(baseTemplatePath);
    _compiledTemplates["base"] = Handlebars.Compile(baseTemplateContent);
  }

  public async Task<string> ApplyTemplate(string templateName, Dictionary<string, object> templateData)
  {
    if (!_compiledTemplates.TryGetValue(templateName, out var template))
    {
      var templatePath = Path.Combine(_templateDirectory, $"{templateName}.html");
      var templateContent = await _fileService.GetFileAsync(templatePath);
      template = Handlebars.Compile(templateContent);
      _compiledTemplates[templateName] = template;
    }

    var content = template(templateData);
    templateData["content"] = content;

    return _compiledTemplates["base"](templateData);
  }
}

public class TemplateConfig
{
  public required string TemplateDirectory { get; set; }
}