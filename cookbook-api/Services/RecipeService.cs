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
    public int RecipesToReturnPerRetrieval { get; init; } = 5;
    public int AcceptableScoreThreshold { get; init; } = 75;
    public int QualityScoreThreshold { get; init; } = 80;
    public int MinRelevantRecipes { get; init; } = 5;
    public int MaxNewRecipeNameAttempts { get; init; } = 5;
    public int MaxParallelTasks { get; init; } = 5; // Same as MinRelevantRecipes
    public string OutputDirectory { get; init; } = "Recipes";
}

public class RecipeService(
    IRecipeRepository recipeRepository,
    RecipeConfig config,
    ILlmService llmService,
    WebScraperService webscraper,
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
                _log.Error($"Aborting recipe searching for '{{RecipeName}}', attempted {attempts} times",
                    requestedRecipeName, attempts - 1);
                throw new Exception($"No recipes found, attempted {attempts - 1} times");
            }

            // Lower the bar of acceptable for every attempt
            acceptableScore -= 5;

            // With a lower bar, first attempt to check local sources before performing a new web scrape
            recipes = await GetRecipes(recipeName, acceptableScore, false);
            if (recipes.Count > 0)
            {
                break;
            }

            // Request for new query to scrap with
            recipeName = await GetReplacementRecipeName(requestedRecipeName, cookbookOrder, previousAttempts);

            _log.Information(
                "Attempting using a replacement recipe search using '{NewRecipeName}' instead of '{RecipeName}' with an acceptable score of {AcceptableScore}.",
                recipeName, requestedRecipeName, acceptableScore);

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
            new SystemChatMessage(cookbookOrder.RecipeList.ToString()),
            new UserChatMessage(
                $"""
                 Thank you.
                 The following recipe name unfortunately did not result in any recipes: {recipeName}.
                 This may be because the recipe name is too unique or not well-known.
                 Please provide an alternative recipe name that should result in a similar search.
                 The more failed attempts, the more the next search should be broadened.
                 Respond with the alternative search result and nothing else.
                 """)
        };

        foreach (var previousAttempt in previousAttempts)
        {
            messages.Add(new SystemChatMessage(previousAttempt));
            messages.Add(
                $"Sorry, that also didn't result in anything. Please try an alternative search query while respecting true to the original recipe of '{recipeName}'.");
        }

        var result = await llmService.GetCompletion(messages);

        return result.ToString()!;
    }

    public async Task<List<Recipe>> GetRecipes(string query, int? acceptableScore = null, bool scrape = true)
    {
        acceptableScore ??= config.AcceptableScoreThreshold;

        // First, attempt to find from JSON files
        var recipes = await recipeRepository.SearchRecipes(query);

        if (recipes.Any())
        {
            _log.Information("Found {count} cached recipes for query '{query}'.", recipes.Count, query);
        }

        var unrankedRecipes = recipes.Where(r => !r.Relevancy.ContainsKey(query)).ToList();
        if (unrankedRecipes.Any())
        {
            await RankRecipesAsync(unrankedRecipes, query, acceptableScore);
        }

        if (scrape && recipes.Count(r => r.Cleaned && r.Relevancy[query].Score >= acceptableScore) <
            config.RecipesToReturnPerRetrieval)
        {
            // If no existing file found, proceed with web scraping

            var result = await webscraper.ScrapeForRecipesAsync(query);

            // Exclude any that didn't get initially picked up by the repository search but was scraped again
            var newRecipes = result.Where(r => !recipeRepository.ContainsRecipe(r.Id!));

            recipes = mapper.Map<List<Recipe>>(newRecipes);
        }

        unrankedRecipes = recipes.Where(r => !r.Relevancy.ContainsKey(query)).ToList();
        if (unrankedRecipes.Any())
        {
            await RankRecipesAsync(unrankedRecipes, query, acceptableScore);
        }

        // Strip out anything that the rank deemed is not a recipe
        recipes = recipes
            .Where(r => r.Relevancy[query].Score > 0)
            .OrderByDescending(r => r.Relevancy[query].Score)
            .ToList();

        var uncleanedRecipes = recipes
            .Where(r => !r.Cleaned)
            .ToList();

        if (uncleanedRecipes.Count != 0)
        {
            await CleanRecipesAsync(uncleanedRecipes);
        }

        if (recipes.Count != 0)
        {
            await recipeRepository.AddRecipes(recipes);
        }

        return recipes
            .Take(config.RecipesToReturnPerRetrieval)
            .ToList();
    }

    private async Task<RelevancyResult> RankRecipe(Recipe recipe, string query)
    {
        var result = await llmService.CallFunction<RelevancyResult>(
            rankRecipePrompt.SystemPrompt,
            rankRecipePrompt.GetUserPrompt(recipe, query),
            rankRecipePrompt.GetFunction()
        );

        result.Query = query;

        _log.Information("Relevancy filter result: {@Result}", result);

        return result;
    }

    private async Task<List<Recipe>> RankRecipesAsync(List<Recipe> recipes, string query, int? acceptableScore = null)
    {
        acceptableScore ??= config.AcceptableScoreThreshold;

        // Don't rank if there are already enough relevant recipes
        if (recipes
                .Where(r=> r.Relevancy.ContainsKey(query))
                .Count(r => r.Relevancy[query].Score >= acceptableScore) >= config.MinRelevantRecipes)
        {
            return recipes.OrderByDescending(r => r.Relevancy[query].Score).ToList();
        }

        var rankedRecipes = new ConcurrentQueue<Recipe>();
        var relevantRecipeCount = new AtomicCounter();
        var cts = new CancellationTokenSource();

        try
        {
            await Parallel.ForEachAsync(recipes,
                new ParallelOptions { MaxDegreeOfParallelism = config.MaxParallelTasks, CancellationToken = cts.Token },
                async (recipe, ct) =>
                {
                    if (ct.IsCancellationRequested) return;

                    if (recipe.Relevancy.TryGetValue(query, out var value) && value.Score >= acceptableScore)
                    {
                        rankedRecipes.Enqueue(recipe);
                        if (relevantRecipeCount.Increment() >= config.MinRelevantRecipes)
                        {
                            await cts.CancelAsync();
                        }

                        return;
                    }

                    await _semaphore.WaitAsync(ct);
                    try
                    {
                        var result = await RankRecipe(recipe, query);

                        recipe.Relevancy[query] = result;

                        rankedRecipes.Enqueue(recipe);

                        if (recipe.Relevancy[query].Score >= acceptableScore &&
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
        }
        catch (TaskCanceledException)
        {
            // Good to return
        }

        return rankedRecipes.OrderByDescending(r => r.Relevancy[query].Score).ToList();
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

    private async Task<Recipe> CleanRecipeData(Recipe recipe)
    {
        if (recipe.Cleaned)
        {
            return recipe;
        }

        try
        {
            var cleanedRecipe = await llmService.CallFunction<CleanedRecipe>(
                cleanRecipePrompt.SystemPrompt,
                cleanRecipePrompt.GetUserPrompt(recipe),
                cleanRecipePrompt.GetFunction()
            );

            _log.Information("Cleaned recipe data: {@CleanedRecipe}", cleanedRecipe);

            recipe.Title = cleanedRecipe.Title;
            recipe.Description = cleanedRecipe.Description;
            recipe.Ingredients = cleanedRecipe.Ingredients;
            recipe.Directions = cleanedRecipe.Directions;
            recipe.Servings = cleanedRecipe.Servings;
            recipe.PrepTime = cleanedRecipe.PrepTime;
            recipe.CookTime = cleanedRecipe.CookTime;
            recipe.TotalTime = cleanedRecipe.TotalTime;
            recipe.Notes = cleanedRecipe.Notes;
            recipe.Cleaned = true;

            return recipe;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error cleaning recipe data: {Message}", ex.Message);
            return recipe;
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

                // In case LLM fails to provide theses
                if (string.IsNullOrWhiteSpace(analysisResult.Suggestions))
                {
                    analysisResult.Suggestions =
                        "Please pay attention to what is desired from the cookbook order and synthesize another one.";
                }

                if (string.IsNullOrEmpty(analysisResult.Analysis))
                {
                    analysisResult.Analysis = "The recipe is not suitable enough for the cookbook order.";
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
                var result = await llmService.GetRunAction<SynthesizedRecipe>(threadId, runId,
                    synthesizeRecipePrompt.GetFunction().Name);

                result.InspiredBy ??= [];

                return result;
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

        var toolOutput =
            $"""
                 A new revision is required. Refer to the QA analysis:
                 ```json
                 {JsonSerializer.Serialize(analysis)}
                 ```
                 """.Trim();

        await llmService.SubmitToolOutputsToRun(
            threadId,
            runId,
            new List<(string, string)> { (toolCallId, toolOutput) }
        );
    }
}