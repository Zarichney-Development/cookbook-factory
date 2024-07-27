using System.Collections.Concurrent;
using System.Text.Json;
using AutoMapper;
using Cookbook.Factory.Models;
using Cookbook.Factory.Prompts;
using OpenAI;
using OpenAI.Assistants;
using Serilog;
using ILogger = Serilog.ILogger;

namespace Cookbook.Factory.Services;

public class RecipeService(IModelService modelService, WebScraperService webscraper, IMapper mapper,
    FileService fileService, OpenAIClient client)
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

        await fileService.WriteToFile(RecipeOutputDirectoryName, query,
            allRecipes.OrderByDescending(r => r.RelevancyScore));

        return cleanedRecipes.OrderByDescending(r => r.RelevancyScore).ToList();
    }

    private async Task<RelevancyResult> RankRecipe(Recipe recipe, string query)
    {
        const string systemPrompt = PrompCatalog.RankRecipe.SystemPrompt;
        var userPrompt = PrompCatalog.RankRecipe.UserPrompt(recipe, query);
        var (functionName, functionDescription, functionParameters) = PrompCatalog.RankRecipe.GetFunction();

        var result = await modelService.CallFunction<RelevancyResult>(
            systemPrompt, userPrompt, functionName, functionDescription, functionParameters);

        _log.Information("Relevancy filter result: {@Result}", result);

        return result;
    }

    private async Task<List<Recipe>> RankRecipesAsync(List<Recipe> recipes, string query)
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

    private async Task<List<Recipe>> CleanRecipesAsync(List<Recipe> recipes)
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

        const string systemPrompt = PrompCatalog.CleanRecipe.SystemPrompt;
        var userPrompt = PrompCatalog.CleanRecipe.UserPrompt(mapper, rawRecipe);
        var (functionName, functionDescription, functionParameters) = PrompCatalog.CleanRecipe.GetFunction();

        try
        {
            var cleanedRecipe = await modelService.CallFunction<Recipe>(
                systemPrompt, userPrompt, functionName, functionDescription, functionParameters);

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

    public async Task<SynthesizedRecipe> SynthesizeRecipe(List<Recipe> recipes, CookbookOrder order, string recipeName)
    {
        try
        {
            var synthesizedRecipe = await SynthesizeRecipe(recipes, order);

            _log.Information("Synthesized recipe data: {Recipe}", synthesizedRecipe);

            var orderNumber = Guid.NewGuid().ToString()[..8];
            var ordersDir = Path.Combine(OrdersOutputDirectoryName, orderNumber);

            await fileService.WriteToFile(ordersDir, "Order", order);
            await fileService.WriteToFile(Path.Combine(ordersDir, "recipes"), recipeName, synthesizedRecipe);

            return synthesizedRecipe;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error synthesizing recipe: {Message}", ex.Message);
            throw;
        }
    }


    public async Task<SynthesizedRecipe> SynthesizeRecipe(List<Recipe> recipes, CookbookOrder order)
    {
        var assistantClient = client.GetAssistantClient();

        try
        {
            // Create threads for both assistants
            var synthesizingThread = (await assistantClient.CreateThreadAsync()).Value;
            var analystThread = (await assistantClient.CreateThreadAsync()).Value;

            // Create assistants
            var recipeAssistant = (await assistantClient.CreateAssistantAsync("gpt-4o-mini",
                new AssistantCreationOptions
                {
                    Instructions = PrompCatalog.SynthesizeRecipe.SystemPrompt,
                    Tools =
                    {
                        new FunctionToolDefinition
                        {
                            FunctionName = PrompCatalog.SynthesizeRecipe.Function.Name,
                            Description = PrompCatalog.SynthesizeRecipe.Function.Description,
                            Parameters = BinaryData.FromString(PrompCatalog.SynthesizeRecipe.Function.Parameters)
                        }
                    }
                })).Value;

            var analystAssistant = (await assistantClient.CreateAssistantAsync("gpt-4o-mini",
                new AssistantCreationOptions
                {
                    Instructions = PrompCatalog.AnalyzeRecipe.SystemPrompt,
                    Tools =
                    {
                        new FunctionToolDefinition
                        {
                            FunctionName = PrompCatalog.AnalyzeRecipe.Function.Name,
                            Description = PrompCatalog.AnalyzeRecipe.Function.Description,
                            Parameters = BinaryData.FromString(PrompCatalog.AnalyzeRecipe.Function.Parameters)
                        }
                    }
                })).Value;

            // Start the recipe synthesizer thread
            await assistantClient.CreateMessageAsync(
                synthesizingThread.Id,
                MessageRole.User,
                new[] { MessageContent.FromText(PrompCatalog.SynthesizeRecipe.UserPrompt(recipes, order)) }
            );

            var synthesizeRun = (await assistantClient.CreateRunAsync(
                synthesizingThread.Id,
                recipeAssistant.Id,
                new RunCreationOptions
                {
                    ParallelToolCallsEnabled = false,
                    ToolConstraint = ToolConstraint.Required,
                }
                )).Value;

            SynthesizedRecipe curatedRecipe = null!;
            var recipeApproved = false;
            ThreadRun? analystRun = null;

            while (!recipeApproved)
            {
                // Process generator run
                while (!synthesizeRun.Status.IsTerminal)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1));
                    synthesizeRun = await assistantClient.GetRunAsync(synthesizeRun.ThreadId, synthesizeRun.Id);

                    if (synthesizeRun.Status == RunStatus.RequiresAction)
                    {
                        foreach (var action in synthesizeRun.RequiredActions)
                        {
                            if (action.FunctionName != PrompCatalog.SynthesizeRecipe.Function.Name) continue;

                            curatedRecipe = JsonSerializer.Deserialize<SynthesizedRecipe>(action.FunctionArguments,
                                new JsonSerializerOptions
                                {
                                    PropertyNameCaseInsensitive = true
                                })!;

                            if (analystRun == null)
                            {
                                // Initial recipe: create a message and start the analyst run
                                await assistantClient.CreateMessageAsync(
                                    analystThread.Id,
                                    MessageRole.User,
                                    new[]
                                    {
                                        MessageContent.FromText(
                                            PrompCatalog.AnalyzeRecipe.UserPrompt(curatedRecipe, order))
                                    }
                                );
                                analystRun =
                                    await assistantClient.CreateRunAsync(analystThread.Id, analystAssistant.Id,
                                        new RunCreationOptions
                                        {
                                            ParallelToolCallsEnabled = false,
                                            ToolConstraint = ToolConstraint.Required,
                                        }
                                    );
                            }
                            else
                            {
                                // Subsequent recipes: submit as tool output to the analyst run
                                await assistantClient.SubmitToolOutputsToRunAsync(analystRun.ThreadId,
                                    analystRun.Id,
                                    new List<ToolOutput>
                                    {
                                        new(analystRun.RequiredActions[0].ToolCallId,
                                            JsonSerializer.Serialize(curatedRecipe))
                                    });
                            }

                            var analysisConducted = false;

                            // Process analyst run
                            while (!analystRun.Status.IsTerminal && !analysisConducted)
                            {
                                await Task.Delay(TimeSpan.FromSeconds(1));
                                analystRun = await assistantClient.GetRunAsync(analystRun.ThreadId, analystRun.Id);

                                if (analystRun.Status == RunStatus.RequiresAction)
                                {
                                    foreach (var analystAction in analystRun.RequiredActions)
                                    {
                                        if (analystAction.FunctionName != PrompCatalog.AnalyzeRecipe.Function.Name)
                                            continue;

                                        var analysisResult = JsonSerializer.Deserialize<RecipeAnalysis>(
                                            analystAction.FunctionArguments,
                                            new JsonSerializerOptions
                                            {
                                                PropertyNameCaseInsensitive = true
                                            })!;

                                        analysisConducted = true;

                                        if (analysisResult.Passes)
                                        {
                                            recipeApproved = true;

                                            _log.Information("Recipe approved: {@AnalysisResult}", analysisResult);

                                            // Cancel both runs as we have an approved recipe
                                            await assistantClient.CancelRunAsync(synthesizeRun.ThreadId,
                                                synthesizeRun.Id);
                                            await assistantClient.CancelRunAsync(analystRun.ThreadId, analystRun.Id);
                                        }
                                        else
                                        {
                                            _log.Information("Recipe needs improvement: {@AnalysisResult}",
                                                analysisResult);

                                            // Send feedback to the generator via tool output
                                            await assistantClient.SubmitToolOutputsToRunAsync(
                                                synthesizeRun.ThreadId, synthesizeRun.Id,
                                                new List<ToolOutput>
                                                {
                                                    new(action.ToolCallId,
                                                        JsonSerializer.Serialize(analysisResult))
                                                });
                                            break; // Exit the foreach loop to continue the analyst run
                                        }
                                    }
                                }

                                if (recipeApproved)
                                {
                                    break; // Exit the while loop if recipe is approved
                                }
                            }

                            if (recipeApproved)
                            {
                                break; // Exit the foreach loop if recipe is approved
                            }
                        }
                    }

                    if (recipeApproved)
                    {
                        break; // Exit the while loop if recipe is approved
                    }
                }

                if (!recipeApproved && synthesizeRun.Status.IsTerminal)
                {
                    // Start a new generator run for revision if needed
                    synthesizeRun = await assistantClient.CreateRunAsync(synthesizingThread.Id, recipeAssistant.Id);
                }
            }

            _log.Information("Final curated recipe: {recipe}", curatedRecipe);

            // Clean up resources
            await assistantClient.DeleteAssistantAsync(recipeAssistant.Id);
            await assistantClient.DeleteAssistantAsync(analystAssistant.Id);
            await assistantClient.DeleteThreadAsync(synthesizingThread.Id);
            await assistantClient.DeleteThreadAsync(analystThread.Id);

            return curatedRecipe;
        }
        catch (Exception ex)
        {
            _log.Error(ex, $"Error in recipe curation process: {ex.Message}");
            throw;
        }
    }
}