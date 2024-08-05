using System.Collections.Concurrent;
using System.Text.Json;
using AutoMapper;
using Cookbook.Factory.Models;
using Cookbook.Factory.Prompts;
using OpenAI.Assistants;
using Serilog;
using ILogger = Serilog.ILogger;

namespace Cookbook.Factory.Services;


public class RecipeService(
    IModelService modelService,
    WebScraperService webscraper,
    IMapper mapper,
    FileService fileService,
    RankRecipePrompt rankRecipePrompt,
    CleanRecipePrompt cleanRecipePrompt,
    SynthesizeRecipePrompt synthesizeRecipePrompt,
    AnalyzeRecipePrompt analyzeRecipePrompt
)
{
    private readonly ILogger _log = Log.ForContext<RecipeService>();
    private const int DontKeepScoreThreshold = 50;
    private const int AcceptableScoreThreshold = 80;

    private const int MinRelevantRecipes = 5;
    private const int MaxParallelTasks = MinRelevantRecipes;
    private readonly SemaphoreSlim _semaphore = new(MaxParallelTasks);

    private const string RecipeOutputDirectoryName = "Recipes";
    private const string OrdersOutputDirectoryName = "Orders";

    public async Task<List<Recipe>> GetRecipes(string query)
    {
        // First, attempt to find and load existing JSON file
        var recipes = await fileService.LoadExistingData<Recipe>(RecipeOutputDirectoryName, query);

        if (recipes.Any())
        {
            _log.Information("Found {count} cached recipes for query '{query}'.", recipes.Count, query);
        }
        else
        {
            // If no existing file found, proceed with web scraping
            recipes = mapper.Map<List<Recipe>>(await webscraper.ScrapeForRecipesAsync(query));
        }

        var rankedRecipes = await RankRecipesAsync(recipes, query);

        // To save on llm cost, only bother cleaning those with a good enough score
        var topRecipes = rankedRecipes.Where(r => r.RelevancyScore >= AcceptableScoreThreshold).ToList();

        var cleanedRecipes = await CleanRecipesAsync(topRecipes.Where(r => !r.Cleaned).ToList());
        cleanedRecipes.AddRange(topRecipes.Where(r => r.Cleaned));

        var filteredOutRecipes = rankedRecipes
            .Where(r => r.RelevancyScore < AcceptableScoreThreshold)
            // Eliminate anything below the relevancy threshold
            .Where(r => r.RelevancyScore >= DontKeepScoreThreshold)
            .ToList();

        // Not sure how much value these lesser scores can be, but might as well store them, except for the irrelevant ones
        var allRecipes = cleanedRecipes.Concat(filteredOutRecipes).ToList();

        fileService.WriteToFile(RecipeOutputDirectoryName, query,
            allRecipes.OrderByDescending(r => r.RelevancyScore));

        return cleanedRecipes.OrderByDescending(r => r.RelevancyScore).ToList();
    }

    public async Task<RelevancyResult> RankRecipe(Recipe recipe, string query)
    {
        var result = await modelService.CallFunction<RelevancyResult>(
            rankRecipePrompt.SystemPrompt,
            rankRecipePrompt.GetUserPrompt(recipe, query),
            rankRecipePrompt.GetFunction()
        );

        _log.Information("Relevancy filter result: {@Result}", result);

        return result;
    }

    private async Task<List<Recipe>> RankRecipesAsync(IReadOnlyCollection<Recipe> recipes, string query)
    {
        // Don't rank if there are already enough relevant recipes
        if (recipes.Count(r => r.RelevancyScore >= AcceptableScoreThreshold) >= MinRelevantRecipes)
        {
            return recipes.OrderByDescending(r => r.RelevancyScore).ToList();
        }

        var rankedRecipes = new ConcurrentQueue<Recipe>();

        await Parallel.ForEachAsync(recipes,
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelTasks },
            async (recipe, ct) =>
            {
                if (recipe.RelevancyScore >= AcceptableScoreThreshold)
                {
                    rankedRecipes.Enqueue(recipe);
                    return;
                }

                if (rankedRecipes.Count(r => r.RelevancyScore >= AcceptableScoreThreshold) >= MinRelevantRecipes)
                {
                    return;
                }

                await _semaphore.WaitAsync(ct);
                try
                {
                    var result = await RankRecipe(recipe, query);

                    recipe.RelevancyScore = result.RelevancyScore;
                    recipe.RelevancyReasoning = result.RelevancyReasoning;

                    rankedRecipes.Enqueue(recipe);
                }
                finally
                {
                    _semaphore.Release();
                }
            });

        return rankedRecipes.OrderByDescending(r => r.RelevancyScore).ToList();
    }

    private async Task<List<Recipe>> CleanRecipesAsync(IEnumerable<Recipe> recipes)
    {
        var cleanedRecipes = new ConcurrentQueue<Recipe>();

        await Parallel.ForEachAsync(
            recipes,
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelTasks },
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
            var cleanedRecipe = await modelService.CallFunction<Recipe>(
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

    private void WriteRecipeToOrderDir(string orderId, string recipeName, SynthesizedRecipe synthesizedRecipe)
    {
        var fullDir = Path.Combine(Path.Combine(OrdersOutputDirectoryName, orderId), "recipes");
        fileService.WriteToFile(fullDir, recipeName, synthesizedRecipe);
    }

    public CookbookOrder ProcessOrder(CookbookOrder order)
    {
        order.OrderId = Guid.NewGuid().ToString()[..8];
        var ordersDir = Path.Combine(OrdersOutputDirectoryName, order.OrderId);

        fileService.WriteToFile(ordersDir, "Order", order);
        
        _log.Information("Order intake: {@Order}", order);

        return order;
    }

    public async Task<SynthesizedRecipe> SynthesizeRecipe(List<Recipe> recipes, CookbookOrder order, string recipeName)
    {
        SynthesizedRecipe synthesizedRecipe;
        
        var synthesizingAssistant = await modelService.CreateAssistant(synthesizeRecipePrompt);
        var analyzeAssistant = await modelService.CreateAssistant(analyzeRecipePrompt);

        var synthesizingThread = await modelService.CreateThread();
        var analyzeThread = await modelService.CreateThread();

        try
        {
            await InitiateSynthesis(synthesizingThread, recipes, order);
            
            synthesizedRecipe =  await ProcessSynthesisUntilApproved(synthesizingThread, synthesizingAssistant, analyzeThread, analyzeAssistant, order, recipeName);

            _log.Information("Recipe synthesized: {@Recipe}", synthesizedRecipe);

            WriteRecipeToOrderDir(order.OrderId!, recipeName, synthesizedRecipe);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error synthesizing recipe: {Message}", ex.Message);
            throw;
        }
        finally
        {
            await CleanupResources(synthesizingAssistant, analyzeAssistant, synthesizingThread, analyzeThread);
        }

        return synthesizedRecipe;
    }

    private async Task InitiateSynthesis(string synthesizingThread, List<Recipe> recipes, CookbookOrder order)
    {
        var userPrompt = synthesizeRecipePrompt.GetUserPrompt(recipes, order);
        await modelService.CreateMessage(synthesizingThread, userPrompt);
    }

    private async Task<SynthesizedRecipe> ProcessSynthesisUntilApproved(string synthesizingThread,
        string synthesizingAssistant, string analyzeThread, string analyzeAssistant, CookbookOrder order,
        string recipeName)
    {
        var count = 0;
        
        while (true)
        {
            count++;
            
            var synthesizedRecipe = await ProcessSynthesisRun(synthesizingThread, synthesizingAssistant);
            
            _log.Information("Synthesized recipe [{count}]: {@Recipe}", count, synthesizedRecipe);
            
            var analysisResult = await ProcessAnalysisRun(analyzeThread, analyzeAssistant, synthesizedRecipe, order, recipeName);

            _log.Information("Analysis result [{verdict}]: {AnalysisResult}", analysisResult.Passes, analysisResult.Feedback);
            
            if (analysisResult.Passes)
            {
                return synthesizedRecipe;
            }

            await ProvideAnalysisFeedback(synthesizingThread, synthesizingAssistant, analysisResult);
        }
    }

    private async Task<SynthesizedRecipe> ProcessSynthesisRun(string threadId, string assistantId)
    {
        var run = await modelService.CreateRun(threadId, assistantId, parallelToolCallsEnabled: false, toolConstraintRequired: true);

        while (true)
        {
            var (isComplete, status) = await modelService.GetRun(threadId, run);

            if (isComplete)
            {
                throw new Exception("Synthesis run completed without producing a recipe");
            }

            if (status == RunStatus.RequiresAction)
            {
                return await modelService.GetRunAction<SynthesizedRecipe>(threadId, run, synthesizeRecipePrompt.GetFunction().Name);
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

    private async Task<RecipeAnalysis> ProcessAnalysisRun(string threadId, string assistantId, SynthesizedRecipe recipe,
        CookbookOrder order, string recipeName)
    {
        var userPrompt = analyzeRecipePrompt.GetUserPrompt(recipe, order, recipeName);
        await modelService.CreateMessage(threadId, userPrompt);

        var run = await modelService.CreateRun(threadId, assistantId, parallelToolCallsEnabled: false, toolConstraintRequired: true);

        while (true)
        {
            var (isComplete, status) = await modelService.GetRun(threadId, run);

            if (isComplete)
            {
                throw new Exception("Analysis run completed without producing a result");
            }

            if (status == RunStatus.RequiresAction)
            {
                return await modelService.GetRunAction<RecipeAnalysis>(threadId, run, analyzeRecipePrompt.GetFunction().Name);
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

    private async Task ProvideAnalysisFeedback(string threadId, string assistantId, RecipeAnalysis analysis)
    {
        var run = await modelService.CreateRun(threadId, assistantId, parallelToolCallsEnabled: false, toolConstraintRequired: true);
        var toolCallId = await modelService.GetToolCallId(threadId, run, synthesizeRecipePrompt.GetFunction().Name);

        await modelService.SubmitToolOutputsToRun(
            threadId,
            run,
            new List<(string, string)>
            {
                (toolCallId, JsonSerializer.Serialize(analysis))
            }
        );
    }

    private async Task CleanupResources(string synthesizingAssistant, string analyzeAssistant, string synthesizingThread, string analyzeThread)
    {
        await modelService.DeleteAssistant(synthesizingAssistant);
        await modelService.DeleteAssistant(analyzeAssistant);
        await modelService.DeleteThread(synthesizingThread);
        await modelService.DeleteThread(analyzeThread);
    }
}