using System.ClientModel;
using System.Collections;
using AutoMapper;
using Cookbook.Factory.Prompts;
using OpenAI;
using OpenAI.Assistants;
using OpenAI.Chat;
using Serilog;
using ILogger = Serilog.ILogger;

namespace Cookbook.Factory.Services;

public static class LlmModels
{
    public const string Gpt4Omini = "gpt-4o-mini";
}

public interface ILlmService
{
    Task<string> CreateAssistant(PromptBase prompt);
    Task<string> CreateThread();
    Task CreateMessage(string threadId, string content, MessageRole role = MessageRole.User);

    Task<string> CreateRun(string threadId, string assistantId, bool toolConstraintRequired = false,
        bool parallelToolCallsEnabled = false);

    Task<ChatCompletion> GetCompletion(IEnumerable<ChatMessage> messages, ChatCompletionOptions? options = null);

    Task<(bool isComplete, RunStatus status)> GetRun(string threadId, string runId);
    Task<string> CancelRun(string threadId, string runId);
    Task SubmitToolOutputsToRun(string threadId, string runId, List<(string toolCallId, string output)> toolOutputs);
    Task DeleteAssistant(string assistantId);
    Task DeleteThread(string threadId);
    Task<T> CallFunction<T>(string systemPrompt, string userPrompt, FunctionDefinition function);
    Task<T> GetRunAction<T>(string threadId, string runId, string functionName);
    Task<string> GetToolCallId(string threadId, string runId, string functionName);
}

public class LlmService(OpenAIClient client, IMapper mapper, IConfiguration configuration) : ILlmService
{
    private readonly ILogger _log = Log.ForContext<LlmService>();
    private readonly string _defaultModel = configuration["OpenAI:ModelName"] ?? LlmModels.Gpt4Omini;

    public async Task<string> CreateAssistant(PromptBase prompt)
    {
        try
        {
            var assistantClient = client.GetAssistantClient();
            var functionToolDefinition = mapper.Map<FunctionToolDefinition>(prompt.GetFunction());
            var response = await assistantClient.CreateAssistantAsync(configuration["OpenAI:ModelName"] ?? prompt.Model,
                new AssistantCreationOptions
                {
                    Name = prompt.Name,
                    Description = prompt.Description,
                    Instructions = prompt.SystemPrompt,
                    Tools = { functionToolDefinition }
                });
            return response.Value.Id;
        }
        catch (Exception e)
        {
            _log.Error(e, "Error occurred while creating assistant");
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
            _log.Error(e, "Error occurred while creating thread");
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
            _log.Error(e, "Error occurred while creating message");
            throw;
        }
    }

    public async Task<string> CreateRun(string threadId, string assistantId, bool toolConstraintRequired = false,
        bool parallelToolCallsEnabled = false)
    {
        try
        {
            var assistantClient = client.GetAssistantClient();
            var response = await assistantClient.CreateRunAsync(threadId, assistantId, new RunCreationOptions
            {
                ParallelToolCallsEnabled = parallelToolCallsEnabled,
                ToolConstraint = toolConstraintRequired ? ToolConstraint.Required : ToolConstraint.None,
            });

            var threadRun = response.Value;

            return threadRun.Id;
        }
        catch (Exception e)
        {
            _log.Error(e, "Error occurred while creating run for thread {threadId}, assistant {assistantId}", threadId,
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
            _log.Error(e, "Error occurred while getting run {runId} for thread {threadId}", runId, threadId);
            throw;
        }
    }

    public async Task<string> CancelRun(string threadId, string runId)
    {
        _log.Information("Cancelling run: {runId} for thread: {threadId}", runId, threadId);
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
            _log.Error(e, "Error occurred while cancelling run {runId} for thread {threadId}", runId, threadId);
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
            _log.Error(e, "Error occurred while submitting tool outputs for run {runId}, thread {threadId}", runId,
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
            _log.Error(e, "Error occurred while deleting assistant {assistantId}", assistantId);
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
            _log.Error(e, "Error occurred while deleting thread {threadId}", threadId);
        }
    }

    public async Task<T> CallFunction<T>(string systemPrompt, string userPrompt, FunctionDefinition function)
    {
        _log.Information(
            "Getting response from model. System prompt: {systemPrompt}, User prompt: {userPrompt}, Function name: {functionName}, Function description: {functionDescription}, Function parameters: {functionParameters}",
            systemPrompt, userPrompt, function.Name, function.Description, function.Parameters);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(userPrompt)
        };

        return await CallFunction<T>(function, messages);
    }


    private async Task<T> CallFunction<T>(FunctionDefinition function, IEnumerable<ChatMessage> messages)
        => await CallFunction<T>(messages, ChatTool.CreateFunctionTool(
            functionName: function.Name,
            functionDescription: function.Description,
            functionParameters: BinaryData.FromString(function.Parameters)
        ));

    public async Task<ChatCompletion> GetCompletion(IEnumerable<ChatMessage> messages, ChatCompletionOptions? options = null)
    {
        var chatClient = client.GetChatClient(_defaultModel);

        try
        {
            return await chatClient.CompleteChatAsync(messages, options);
        }
        catch (Exception e)
        {
            _log.Error(e, "Error occurred while getting response from model");
            throw;
        }
    }

    private async Task<T> CallFunction<T>(IEnumerable<ChatMessage> messages, ChatTool functionTool)
    {
        var chatCompletion = await GetCompletion(messages, new ChatCompletionOptions
        {
            Tools = { functionTool }
        });

        if (chatCompletion.FinishReason != ChatFinishReason.ToolCalls)
        {
            throw new Exception("Failed to get a valid response from the model.");
        }

        foreach (var toolCall in chatCompletion.ToolCalls)
        {
            if (toolCall.FunctionName != functionTool.FunctionName)
            {
                _log.Error("Expected function name {functionName} but got {toolCall.FunctionName}",
                    functionTool.FunctionName, toolCall.FunctionName);
                continue;
            }

            try
            {
                var result = Utils.Deserialize<T>(toolCall.FunctionArguments);

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

    public async Task<T> GetRunAction<T>(string threadId, string runId, string functionName)
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
            _log.Error(e, "Error occurred while getting run action");
            throw;
        }
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
            _log.Error(e, "Error occurred while getting tool call ID");
            throw;
        }
    }
}