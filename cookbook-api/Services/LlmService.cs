using System.Text.Json;
using AutoMapper;
using Cookbook.Factory.Config;
using Cookbook.Factory.Prompts;
using OpenAI;
using OpenAI.Assistants;
using OpenAI.Chat;
using Polly;
using Polly.Retry;
using ILogger = Serilog.ILogger;

namespace Cookbook.Factory.Services;

public class LlmConfig : IConfig
{
    public string ModelName { get; init; } = LlmModels.Gpt4Omini;
    public int RetryAttempts { get; init; } = 5;
}

public static class LlmModels
{
    public const string Gpt4Omini = "gpt-4o-mini";
}

public interface ILlmService
{
    Task<string> CreateAssistant(PromptBase prompt);
    Task<string> CreateThread();
    Task CreateMessage(string threadId, string content, MessageRole role = MessageRole.User);

    Task<string> CreateRun(string threadId, string assistantId, bool toolConstraintRequired = false);

    Task<ChatCompletion> GetCompletion(List<ChatMessage> messages, ChatCompletionOptions? options = null, int? retryCount = null);

    Task<(bool isComplete, RunStatus status)> GetRun(string threadId, string runId);
    Task<string> CancelRun(string threadId, string runId);
    Task SubmitToolOutputsToRun(string threadId, string runId, List<(string toolCallId, string output)> toolOutputs);
    Task DeleteAssistant(string assistantId);
    Task DeleteThread(string threadId);
    Task<T> CallFunction<T>(string systemPrompt, string userPrompt, FunctionDefinition function, int? retryCount = null);
    Task<T> GetRunAction<T>(string threadId, string runId, string functionName);
    Task<string> GetToolCallId(string threadId, string runId, string functionName);
}

