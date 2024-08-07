using System.Collections.Concurrent;
using System.Text.Json;
using AutoMapper;
using Cookbook.Factory.Models;
using Cookbook.Factory.Prompts;
using OpenAI.Assistants;
using OpenAI.Chat;
using Serilog;
using ILogger = Serilog.ILogger;

namespace Cookbook.Factory.Services;

public class RecipeConfig : IConfig
{
    public int DontKeepScoreThreshold { get; init; } = 40;
    public int AcceptableScoreThreshold { get; init; } = 75;
    public int QualityScoreThreshold { get; init; } = 80;
    public int MinRelevantRecipes { get; init; } = 5;
    public int MaxNewRecipeNameAttempts { get; init; } = 3;
    public int MaxParallelTasks { get; init; } = 5; // Same as MinRelevantRecipes
    public string OutputDirectory { get; init; } = "Recipes";
}

public class RecipeService(
    RecipeConfig config,
    ILlmService llmService,
    WebScraperService webscraper,
    FileService fileService,
    IMapper mapper,
    RankRecipePrompt rankRecipePrompt,
    CleanRecipePrompt cleanRecipePrompt,
    SynthesizeRecipePrompt synthesizeRecipePrompt,
    AnalyzeRecipePrompt analyzeRecipePrompt,
    ProcessOrderPrompt processOrderPrompt
)
{
    private readonly ILogger _log = Log.ForContext<RecipeService>();

    private readonly SemaphoreSlim _semaphore = new(config.MaxParallelTasks);
    
    public async Task<List<Recipe>> GetRecipes(string requestedRecipeName, CookbookOrder cookbookOrder)
    {
        var recipeName = requestedRecipeName;
        var recipes = await GetRecipes(recipeName);

        var previousAttempts = new List<string>();

        var acceptableScore = config.AcceptableScoreThreshold;

        var attempts = 0;
        while (recipes.Count == 0)
        {
            _log.Warning("No recipes found for '{RecipeName}'", recipeName);

            if (++attempts > config.MaxNewRecipeNameAttempts)
            {
                _log.Warning("Aborting recipe searching for '{RecipeName}'", requestedRecipeName);
                throw new Exception("No recipes found");
            }
            
            // Lower the bar of acceptable for every attempt
            acceptableScore -= 10;
            
            recipes = await GetRecipes(recipeName, acceptableScore, false);
            
            if (recipes.Count > 0)
            {
                break;
            }
            
            // Request for new query to scrap with
            recipeName = await GetReplacementRecipeName(requestedRecipeName, cookbookOrder, previousAttempts);

            _log.Information("Attempting to find a replacement recipe name for '{RecipeName}' using '{NewRecipeName}'", requestedRecipeName, recipeName);
            
            recipes = await GetRecipes(recipeName, acceptableScore);

            if (recipes.Count == 0)
            {
                previousAttempts.Add(recipeName);
            }
        }

        return recipes;
    }

    private async Task<string> GetReplacementRecipeName(string recipeName, CookbookOrder cookbookOrder,
        List<string> previousAttempts)
    {
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(processOrderPrompt.SystemPrompt),
            new UserChatMessage(processOrderPrompt.GetUserPrompt(cookbookOrder)),
            new SystemChatMessage(cookbookOrder.Recipes.ToString()),
            new UserChatMessage(
                $"""
                 Thank you.
                 The following recipe name unfortunately did not result in any recipes: {recipeName}.
                 This may be because the recipe name is too unique or not well-known.
                 Please provide an alternative recipe name that should result in a similar search.
                 Respond with the alternative search result and nothing else.
                 """)
        };

        foreach (var previousAttempt in previousAttempts)
        {
            messages.Add(new SystemChatMessage(previousAttempt));
            messages.Add($"Sorry, that also didn't result in anything. Please try an alternative search query while respecting true to the original recipe of '{recipeName}'.");
        }

        var result = await llmService.GetCompletion(messages);

        return result.ToString()!;
    }

    public async Task<List<Recipe>> GetRecipes(string query, int? acceptableScore = null, bool scrape = true)
    {
        acceptableScore ??= config.AcceptableScoreThreshold;
        
        // First, attempt to find and load existing JSON file
        var recipes = await fileService.LoadExistingData<Recipe>(config.OutputDirectory, query);

        if (recipes.Any())
        {
            _log.Information("Found {count} cached recipes for query '{query}'.", recipes.Count, query);
        }
        else if (scrape)
        {
            // If no existing file found, proceed with web scraping
            recipes = mapper.Map<List<Recipe>>(await webscraper.ScrapeForRecipesAsync(query));
        }

        var rankedRecipes = await RankRecipesAsync(recipes, query, acceptableScore);

        // To save on llm cost, only bother cleaning those with a good enough score
        var topRecipes = rankedRecipes.Where(r => r.RelevancyScore >= acceptableScore).ToList();

        var cleanedRecipes = await CleanRecipesAsync(topRecipes.Where(r => !r.Cleaned).ToList());
        cleanedRecipes.AddRange(topRecipes.Where(r => r.Cleaned));

        var filteredOutRecipes = rankedRecipes
            .Where(r => r.RelevancyScore < acceptableScore)
            // Eliminate anything below the relevancy threshold
            .Where(r => r.RelevancyScore >= config.DontKeepScoreThreshold)
            .ToList();

        // Not sure how much value these lesser scores can be, but might as well store them, except for the irrelevant ones
        var allRecipes = cleanedRecipes.Concat(filteredOutRecipes).ToList();

        fileService.WriteToFile(config.OutputDirectory, query,
            allRecipes.OrderByDescending(r => r.RelevancyScore));

        return cleanedRecipes.OrderByDescending(r => r.RelevancyScore).ToList();
    }


    private async Task<RelevancyResult> RankRecipe(Recipe recipe, string query)
    {
        var result = await llmService.CallFunction<RelevancyResult>(
            rankRecipePrompt.SystemPrompt,
            rankRecipePrompt.GetUserPrompt(recipe, query),
            rankRecipePrompt.GetFunction()
        );

        _log.Information("Relevancy filter result: {@Result}", result);

        return result;
    }

    private async Task<List<Recipe>> RankRecipesAsync(IReadOnlyCollection<Recipe> recipes, string query, int? acceptableScore = null)
    {
        acceptableScore ??= config.AcceptableScoreThreshold;
        
        // Don't rank if there are already enough relevant recipes
        if (recipes.Count(r => r.RelevancyScore >= acceptableScore) >= config.MinRelevantRecipes)
        {
            return recipes.OrderByDescending(r => r.RelevancyScore).ToList();
        }

        var rankedRecipes = new ConcurrentQueue<Recipe>();
        var relevantRecipeCount = new AtomicCounter();
        var cts = new CancellationTokenSource();

        await Parallel.ForEachAsync(recipes,
            new ParallelOptions { MaxDegreeOfParallelism = config.MaxParallelTasks, CancellationToken = cts.Token },
            async (recipe, ct) =>
            {
                if (ct.IsCancellationRequested) return;

                if (recipe.RelevancyScore >= acceptableScore)
                {
                    rankedRecipes.Enqueue(recipe);
                    if (relevantRecipeCount.Increment() >=config.MinRelevantRecipes)
                    {
                        await cts.CancelAsync();
                    }

                    return;
                }

                await _semaphore.WaitAsync(ct);
                try
                {
                    var result = await RankRecipe(recipe, query);

                    recipe.RelevancyScore = result.RelevancyScore;
                    recipe.RelevancyReasoning = result.RelevancyReasoning;

                    rankedRecipes.Enqueue(recipe);

                    if (recipe.RelevancyScore >= acceptableScore &&
                        relevantRecipeCount.Increment() >= config.MinRelevantRecipes)
                    {
                        await cts.CancelAsync();
                    }
                }
                finally
                {
                    _semaphore.Release();
                }
            });

        return rankedRecipes.OrderByDescending(r => r.RelevancyScore).ToList();
    }

    private async Task<List<Recipe>> CleanRecipesAsync(List<Recipe> recipes)
    {
        if (!recipes.Any())
        {
            return new List<Recipe>();
        }
        
        var cleanedRecipes = new ConcurrentQueue<Recipe>();

        await Parallel.ForEachAsync(
            recipes.ToList(),
            new ParallelOptions { MaxDegreeOfParallelism = config.MaxParallelTasks },
            async (recipe, ct) =>
            {
                await _semaphore.WaitAsync(ct);
                try
                {
                    cleanedRecipes.Enqueue(await CleanRecipeData(recipe));
                }
                finally
                {
                    _semaphore.Release();
                }
            });

        return cleanedRecipes.ToList();
    }

    private async Task<Recipe> CleanRecipeData(Recipe rawRecipe)
    {
        if (rawRecipe.Cleaned)
        {
            return rawRecipe;
        }

        try
        {
            var cleanedRecipe = await llmService.CallFunction<Recipe>(
                cleanRecipePrompt.SystemPrompt,
                cleanRecipePrompt.GetUserPrompt(rawRecipe),
                cleanRecipePrompt.GetFunction()
            );

            _log.Information("Cleaned recipe data: {@CleanedRecipe}", cleanedRecipe);

            // Preserve fields that shouldn't be modified by the LLM
            cleanedRecipe.RecipeUrl = rawRecipe.RecipeUrl;
            cleanedRecipe.ImageUrl = rawRecipe.ImageUrl;
            cleanedRecipe.RelevancyScore = rawRecipe.RelevancyScore;
            cleanedRecipe.RelevancyReasoning = rawRecipe.RelevancyReasoning;
            cleanedRecipe.Cleaned = true;

            return cleanedRecipe;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error cleaning recipe data: {Message}", ex.Message);
            return rawRecipe;
        }
    }

    public async Task<(SynthesizedRecipe, List<SynthesizedRecipe>)> SynthesizeRecipe(List<Recipe> recipes,
        CookbookOrder order, string recipeName)
    {
        SynthesizedRecipe synthesizedRecipe;
        List<SynthesizedRecipe> rejectedRecipes = new();

        var synthesizingAssistantId = await llmService.CreateAssistant(synthesizeRecipePrompt);
        var analyzeAssistantId = await llmService.CreateAssistant(analyzeRecipePrompt);

        var synthesizingThreadId = await llmService.CreateThread();
        var analyzeThreadId = await llmService.CreateThread();

        string synthesizeRunId = null!;
        string analysisRunId = null!;

        _log.Information("[{Recipe}] Synthesizing using recipes: {@Recipes}", recipeName, recipes);

        try
        {
            var synthesizePrompt = synthesizeRecipePrompt.GetUserPrompt(recipes, order);
            await llmService.CreateMessage(synthesizingThreadId, synthesizePrompt);

            synthesizeRunId = await llmService.CreateRun(synthesizingThreadId, synthesizingAssistantId, true);

            var count = 0;

            while (true)
            {
                count++;

                synthesizedRecipe = await ProcessSynthesisRun(synthesizingThreadId, synthesizeRunId);

                _log.Information("[{Recipe} - Run {count}] Synthesized recipe: {@SynthesizedRecipe}", recipeName, count,
                    synthesizedRecipe);

                if (count == 1)
                {
                    var analysisPrompt = analyzeRecipePrompt.GetUserPrompt(synthesizedRecipe, order, recipeName);
                    await llmService.CreateMessage(analyzeThreadId, analysisPrompt);

                    analysisRunId = await llmService.CreateRun(analyzeThreadId, analyzeAssistantId, true);
                }
                else
                {
                    await ProvideRevisedRecipe(analyzeThreadId, analysisRunId, synthesizedRecipe);
                }

                var analysisResult = await ProcessAnalysisRun(analyzeThreadId, analysisRunId);

                _log.Information("[{Recipe} - Run {count}] Analysis result: {@Analysis}",
                    recipeName, count, analysisResult);

                synthesizedRecipe.AddAnalysisResult(analysisResult);

                if (analysisResult.QualityScore >= config.QualityScoreThreshold)
                {
                    break;
                }

                rejectedRecipes.Add(synthesizedRecipe);

                await ProvideAnalysisFeedback(synthesizingThreadId, synthesizeRunId, analysisResult);
            }

            _log.Information("[{Recipe}] Synthesized: {@Recipes}", recipeName, synthesizedRecipe);
        }
        catch (Exception ex)
        {
            _log.Error(ex,
                "[{Recipe}] Error synthesizing recipe: {Message}. Synthesizer Assistant: {SynthesizerAssistant}, Thread: {SynthesizingThread}, Run: {SynthesizingRun}. Analyzer Assistant: {AnalyzerAssistant}, Thread: {AnalyzeThread}, Run: {AnalyzeRun}",
                recipeName, ex.Message, synthesizingAssistantId, synthesizingThreadId, synthesizeRunId,
                analyzeAssistantId, analyzeThreadId, analysisRunId);
            throw;
        }
        finally
        {
            await llmService.CancelRun(analyzeThreadId, analysisRunId);
            await llmService.CancelRun(synthesizingThreadId, synthesizeRunId);
            await llmService.DeleteAssistant(synthesizingAssistantId);
            await llmService.DeleteAssistant(analyzeAssistantId);
            await llmService.DeleteThread(synthesizingThreadId);
            await llmService.DeleteThread(analyzeThreadId);
        }

        return (synthesizedRecipe, rejectedRecipes);
    }

    private async Task<SynthesizedRecipe> ProcessSynthesisRun(string threadId, string runId)
    {
        while (true)
        {
            var (isComplete, status) = await llmService.GetRun(threadId, runId);

            if (isComplete)
            {
                throw new Exception("Synthesis run completed without producing a recipe");
            }

            if (status == RunStatus.RequiresAction)
            {
                return await llmService.GetRunAction<SynthesizedRecipe>(threadId, runId,
                    synthesizeRecipePrompt.GetFunction().Name);
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

    private async Task<RecipeAnalysis> ProcessAnalysisRun(string threadId, string runId)
    {
        while (true)
        {
            var (isComplete, status) = await llmService.GetRun(threadId, runId);

            if (isComplete)
            {
                throw new Exception("Analysis run completed without producing a result");
            }

            if (status == RunStatus.RequiresAction)
            {
                return await llmService.GetRunAction<RecipeAnalysis>(threadId, runId,
                    analyzeRecipePrompt.GetFunction().Name);
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

    private async Task ProvideRevisedRecipe(string threadId, string runId, SynthesizedRecipe recipe)
    {
        var toolCallId = await llmService.GetToolCallId(threadId, runId, analyzeRecipePrompt.GetFunction().Name);

        await llmService.SubmitToolOutputsToRun(
            threadId,
            runId,
            new List<(string, string)>
            {
                (toolCallId, JsonSerializer.Serialize(recipe))
            }
        );
    }

    private async Task ProvideAnalysisFeedback(string threadId, string runId, RecipeAnalysis analysis)
    {
        var toolCallId = await llmService.GetToolCallId(threadId, runId, synthesizeRecipePrompt.GetFunction().Name);

        await llmService.SubmitToolOutputsToRun(
            threadId,
            runId,
            new List<(string, string)>
            {
                (toolCallId, JsonSerializer.Serialize(analysis))
            }
        );
    }
}