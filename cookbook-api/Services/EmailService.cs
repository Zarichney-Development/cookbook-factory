using HandlebarsDotNet;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Serilog;
using ILogger = Serilog.ILogger;
using SendMailPostRequestBody = Microsoft.Graph.Users.Item.SendMail.SendMailPostRequestBody;

namespace Cookbook.Factory.Services;

public interface IEmailService
{
    Task SendEmail(string recipient, string subject, string htmlContent,
        Dictionary<string, object> templateData);
}

public class EmailService(GraphServiceClient graphClient, EmailConfig config, ITemplateService templateService)
    : IEmailService
{
    private readonly ILogger _log = Log.ForContext<EmailService>();

    public async Task SendEmail(string recipient, string subject, string htmlContent,
        Dictionary<string, object> templateData)
    {
        var message = new Message
        {
            Subject = subject,
            Body = new ItemBody
            {
                ContentType = BodyType.Html,
                Content = templateService.ApplyTemplate(htmlContent, templateData)
            },
            ToRecipients = new List<Recipient>
            {
                new()
                {
                    EmailAddress = new EmailAddress
                    {
                        Address = recipient
                    }
                }
            }
        };

        var requestBody = new SendMailPostRequestBody
        {
            Message = message,
            SaveToSentItems = true
        };

        try
        {
            await graphClient.Users[config.FromEmail].SendMail.PostAsync(requestBody);
        }
        catch (Exception e)
        {
            _log.Error(e, "Error sending email");
            throw;
        }
    }
}

public interface ITemplateService
{
    string ApplyTemplate(string templateName, Dictionary<string, object> templateData);
}

public class TemplateService : ITemplateService
{
    private readonly string _templateDirectory;
    private readonly Dictionary<string, HandlebarsTemplate<object, object>> _compiledTemplates;

    public TemplateService( EmailConfig config)
    {
        _templateDirectory = config.TemplateDirectory;
        _compiledTemplates = new Dictionary<string, HandlebarsTemplate<object, object>>();
        CompileBaseTemplate();
    }

    private void CompileBaseTemplate()
    {
        var baseTemplatePath = Path.Combine(_templateDirectory, "base.html");
        var baseTemplateContent = File.ReadAllText(baseTemplatePath);
        _compiledTemplates["base"] = Handlebars.Compile(baseTemplateContent);
    }

    public string ApplyTemplate(string templateName, Dictionary<string, object> templateData)
    {
        if (!_compiledTemplates.TryGetValue(templateName, out var template))
        {
            var templatePath = Path.Combine(_templateDirectory, $"{templateName}.html");
            var templateContent = File.ReadAllText(templatePath);
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