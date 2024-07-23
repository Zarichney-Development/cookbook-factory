using System.Text.Json;
using OpenAI;
using OpenAI.Chat;

namespace Cookbook.Factory.Services;

public interface IModelService
{
    Task<T> GetResponseAsync<T>(string systemPrompt, string userPrompt, string functionName, string functionDescription,
        string functionParameters);
}

public class ModelService : IModelService
{
    private readonly OpenAIClient _client;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ModelService> _logger;
    private const string DefaultModel = "gpt-4o-mini";

    public ModelService(OpenAIClient client, IConfiguration configuration, ILogger<ModelService> logger)
    {
        _client = client;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<T> GetResponseAsync<T>(string systemPrompt, string userPrompt, string functionName,
        string functionDescription, string functionParameters)
    {
        _logger.LogInformation($"Getting response from model");
        _logger.LogInformation($"System prompt: {systemPrompt}");
        _logger.LogInformation($"User prompt: {userPrompt}");
        
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(userPrompt)
        };

        var functionTool = ChatTool.CreateFunctionTool(
            functionName: functionName,
            functionDescription: functionDescription,
            functionParameters: BinaryData.FromString(functionParameters)
        );

        var options = new ChatCompletionOptions
        {
            Tools = { functionTool }
        };

        var model = _configuration["OpenAI:ModelName"] ?? DefaultModel;
        var chatClient = _client.GetChatClient(model);

        try
        {
            ChatCompletion chatCompletion = await chatClient.CompleteChatAsync(messages, options);

            if (chatCompletion.FinishReason == ChatFinishReason.ToolCalls)
            {
                foreach (var toolCall in chatCompletion.ToolCalls)
                {
                    if (toolCall.FunctionName == functionName)
                    {
                        var result = JsonSerializer.Deserialize<T>(toolCall.FunctionArguments, new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        });
                        
                        if (result != null)
                        {
                            return result;
                        }
                    }
                }
            }

            throw new Exception("Failed to get a valid response from the model.");
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error occurred while getting response from model");
            throw;
        }
    }
}