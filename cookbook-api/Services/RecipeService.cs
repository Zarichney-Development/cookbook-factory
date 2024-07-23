using System.Collections.Concurrent;
using AngleSharp;
using AngleSharp.Dom;
using System.Text.Json;
using AutoMapper;
using Cookbook.Factory.Models;

namespace Cookbook.Factory.Services;

public class RecipeService
{
    private readonly ILogger<RecipeService> _logger;
    private const int MaxNumRecipesPerSite = 1;
    private const int MaxParallelTasks = 5;
    private const int PerfectScoreThreshold = 9;
    private const int MinRelevantRecipes = 5;
    private static Dictionary<string, Dictionary<string, string>>? _siteSelectors;
    private readonly ModelService _modelService;
    private readonly IMapper _mapper;

    public RecipeService(ILogger<RecipeService> logger, ModelService modelService, IMapper mapper)
    {
        _logger = logger;
        _modelService = modelService;
        _mapper = mapper;
    }

    private async Task<RelevancyFilterResult> RankRecipe(Recipe recipeData, string query)
    {
        var systemPrompt =
            "You are a recipe relevancy filter. Your task is to determine if a recipe is relevant to a given query.";
        var userPrompt =
            $"Determine if the following recipe is relevant to the query: '{query}'\n\nRecipe data:\n{JsonSerializer.Serialize(recipeData)}";
        var functionName = "FilterRelevancy";
        var functionDescription = "Filter the relevancy of a recipe based on a given query";
        var functionParameters = @"{
        ""type"": ""object"",
        ""properties"": {
            ""isRelevant"": {
                ""type"": ""boolean"",
                ""description"": ""Whether the recipe is relevant to the query""
            },
            ""relevancyScore"": {
                ""type"": ""integer"",
                ""description"": ""A score from 0 to 10 indicating how relevant the recipe is""
            },
            ""reasoning"": {
                ""type"": ""string"",
                ""description"": ""A brief explanation of the relevancy decision""
            }
        },
        ""required"": [""isRelevant"", ""relevancyScore"", ""reasoning""]
    }";

        var result = await _modelService.GetResponseAsync<RelevancyFilterResult>(
            systemPrompt, userPrompt, functionName, functionDescription, functionParameters);

        _logger.LogInformation($"Relevancy filter result: {JsonSerializer.Serialize(result)}");

        return result;
    }

    public async Task<List<Recipe>> RankRecipes(List<Recipe> recipes, string query)
    {
        var relevantRecipes = new ConcurrentBag<Recipe>();
        var semaphore = new SemaphoreSlim(MaxParallelTasks);
        var tasks = new List<Task>();

        foreach (var recipe in recipes)
        {
            if (recipe.Relevancy >= PerfectScoreThreshold)
            {
                relevantRecipes.Add(recipe);
                continue;
            }

            if (relevantRecipes.Count >= MinRelevantRecipes &&
                relevantRecipes.Count(r => r.Relevancy >= PerfectScoreThreshold) >= MinRelevantRecipes)
            {
                break;
            }

            await semaphore.WaitAsync();

            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var result = await RankRecipe(recipe, query);

                    if (result.IsRelevant)
                    {
                        recipe.Relevancy = result.RelevancyScore;
                        recipe.RelevancyReasoning = result.Reasoning;
                        relevantRecipes.Add(recipe);
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            }));
        }

        await Task.WhenAll(tasks);

        var topRecipes = relevantRecipes
            .OrderByDescending(r => r.Relevancy)
            .Take(MinRelevantRecipes)
            .ToList();

        return topRecipes;
    }

    public async Task<List<Recipe>> CleanRecipes(List<Recipe> recipes)
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
                    var cleanedRecipe = await CleanRecipeData(recipe);
                    cleanedRecipes.Add(cleanedRecipe);
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

        const string systemPrompt = @"
You are an AI assistant specialized in cleaning and standardizing recipe data that has been scraped from various online sources. Your task is to process the given recipe data and return a cleaned, standardized version. Follow these guidelines:

1. Standardize units of measurement (UoM) to common formats (e.g., 'tbsp' for tablespoon, 'tsp' for teaspoon, 'g' for grams).
2. Correct spacing and spelling issues throughout the recipe.
3. Ensure ingredients and directions are complete and properly formatted as arrays of strings.
4. Standardize cooking times and temperatures (e.g., '350°F' or '175°C').
5. Remove any irrelevant information or accidentally scraped content.
6. Do not add any new information. If a field is empty or missing, leave it empty in the output.
7. Ensure consistent formatting for all fields.