public class LlmService(OpenAIClient client, IMapper mapper, LlmConfig config) : ILlmService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<LlmService>();

    private readonly AsyncRetryPolicy _retryPolicy = Policy
        .Handle<Exception>()
        .WaitAndRetryAsync(
            retryCount: config.RetryAttempts,
            sleepDurationProvider: _ => TimeSpan.FromSeconds(1),
            onRetry: (exception, _, retryCount, context) =>
            {
                Log.Warning(exception, "LLM attempt {retryCount}: Retrying due to {exception}. Retry Context: {@Context}",
                    retryCount, exception.Message, context);
            }
        );
    
    private async Task<T> ExecuteWithRetry<T>(int? retryCount, Func<Task<T>> action)
    {
        var policy = Policy
            .Handle<Exception>()
            .WaitAndRetryAsync(
                retryCount: retryCount ?? config.RetryAttempts,
                sleepDurationProvider: _ => TimeSpan.FromSeconds(1),
                onRetry: (exception, _, currentRetry, context) =>
                {
                    Log.Warning(exception, "LLM attempt {retryCount}: Retrying due to {exception}. Retry Context: {@Context}",
                        currentRetry, exception.Message, context);
                }
            );

        return await policy.ExecuteAsync(action);
    }

    public async Task<string> CreateAssistant(PromptBase prompt)
    {
        try
        {
            var assistantClient = client.GetAssistantClient();
            var functionToolDefinition = mapper.Map<FunctionToolDefinition>(prompt.GetFunction());
            functionToolDefinition.StrictParameterSchemaEnabled = true;
            var response = await assistantClient.CreateAssistantAsync(prompt.Model ?? config.ModelName,
                new AssistantCreationOptions
                {
                    Name = prompt.Name,
                    Description = prompt.Description,
                    Instructions = prompt.SystemPrompt,
                    Tools = { functionToolDefinition },
                });
            return response.Value.Id;
        }
        catch (Exception e)
        {
            Log.Error(e, "Error occurred while creating assistant");
            throw;
        }
    }

    public async Task<string> CreateThread()
    {
        try
        {
            var assistantClient = client.GetAssistantClient();
            var response = await assistantClient.CreateThreadAsync();
            return response.Value.Id;
        }
        catch (Exception e)
        {
            Log.Error(e, "Error occurred while creating thread");
            throw;
        }
    }

    public async Task CreateMessage(string threadId, string content, MessageRole role = MessageRole.User)
    {
        try
        {
            var assistantClient = client.GetAssistantClient();
            await assistantClient.CreateMessageAsync(
                threadId,
                role,
                new[] { MessageContent.FromText(content) }
            );
        }
        catch (Exception e)
        {
            Log.Error(e, "Error occurred while creating message");
            throw;
        }
    }

    public async Task<string> CreateRun(string threadId, string assistantId, bool toolConstraintRequired = false)
    {
        try
        {
            var assistantClient = client.GetAssistantClient();
            var response = await assistantClient.CreateRunAsync(threadId, assistantId, new RunCreationOptions
            {

                ToolConstraint = toolConstraintRequired ? ToolConstraint.Required : ToolConstraint.None,
            });

            var threadRun = response.Value;

            return threadRun.Id;
        }
        catch (Exception e)
        {
            Log.Error(e, "Error occurred while creating run for thread {threadId}, assistant {assistantId}", threadId,
                assistantId);
            throw;
        }
    }

    public async Task<(bool isComplete, RunStatus status)> GetRun(string threadId, string runId)
    {
        try
        {
            var assistantClient = client.GetAssistantClient();
            var runResult = await assistantClient.GetRunAsync(threadId, runId);
            var run = runResult.Value;
            return (run.Status.IsTerminal, run.Status);
        }
        catch (Exception e)
        {
            Log.Error(e, "Error occurred while getting run {runId} for thread {threadId}", runId, threadId);
            throw;
        }
    }

    public async Task<string> CancelRun(string threadId, string runId)
    {
        Log.Information("Cancelling run: {runId} for thread: {threadId}", runId, threadId);
        try
        {
            var assistantClient = client.GetAssistantClient();

            var run = await GetRun(threadId, runId);
            if (run.isComplete)
            {
                return "Run is already complete.";
            }

            var responseResult = await assistantClient.CancelRunAsync(threadId, runId);

            var response = responseResult.Value
                           ?? throw new Exception("Failed to cancel run. Response was null.");

            return response.Status.ToString()!;
        }
        catch (Exception e)
        {
            Log.Error(e, "Error occurred while cancelling run {runId} for thread {threadId}", runId, threadId);
            return "Failed to cancel run.";
        }
    }

    public async Task SubmitToolOutputsToRun(string threadId, string runId,
        List<(string toolCallId, string output)> toolOutputs)
    {
        try
        {
            var assistantClient = client.GetAssistantClient();
            await assistantClient.SubmitToolOutputsToRunAsync(threadId, runId,
                toolOutputs.Select(to => new ToolOutput(to.toolCallId, to.output)).ToList());
        }
        catch (Exception e)
        {
            Log.Error(e, "Error occurred while submitting tool outputs for run {runId}, thread {threadId}", runId,
                threadId);
            throw;
        }
    }

    public async Task DeleteAssistant(string assistantId)
    {
        try
        {
            var assistantClient = client.GetAssistantClient();
            await assistantClient.DeleteAssistantAsync(assistantId);
        }
        catch (Exception e)
        {
            Log.Error(e, "Error occurred while deleting assistant {assistantId}", assistantId);
        }
    }

    public async Task DeleteThread(string threadId)
    {
        try
        {
            var assistantClient = client.GetAssistantClient();
            await assistantClient.DeleteThreadAsync(threadId);
        }
        catch (Exception e)
        {
            Log.Error(e, "Error occurred while deleting thread {threadId}", threadId);
        }
    }

    public async Task<T> CallFunction<T>(string systemPrompt, string userPrompt, FunctionDefinition function, int? retryCount = null)
    {
        Log.Information(
            "Getting response from model. System prompt: {systemPrompt}, User prompt: {userPrompt}, Function: {@function}",
            systemPrompt, userPrompt, function);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(userPrompt)
        };

        var result =  await CallFunction<T>(function, messages, retryCount);

        return result;
    }


    private async Task<T> CallFunction<T>(FunctionDefinition function, List<ChatMessage> messages, int? retryCount = null)
        => await CallFunction<T>(messages, ChatTool.CreateFunctionTool(
            functionName: function.Name,
            functionDescription: function.Description,
            functionParameters: BinaryData.FromString(function.Parameters)
        ), retryCount);

    private async Task<T> CallFunction<T>(List<ChatMessage> messages, ChatTool functionTool, int? retryCount = null)
    {
        var chatCompletion = await GetCompletion(messages, new ChatCompletionOptions
        {
            Tools = { functionTool },
            AllowParallelToolCalls = false
        }, retryCount);

        if (chatCompletion.FinishReason != ChatFinishReason.ToolCalls)
        {
            throw new Exception("Failed to get a valid response from the model.");
        }

        foreach (var toolCall in chatCompletion.ToolCalls)
        {
            if (toolCall.FunctionName != functionTool.FunctionName)
            {
                Log.Error("Expected function name {functionName} but got {toolCall.FunctionName}",
                    functionTool.FunctionName, toolCall.FunctionName);
                continue;
            }

            try
            {
                Log.Information("Function arguments: {FunctionArguments}", toolCall.FunctionArguments);
                using var argumentsJson = JsonDocument.Parse(toolCall.FunctionArguments);

                Log.Information("Deserializing tool call arguments to type {type}", typeof(T).Name);
                var result = Utils.Deserialize<T>(argumentsJson);

                if (result != null)
                {
                    return result;
                }
            }
            catch (Exception e)
            {
                Log.Error(e,
                    "Failed to deserialize tool call arguments to type {type}, attempted to deserialize {FunctionArguments}",
                    typeof(T).Name, toolCall.FunctionArguments);
                throw;
            }
        }

        throw new Exception("Failed to get a valid response from the model.");
    }

    public async Task<ChatCompletion> GetCompletion(List<ChatMessage> messages,
        ChatCompletionOptions? options = null, int? retryCount = null)
    {
        return await ExecuteWithRetry(retryCount, async () =>
        {
            var chatClient = client.GetChatClient(config.ModelName);

            try
            {
                Log.Information("Sending prompt to model");

                var result = await chatClient.CompleteChatAsync(messages, options);

                Log.Information("Received response from model: {@result}", result);

                return result;
            }
            catch (Exception e)
            {
                Log.Error(e, "Error occurred while getting response from model");
                throw;
            }
        });
    }

    public async Task<T> GetRunAction<T>(string threadId, string runId, string functionName)
    {
        return await _retryPolicy.ExecuteAsync(async () =>
        {
            try
            {
                var assistantClient = client.GetAssistantClient();
                var runResult = await assistantClient.GetRunAsync(threadId, runId);
                var run = runResult.Value;

                if (run.Status != RunStatus.RequiresAction)
                {
                    throw new InvalidOperationException($"Run status is {run.Status}, expected RequiresAction");
                }

                var action = run.RequiredActions.FirstOrDefault(a => a.FunctionName == functionName);
                if (action == null)
                {
                    throw new InvalidOperationException($"No action found for function {functionName}");
                }


                return Utils.Deserialize<T>(action.FunctionArguments);
            }
            catch (Exception e)
            {
                Log.Error(e, "Error occurred while getting run action");
                throw;
            }
        });
    }

    public async Task<string> GetToolCallId(string threadId, string runId, string functionName)
    {
        try
        {
            var assistantClient = client.GetAssistantClient();
            var runResult = await assistantClient.GetRunAsync(threadId, runId);
            var run = runResult.Value;

            if (run.Status != RunStatus.RequiresAction)
            {
                throw new InvalidOperationException($"Run status is {run.Status}, expected RequiresAction");
            }

            var action = run.RequiredActions.FirstOrDefault(a => a.FunctionName == functionName);
            if (action == null)
            {
                throw new InvalidOperationException($"No action found for function {functionName}");
            }

            return action.ToolCallId;
        }
        catch (Exception e)
        {
            Log.Error(e, "Error occurred while getting tool call ID");
            throw;
        }
    }
}