using System.Collections.Concurrent;
using System.Text.Json;
using AutoMapper;
using Cookbook.Factory.Config;
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
    public int MaxNewRecipeNameAttempts { get; init; } = 5;
    public int MaxParallelTasks { get; init; } = 5;
    public string OutputDirectory { get; init; } = "Recipes";
}

public class RecipeService(
    IRecipeRepository recipeRepository,
    RecipeConfig config,
    ILlmService llmService,
    WebScraperService webscraper,
    IMapper mapper,
    RankRecipePrompt rankRecipePrompt,
    SynthesizeRecipePrompt synthesizeRecipePrompt,
    AnalyzeRecipePrompt analyzeRecipePrompt,
    ProcessOrderPrompt processOrderPrompt
)
{
    private readonly ILogger _log = Log.ForContext<RecipeService>();

    public async Task<List<Recipe>> GetRecipes(string requestedRecipeName, CookbookOrder cookbookOrder)
    {
        var recipeName = requestedRecipeName;
        var recipes = await GetRecipes(recipeName);

        var previousAttempts = new List<string?>();
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
        List<string?> previousAttempts)
    {
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(processOrderPrompt.SystemPrompt),
            new UserChatMessage(processOrderPrompt.GetUserPrompt(cookbookOrder)),
            new SystemChatMessage(cookbookOrder.RecipeList.ToString()),
            new UserChatMessage(
                $"""
                 Thank you. The recipe name "{recipeName}" didn't yield any results, possibly due to its uniqueness or obscurity.
                 Please provide an alternative name for a similar recipe. With each failed attempt, the search will broaden.
                 Only respond with the new recipe name.
                 """)
        };

        foreach (var previousAttempt in previousAttempts)
        {
            messages.Add(new SystemChatMessage(previousAttempt));
            messages.Add(
                $"Sorry, that search also returned no results. Please try another query, staying as true as possible to the original recipe '{recipeName}'.");
        }

        var result = await llmService.GetCompletion(messages);

        return result.ToString()!;
    }

    public async Task<List<Recipe>> GetRecipes(string query, int? acceptableScore = null, bool scrape = true)
    {
        acceptableScore ??= config.AcceptableScoreThreshold;
        var recipesNeeded = config.RecipesToReturnPerRetrieval;

        // First, attempt to retrieve via local source of JSON files
        var recipes = await recipeRepository.SearchRecipes(query);
        if (recipes.Count != 0)
        {
            _log.Information("Retrieved {count} cached recipes for query '{query}'.", recipes.Count, query);
        }

        await RankUnrankedRecipesAsync(recipes, query, acceptableScore.Value);

        // Check if additional recipes should be web-scraped
        if (scrape && recipes.Count(r => (r.Relevancy.TryGetValue(query, out var value) ? value.Score : 0) >= acceptableScore) < recipesNeeded)
        {
            // Proceed with web scraping
            var scrapedRecipes = await webscraper.ScrapeForRecipesAsync(query);

            // Exclude any recipes already in the repository
            var newRecipes = scrapedRecipes.Where(r => !recipeRepository.ContainsRecipe(r.Id!));

            // Map scraped recipes to Recipe objects and add to the list
            recipes.AddRange(mapper.Map<List<Recipe>>(newRecipes));

            // Rank any new unranked recipes
            await RankUnrankedRecipesAsync(recipes, query, acceptableScore.Value);
        }

        // Strip out any recipes that are not relevant
        recipes = recipes
            .Where(r => !r.Relevancy.ContainsKey(query) || r.Relevancy[query].Score > 0)
            .OrderByDescending(r => r.Relevancy.TryGetValue(query, out var value) ? value.Score : 0)
            .ToList();

        // Save recipes to the repository
        if (recipes.Count != 0)
        {
            await recipeRepository.AddUpdateRecipes(recipes);
        }

        // Return the top recipes
        return recipes
            .Take(config.RecipesToReturnPerRetrieval)
            .ToList();
    }

    private async Task RankUnrankedRecipesAsync(List<Recipe> recipes, string query, int acceptableScore)
    {
        var unrankedRecipes = recipes.Where(r => !r.Relevancy.ContainsKey(query)).ToList();
        if (unrankedRecipes.Count != 0)
        {
            await RankRecipesAsync(unrankedRecipes, query, acceptableScore);
            // At this point, unrankedRecipes have their Relevancy updated.
            // No need to replace items in the list since the recipes are reference types
            // and their properties have been updated in place.
        }

        // Now sort the recipes list based on the updated relevancy scores.
        recipes.Sort((r1, r2) =>
        {
            var score1 = r1.Relevancy.TryGetValue(query, out var value) ? value.Score : 0;
            var score2 = r2.Relevancy.TryGetValue(query, out var value1) ? value1.Score : 0;
            return score2.CompareTo(score1);
        });
    }

    private async Task RankRecipesAsync(List<Recipe> recipes, string query, int? acceptableScore = null)
    {
        acceptableScore ??= config.AcceptableScoreThreshold;

        var rankedRecipes = new ConcurrentBag<Recipe>();
        var cts = new CancellationTokenSource();

        try
        {
            await Parallel.ForEachAsync(recipes, new ParallelOptions
                {
                    MaxDegreeOfParallelism = config.MaxParallelTasks,
                    CancellationToken = cts.Token
                },
                async (recipe, ct) =>
                {
                    // Check if cancellation has been requested
                    if (ct.IsCancellationRequested)
                        return;

                    try
                    {
                        if (!recipe.Relevancy.TryGetValue(query, out var value) || value.Score < acceptableScore)
                        {
                            // Evaluate relevancy if not already
                            value = await RankRecipe(recipe, query);
                            recipe.Relevancy[query] = value;
                        }

                        if (value.Score >= acceptableScore)
                        {
                            rankedRecipes.Add(recipe);

                            // Check if we have collected enough recipes
                            if (rankedRecipes.Count >= config.RecipesToReturnPerRetrieval)
                            {
                                cts.Cancel(); // Cancel remaining tasks
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Task was cancelled, just return
                    }
                    catch (Exception ex)
                    {
                        _log.Error(ex, $"Error ranking recipe: {recipe.Id}");
                        throw;
                    }
                });
        }
        catch (OperationCanceledException)
        {
            // The operation was cancelled because enough recipes were found
        }
        finally
        {
            cts.Dispose();
        }
    }

    private async Task<RelevancyResult> RankRecipe(Recipe recipe, string? query)
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

    public async Task<(SynthesizedRecipe, List<SynthesizedRecipe>)> SynthesizeRecipe(List<Recipe> recipes,
        CookbookOrder order, string? recipeName)
    {
        SynthesizedRecipe synthesizedRecipe;
        List<SynthesizedRecipe> rejectedRecipes = [];

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