Process the recipe data carefully and return a cleaned version that maintains the original structure and information while improving clarity and consistency.";

        var llmRecipe = _mapper.Map<LlmRecipe>(rawRecipe);
        var userPrompt = JsonSerializer.Serialize(llmRecipe);

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
                ingredients = new { type = "array", items = new { type = "string" } },
                directions = new { type = "array", items = new { type = "string" } },
                notes = new { type = "string" }
            },
            required = new[] { "title", "ingredients", "directions" }
        });

        try
        {
            var cleanedLlmRecipe = await _modelService.GetResponseAsync<LlmRecipe>(
                systemPrompt, userPrompt, functionName, functionDescription, functionParameters);

            _logger.LogInformation($"Cleaned recipe data: {JsonSerializer.Serialize(cleanedLlmRecipe)}");

            var cleanedRecipe = _mapper.Map<Recipe>(cleanedLlmRecipe);

            // Preserve fields that shouldn't be modified by the LLM
            cleanedRecipe.RecipeUrl = rawRecipe.RecipeUrl;
            cleanedRecipe.ImageUrl = rawRecipe.ImageUrl;
            cleanedRecipe.Relevancy = rawRecipe.Relevancy;
            cleanedRecipe.RelevancyReasoning = rawRecipe.RelevancyReasoning;
            cleanedRecipe.Cleaned = true;

            return cleanedRecipe;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error cleaning recipe data: {ex.Message}");
            return rawRecipe;
        }
    }

    public async Task<LlmRecipe> CurateRecipe(List<Recipe> recipes, CookbookOrder order)
    {
        const string systemPrompt = @"
# Recipe Curation System Prompt

You are an AI assistant specialized in recipe curation and customization. Your task is to create a personalized recipe based on the user's cookbook order and a set of real-world recipes. Follow these steps to curate the perfect recipe:

1. Analyze the cookbook order:
   - Review the user's section on Cookbook Content, Cookbook Details, and User Details.
   - Pay special attention to dietary restrictions, allergies, skill level, and cooking goals.

2. Evaluate the provided recipes:
   - Consider the relevancy score and reasoning for each recipe.
   - Assess how well each recipe aligns with the cookbook order requirements.

3. Create a new recipe:
   - Blend elements from the most relevant recipes.
   - Ensure the new recipe meets the user's dietary needs and preferences.
   - Adjust the recipe to match the user's skill level and time constraints.
   - Incorporate elements that align with the cookbook's theme and cultural exploration goals.

4. Customize the recipe:
   - Modify ingredients and techniques to suit the user's dietary restrictions and allergies.
   - Adjust serving size to match the user's requirements.
   - Include alternatives or substitutions as specified in the cookbook order.

5. Enhance the recipe:
   - Add brief cultural context or storytelling elements.
   - Incorporate educational content related to techniques or ingredients.
   - Include practical features like meal prep tips or leftover ideas.

6. Format the output:
   - Use the provided Recipe class structure to organize the information.
   - Ensure all fields are filled out appropriately.
   - In the Notes field, include any relevant information about customizations, cultural context, or educational content.

7. Provide attribution:
   - Create a list of ""Inspired by"" URLs from the original recipes that contributed to the new recipe.

Output the curated recipe in the following JSON format:

```json
{
""title"": """",
""description"": """",
""servings"": """",
""prepTime"": """",
""cookTime"": """",
""totalTime"": """",
""ingredients"": [],
""directions"": [],
""notes"": """",
""inspiredBy"": ['list-of-urls']
}
```

""";

        var userPrompt = $"""
                         # Recipe data:
                         '''json
                         {JsonSerializer.Serialize(_mapper.Map<List<LlmRecipe>>(recipes))}
                         '''
                         # Cookbook Order:
                         {order}
                         
                         Remember to tailor the recipe to the user's specific needs and preferences while maintaining the integrity of the original recipes and the desired cookbook theme.
                         """;

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
                ingredients = new { type = "array", items = new { type = "string" } },
                directions = new { type = "array", items = new { type = "string" } },
                notes = new { type = "string" }
            },
            required = new[] { "title", "ingredients", "directions" }
        });

        try
        {
            var curatedRecipe = await _modelService.GetResponseAsync<LlmRecipe>(
                systemPrompt, userPrompt, functionName, functionDescription, functionParameters);

            _logger.LogInformation($"Curated recipe data: {JsonSerializer.Serialize(curatedRecipe)}");

            return curatedRecipe;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error cleaning recipe data: {ex.Message}");
            throw;
        }
    }

    public async Task<List<Recipe>> GetRecipes(string query)
    {
        // First, attempt to find and load existing JSON file
        var recipes = await LoadExistingRecipes(query);
        if (recipes is { Count: > 0 })
        {
            _logger.LogInformation($"Found existing recipes for query '{query}'. Returning {recipes.Count} recipes.");
            return recipes;
        }

        // If no existing file found, proceed with web scraping
        _siteSelectors = await Utils.LoadSiteSelectors();

        var allRecipes = new List<Recipe>();

        foreach (var site in _siteSelectors)
        {
            if (string.IsNullOrEmpty(site.Value["search_page"]))
            {
                _logger.LogInformation($"Site {site.Key} is missing the search page URL");
                continue;
            }

            var scrapedRecipes = await ScrapeRecipes(site.Key, query);
            if (scrapedRecipes.Count > 0)
            {
                allRecipes.AddRange(scrapedRecipes);
            }
        }

        if (allRecipes.Count > 0)
        {
            await Utils.SaveToJsonAsync(query, allRecipes);
            _logger.LogInformation($"Successfully saved {allRecipes.Count} recipes for {query}.");
        }
        else
        {
            _logger.LogInformation("Failed to retrieve data for any recipes.");
        }
        
        var topRecipes = await RankRecipes(allRecipes, query);
            
        var cleanedRecipes = await CleanRecipes(topRecipes);
        
        await Utils.SaveToJsonAsync(query, cleanedRecipes);

        return cleanedRecipes;
    }

    private async Task<List<Recipe>?> LoadExistingRecipes(string query)
    {
        var filePath = Path.Combine("Recipes", $"{Utils.SanitizeFileName(query)}.json");

        if (File.Exists(filePath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(filePath);
                var recipes = JsonSerializer.Deserialize<List<Recipe>>(json);
                return recipes;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error loading existing recipes for query '{query}'");
            }
        }

        return null;
    }

    private async Task<List<Recipe>> ScrapeRecipes(string site, string foodItem)
    {
        var selectors = _siteSelectors![site];
        var siteUrl = selectors["base_url"];

        if (string.IsNullOrEmpty(siteUrl))
        {
            siteUrl = $"https://{site}.com";
        }

        var searchUrl = $"{siteUrl}{selectors["search_page"]}{Uri.EscapeDataString(foodItem)}";
        var html = await Utils.GetHtmlAsync(searchUrl, _logger);
        if (string.IsNullOrEmpty(html))
        {
            return new List<Recipe>();
        }

        var context = BrowsingContext.New(Configuration.Default);
        var document = await context.OpenAsync(req => req.Content(html));

        var links = document.QuerySelectorAll(selectors["listed_recipe"]);
        var urls = links.Select(link => link.GetAttribute("href")).Distinct().ToList();

        if (urls.Count == 0)
        {
            _logger.LogInformation($"No recipes found for {foodItem} on {siteUrl}");
            return new List<Recipe>();
        }

        var recipes = new List<Recipe>();

        foreach (var url in urls)
        {
            var fullUrl = url!.StartsWith("https://") ? url : $"{siteUrl}{url}";
            var recipe = await ParseRecipe(fullUrl, selectors);
            if (recipe != null)
            {
                recipes.Add(recipe);
            }

            if (recipes.Count == MaxNumRecipesPerSite)
            {
                break;
            }
        }

        return recipes;
    }

    private async Task<Recipe?> ParseRecipe(string url, Dictionary<string, string> selectors)
    {
        var html = await Utils.GetHtmlAsync(url, _logger);
        if (string.IsNullOrEmpty(html))
        {
            return null;
        }

        var context = BrowsingContext.New(Configuration.Default);
        var document = await context.OpenAsync(req => req.Content(html));

        try
        {
            return new Recipe
            {
                RecipeUrl = url,
                Title = ExtractText(document, selectors["title"]),
                Description = ExtractText(document, selectors["description"]),
                ImageUrl = ExtractText(document, selectors["image"], "data-lazy-src") ??
                           ExtractText(document, selectors["image"], "src"),
                Servings = ExtractText(document, selectors["servings"]),
                PrepTime = ExtractText(document, selectors["prep_time"]),
                CookTime = ExtractText(document, selectors["cook_time"]),
                TotalTime = ExtractText(document, selectors["total_time"]),
                Ingredients = document.QuerySelectorAll(selectors["ingredients"]).Select(e => e.TextContent.Trim())
                    .ToList(),
                Directions = document.QuerySelectorAll(selectors["directions"]).Select(e => e.TextContent.Trim())
                    .ToList(),
                Notes = ExtractText(document, selectors["notes"])
            };
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error occurred during parse_recipe");
            return null;
        }
    }

    private string ExtractText(IDocument document, string selector, string? attribute = null)
        => Utils.ExtractText(_logger, document, selector, attribute);
}