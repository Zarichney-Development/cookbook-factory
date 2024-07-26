using System.Collections.Concurrent;
using System.Text.Json;
using AutoMapper;
using Cookbook.Factory.Models;
using ILogger = Cookbook.Factory.Logging.ILogger;

namespace Cookbook.Factory.Services;

public class RecipeService(ILogger logger, IModelService modelService, WebScraperService webscraper, IMapper mapper)
{
    private const int DontKeepScoreThreshold = 50;
    private const int AcceptableScoreThreshold = 80;

    private const int MinRelevantRecipes = 5;
    private const int MaxParallelTasks = MinRelevantRecipes;

    private const string RecipeOutputDirectoryName = "Recipes";
    private const string OrdersOutputDirectoryName = "Orders";

    public async Task<List<Recipe>> GetRecipes(string query)
    {
        // First, attempt to find and load existing JSON file
        var recipes = await LoadExistingRecipes(query);

        if (recipes.Count > 0)
        {
            logger.LogInformation("Found {count} cached recipes for query '{query}'.", recipes.Count, query);
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

        var allRecipes = cleanedRecipes;

        // Not sure how much value these lesser scores can be, but might as well store them, except for the irrelevant ones
        allRecipes.AddRange(filteredOutRecipes);

        await Utils.WriteToFile(RecipeOutputDirectoryName, query, allRecipes.OrderByDescending(r => r.RelevancyScore));

        return cleanedRecipes.OrderByDescending(r => r.RelevancyScore).ToList();
    }

    private async Task<RelevancyResult> RankRecipe(Recipe recipe, string query)
    {
        var systemPrompt =
            "You are a recipe relevancy filter. Your task is to determine if a recipe is relevant to a given query.";
        var userPrompt =
            $"Determine if the following recipe is relevant to the query: '{query}'\n\nRecipe data:\n{JsonSerializer.Serialize(recipe)}";
        var functionName = "FilterRelevancy";
        var functionDescription = "Filter the relevancy of a recipe based on a given query";
        var functionParameters = JsonSerializer.Serialize(new
        {
            type = "object",
            properties = new
            {
                relevancyScore = new
                    { type = "integer", description = "A score from 0 to 100 indicating how relevant the recipe is" },
                relevancyReasoning = new
                    { type = "string", description = "A brief explanation of the relevancy decision" },
            },
            required = new[] { "relevancyScore", "relevancyReasoning" }
        });

        var result = await modelService.GetToolResponse<RelevancyResult>(
            systemPrompt, userPrompt, functionName, functionDescription, functionParameters);

        logger.LogInformation($"Relevancy filter result: {JsonSerializer.Serialize(result)}");

        return result;
    }

    private async Task<List<Recipe>> RankRecipesAsync(List<Recipe> recipes, string query)
    {
        // Don't rank if there are already enough relevant recipes
        if (recipes.Count(r => r.RelevancyScore >= AcceptableScoreThreshold) >= MinRelevantRecipes)
        {
            return recipes.OrderByDescending(r => r.RelevancyScore).ToList();
        }

        var rankedRecipes = new ConcurrentBag<Recipe>();
        var tasks = new List<Task>();
        var semaphore = new SemaphoreSlim(MaxParallelTasks);

        foreach (var recipe in recipes)
        {
            if (recipe.RelevancyScore >= AcceptableScoreThreshold)
            {
                rankedRecipes.Add(recipe);
                continue;
            }

            if (rankedRecipes.Count(r => r.RelevancyScore >= AcceptableScoreThreshold) >= MinRelevantRecipes)
            {
                break;
            }

            await semaphore.WaitAsync();

            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var result = await RankRecipe(recipe, query);

                    recipe.RelevancyScore = result.RelevancyScore;
                    recipe.RelevancyReasoning = result.RelevancyReasoning;

                    rankedRecipes.Add(recipe);
                }
                finally
                {
                    semaphore.Release();
                }
            }));
        }

        await Task.WhenAll(tasks);

        return rankedRecipes.OrderByDescending(r => r.RelevancyScore).ToList();
    }

    private async Task<List<Recipe>> CleanRecipesAsync(List<Recipe> recipes)
    {
        var cleanedRecipes = new ConcurrentBag<Recipe>();
        var tasks = new List<Task>();
        var semaphore = new SemaphoreSlim(MaxParallelTasks);

        foreach (var recipe in recipes)
        {
            await semaphore.WaitAsync();

            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    cleanedRecipes.Add(await CleanRecipeData(recipe));
                }
                finally
                {
                    semaphore.Release();
                }
            }));
        }

        await Task.WhenAll(tasks);

        return cleanedRecipes.ToList();
    }

    private async Task<Recipe> CleanRecipeData(Recipe rawRecipe)
    {
        if (rawRecipe.Cleaned)
        {
            return rawRecipe;
        }

        const string systemPrompt =
            """
            You are specialized in cleaning and standardizing scraped recipe data. Follow these guidelines:
            1. Standardize units of measurement (e.g., 'tbsp', 'tsp', 'g').
            2. Correct spacing and spelling issues.
            3. Format ingredients and directions as arrays of strings.
            4. Standardize cooking times and temperatures (e.g., '350°F', '175°C').
            5. Remove irrelevant or accidental content.
            6. Do not add new information or change the original recipe.
            7. Leave empty fields as they are, replace nulls with empty strings.
            8. Ensure consistent formatting.
            Return a cleaned, standardized version of the recipe data, preserving original structure and information while improving clarity and consistency.
            """;

        var userPrompt = JsonSerializer.Serialize(mapper.Map<ScrapedRecipe>(rawRecipe));

        var functionName = "CleanRecipeData";
        var functionDescription = "Clean and standardize scraped recipe data";
        var functionParameters = JsonSerializer.Serialize(new
        {
            type = "object",
            properties = new
            {
                title = new { type = "string" },
                description = new { type = "string" },
                servings = new { type = "string" },
                prepTime = new { type = "string" },
                cookTime = new { type = "string" },
                totalTime = new { type = "string" },
                notes = new { type = "string" },
                ingredients = new { type = "array", items = new { type = "string" } },
                directions = new { type = "array", items = new { type = "string" } },
            },
            required = new[]
            {
                "title", "servings", "description", "prepTime", "cookTime", "totalTime", "notes", "ingredients",
                "directions"
            }
        });

        try
        {
            var cleanedRecipe = await modelService.GetToolResponse<Recipe>(
                systemPrompt, userPrompt, functionName, functionDescription, functionParameters);

            logger.LogInformation($"Cleaned recipe data: {JsonSerializer.Serialize(cleanedRecipe)}");

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
            logger.LogError(ex, $"Error cleaning recipe data: {ex.Message}");
            return rawRecipe;
        }
    }

    public async Task<SynthesizedRecipe> SynthesizeRecipe(List<Recipe> recipes, CookbookOrder order, string recipeName)
    {
        const string systemPrompt =
            """
            # Recipe Curation System Prompt

            **Role:** AI assistant for personalized recipe creation.

            **Steps:**
            1. **Analyze Cookbook Order:**
               - Review Cookbook Content, Details, and User Details.
               - Focus on dietary restrictions, allergies, skill level, and cooking goals.
            2. **Evaluate Provided Recipes:**
               - Analyze recipes from various sources for inspiration.
            3. **Create New Recipe:**
               - Blend elements from relevant recipes.
               - Ensure it meets dietary needs, preferences, skill level, and time constraints.
               - Align with the cookbook's theme and cultural goals.
            4. **Customize Recipe:**
               - Modify ingredients and techniques for dietary restrictions and allergies.
               - Adjust serving size.
               - Include alternatives or substitutions.
            5. **Enhance Recipe:**
               - Add cultural context or storytelling.
               - Incorporate educational content.
               - Include meal prep tips or leftover ideas.
            6. **Format Output:**
               - Use provided Recipe class structure.
               - Fill out all fields.
               - Include customizations, cultural context, or educational content in Notes.
            7. **Provide Attribution:**
               - List "Inspired by" URLs from original recipes.
               - Only include those that contributed towards the synthesized recipe.

            **Output Format:**
            ```json
            {
                'title': '',
                'description': '',
                'servings': '',
                'prepTime': '',
                'cookTime': '',
                'totalTime': '',
                'ingredients': [],
                'directions': [],
                'notes': '',
                'inspiredBy': ['list-of-urls']
            }
            ```

            **Goal:** Tailor recipes to user needs and preferences while maintaining original integrity and cookbook theme.
            """;

        var userPrompt = $"""
                          # Recipe data:
                          ```json
                          {JsonSerializer.Serialize(recipes)}
                          ```

                          # Cookbook Order:
                          {order.ToMarkdown()}

                          Please synthesize a personalized recipe.
                          """;

        var functionName = "SynthesizeRecipe";
        var functionDescription = "Refer to existing recipes and synthesize a new one based on user expectations";
        var functionParameters = JsonSerializer.Serialize(new
        {
            type = "object",
            properties = new
            {
                title = new { type = "string" },
                description = new { type = "string" },
                servings = new { type = "string" },
                prepTime = new { type = "string" },
                cookTime = new { type = "string" },
                totalTime = new { type = "string" },
                ingredients = new { type = "array", items = new { type = "string" } },
                directions = new { type = "array", items = new { type = "string" } },
                notes = new { type = "string" },
                inspiredBy = new { type = "array", items = new { type = "string" } }
            },
            required = new[]
            {
                "title", "ingredients", "directions", "servings", "description", "prepTime", "cookTime", "totalTime",
                "notes", "inspiredBy"
            }
        });

        try
        {
            var synthesizedRecipe = await modelService.GetToolResponse<SynthesizedRecipe>(
                systemPrompt, userPrompt, functionName, functionDescription, functionParameters);

            logger.LogInformation("Synthesized recipe data: {recipe}", synthesizedRecipe);

            var orderNumber = Guid.NewGuid().ToString().Substring(0, 8);
            var ordersDir = Path.Combine(OrdersOutputDirectoryName, orderNumber);

            await Utils.WriteToFile(ordersDir, "Order", order);
            await Utils.WriteToFile(Path.Combine(ordersDir, "recipes"), recipeName, synthesizedRecipe);

            return synthesizedRecipe;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Error cleaning recipe data: {ex.Message}");
            throw;
        }
    }

    private async Task<List<Recipe>> LoadExistingRecipes(string query)
        => await Utils.LoadExistingData<Recipe>(logger, RecipeOutputDirectoryName, query);
}