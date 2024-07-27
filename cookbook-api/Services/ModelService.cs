using System.Text.Json;
using OpenAI;
using OpenAI.Chat;
using Serilog;
using ILogger = Serilog.ILogger;

namespace Cookbook.Factory.Services;

public interface IModelService
{
    Task<T> CallFunction<T>(string systemPrompt, string userPrompt, string functionName, string functionDescription,
        string functionParameters);
}

public class ModelService(OpenAIClient client, IConfiguration configuration)
    : IModelService
{
    private readonly ILogger _log = Log.ForContext<ModelService>();
    private const string DefaultModel = "gpt-4o-mini";

    public async Task<T> CallFunction<T>(string systemPrompt, string userPrompt, string functionName,
        string functionDescription, string functionParameters)
    {
        _log.Information(
            "Getting response from model. System prompt: {systemPrompt}, User prompt: {userPrompt}, Function name: {functionName}, Function description: {functionDescription}, Function parameters: {functionParameters}",
            systemPrompt, userPrompt, functionName, functionDescription, functionParameters);

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

        var model = configuration["OpenAI:ModelName"] ?? DefaultModel;
        var chatClient = client.GetChatClient(model);

        ChatCompletion chatCompletion;
        try
        {
            chatCompletion = await chatClient.CompleteChatAsync(messages, options);
        }
        catch (Exception e)
        {
            _log.Error(e, "Error occurred while getting response from model");
            throw;
        }

        if (chatCompletion.FinishReason != ChatFinishReason.ToolCalls)
        {
            throw new Exception("Failed to get a valid response from the model.");
        }

        foreach (var toolCall in chatCompletion.ToolCalls)
        {
            if (toolCall.FunctionName != functionName)
            {
                _log.Error("Expected function name {functionName} but got {toolCall.FunctionName}",
                    functionName, toolCall.FunctionName);
                continue;
            }

            try
            {
                var result = JsonSerializer.Deserialize<T>(toolCall.FunctionArguments,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                if (result != null)
                {
                    return result;
                }
            }
            catch (Exception e)
            {
                _log.Error(e,
                    "Failed to deserialize tool call arguments to type {type}, attempted to deserialize {FunctionArguments}",
                    typeof(T).Name, toolCall.FunctionArguments);
                throw;
            }
        }

        throw new Exception("Failed to get a valid response from the model.");
    }
}