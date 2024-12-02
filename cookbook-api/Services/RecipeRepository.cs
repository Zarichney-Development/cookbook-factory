using System.Collections.Concurrent;
using AutoMapper;
using Cookbook.Factory.Models;
using Cookbook.Factory.Prompts;
using Serilog;
using ILogger = Serilog.ILogger;

namespace Cookbook.Factory.Services;

public interface IRecipeRepository
{
    Task InitializeAsync();
    Task<List<Recipe>> SearchRecipes(string? query);
    Task AddUpdateRecipes(List<Recipe> recipes);
    bool ContainsRecipe(string recipeId);
}

public class RecipeRepository(
    RecipeConfig config,
    IFileService fileService,
    ILlmService llmService,
    IMapper mapper,
    CleanRecipePrompt cleanRecipePrompt,
    RecipeNamerPrompt recipeNamerPrompt)
    : IRecipeRepository
{
    private readonly ILogger _log = Log.ForContext<RecipeRepository>();

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Recipe>> _recipes =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, byte> _recipeIds = new();

    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private bool _isInitialized;

    public async Task InitializeAsync()
    {
        if (_isInitialized) return;

        await _initializationLock.WaitAsync();
        try
        {
            if (_isInitialized) return;

            _log.Information("Initializing RecipeRepository...");
            await LoadRecipesAsync();
            _isInitialized = true;
            _log.Information("RecipeRepository initialized successfully.");
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private async Task LoadRecipesAsync()
    {
        try
        {
            var recipeFiles = fileService.GetFiles(config.OutputDirectory);
            _log.Information("Found {count} recipe files.", recipeFiles.Length);

            var loadTasks = recipeFiles.Select(async file =>
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                try
                {
                    var recipes = await fileService.ReadFromFile<List<Recipe>>(config.OutputDirectory, fileName);
                    foreach (var recipe in recipes)
                    {
                        AddRecipeToRepository(recipe);
                    }
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "Error loading recipes from file: {FileName}", fileName);
                }
            });

            await Task.WhenAll(loadTasks);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error loading recipes");
            throw;
        }
    }

    public bool ContainsRecipe(string recipeId)
    {
        return _recipeIds.ContainsKey(recipeId);
    }

    private void AddRecipeToRepository(Recipe recipe)
    {
        if (string.IsNullOrEmpty(recipe.Id))
        {
            if (string.IsNullOrEmpty(recipe.RecipeUrl))
            {
                _log.Warning("Recipe with missing ID and URL cannot be added.");
                return;
            }

            recipe.Id = WebScraperService.GenerateUrlFingerprint(recipe.RecipeUrl);
        }

        _recipeIds.TryAdd(recipe.Id, 0);

        // For each title & alias, map this recipe to the dictionary for fast access
        AddToDictionary(recipe.Title!, recipe);
        foreach (var alias in recipe.Aliases)
        {
            AddToDictionary(alias, recipe);
        }
    }

    private void AddToDictionary(string key, Recipe recipe)
    {
        var recipeDict = _recipes.GetOrAdd(key, _ => new ConcurrentDictionary<string, Recipe>());
        recipeDict.AddOrUpdate(recipe.Id!, recipe, (_, _) => recipe);
    }

    public async Task<List<Recipe>> SearchRecipes(string? query)
    {
        if (!_isInitialized)
        {
            _log.Warning("Attempting to search before initialization. Initializing now.");
            await InitializeAsync();
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("Search query cannot be empty", nameof(query));
        }

        _log.Information("Searching for recipes matching query: {Query}", query);

        return await Task.Run(() =>
        {
            var results = new ConcurrentDictionary<string, Recipe>();

            // Exact matches
            if (_recipes.TryGetValue(query, out var exactMatches))
            {
                foreach (var recipe in exactMatches.Values)
                {
                    results.TryAdd(recipe.Id!, recipe);
                }
            }

            // Fuzzy matches
            foreach (var kvp in _recipes)
            {
                var key = kvp.Key;
                if (key.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    query.Contains(key, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var recipe in kvp.Value.Values)
                    {
                        results.TryAdd(recipe.Id!, recipe);
                    }
                }
            }

            var recipes = results.Values
                .OrderByDescending(r => CalculateRelevanceScore(r, query))
                .ToList();

            _log.Information("Found {count} recipes matching query: {query}", recipes.Count, query);

            return recipes;
        });
    }

    public async Task AddUpdateRecipes(List<Recipe> recipes)
    {
        await CleanUncleanedRecipesAsync(recipes);

        // Process recipes and organize them into new files
        var filesToWrite = new ConcurrentDictionary<string, ConcurrentBag<Recipe>>();

        await Parallel.ForEachAsync(recipes, new ParallelOptions
        {
            MaxDegreeOfParallelism = config.MaxParallelTasks
        }, async (recipe, _) =>
        {
            try
            {
                await IndexAndRenameRecipeAsync(recipe);

                // Add to filesToWrite
                var recipeBag = filesToWrite.GetOrAdd(recipe.IndexTitle!, _ => []);
                recipeBag.Add(recipe);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Error processing recipe '{RecipeId}'", recipe.Id);
            }
        });

        foreach (var (title, recipeBag) in filesToWrite)
        {
            var recipeList = recipeBag.ToList();

            try
            {
                foreach (var recipe in recipeList)
                {
                    AddRecipeToRepository(recipe);
                }

                var existingRecipes =
                    await fileService.ReadFromFile<List<Recipe>?>(config.OutputDirectory, title) ?? [];

                var combinedRecipes = UpdateExistingRecipes(existingRecipes, recipeList);

                fileService.WriteToFileAsync(config.OutputDirectory, title, combinedRecipes);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Error writing recipes to file: {Title}", title);
            }
        }
    }

    private List<Recipe> UpdateExistingRecipes(List<Recipe> existingRecipes, List<Recipe> newRecipes)
    {
        // Create a dictionary for existing recipes by Id
        var existingRecipesDict = existingRecipes.ToDictionary(r => r.Id!);

        foreach (var newRecipe in newRecipes)
        {
            var recipeId = newRecipe.Id!;
            if (existingRecipesDict.TryGetValue(recipeId, out var existingRecipe))
            {
                // Update existing recipe with new data
                existingRecipe.Relevancy = newRecipe.Relevancy;
                existingRecipe.Cleaned = newRecipe.Cleaned || existingRecipe.Cleaned;
            }
            else
            {
                // Add new recipe
                existingRecipesDict[recipeId] = newRecipe;
            }
        }

        // Return the combined list
        return existingRecipesDict.Values.ToList();
    }

    private async Task IndexAndRenameRecipeAsync(Recipe recipe)
    {
        if (string.IsNullOrEmpty(recipe.IndexTitle))
        {
            try
            {
                var result = await llmService.CallFunction<RenamerResult>(
                    recipeNamerPrompt.SystemPrompt,
                    recipeNamerPrompt.GetUserPrompt(recipe),
                    recipeNamerPrompt.GetFunction()
                );

                _log.Information("Received response from model for recipe {RecipeTitle}: {@Result}", recipe.Title,
                    result);

                recipe.Aliases = result.Aliases.Select(a => a.Replace("Print Pin It", "").Trim()).ToList();
                recipe.IndexTitle = result.IndexTitle;
                recipe.Title = recipe.Title?.Replace("Print Pin It", "").Trim();
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Error processing recipe '{RecipeId}'", recipe.Id);
            }
        }
    }

    private static double CalculateRelevanceScore(Recipe recipe, string query)
    {
        // Simple relevance scoring based on title match
        if (recipe.Title!.Equals(query, StringComparison.OrdinalIgnoreCase))
            return 1.0;
        if (recipe.Title!.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 0.8;
        if (recipe.Aliases.Any(a => a.Equals(query, StringComparison.OrdinalIgnoreCase)))
            return 0.6;
        if (recipe.Aliases.Any(a => a.Contains(query, StringComparison.OrdinalIgnoreCase)))
            return 0.4;
        return 0.2; // Fallback score for partial matches
    }

    private async Task CleanUncleanedRecipesAsync(List<Recipe> recipes)
    {
        var uncleanedRecipes = recipes.Where(r => !r.Cleaned).ToList();
        if (uncleanedRecipes.Count != 0)
        {
            var cleanedRecipes = await CleanRecipesAsync(uncleanedRecipes);

            // Remove uncleaned recipes from the list
            recipes.RemoveAll(r => !r.Cleaned);

            // Add cleaned recipes to the list
            recipes.AddRange(cleanedRecipes);
        }
    }

    private async Task<List<Recipe>> CleanRecipesAsync(List<Recipe> recipes)
    {
        if (recipes.Count == 0)
        {
            return [];
        }

        var cleanedRecipes = new ConcurrentBag<Recipe>();

        await Parallel.ForEachAsync(
            recipes,
            new ParallelOptions { MaxDegreeOfParallelism = config.MaxParallelTasks },
            async (recipe, _) =>
            {
                try
                {
                    var cleanedRecipe = await CleanRecipeData(recipe);
                    cleanedRecipes.Add(cleanedRecipe);
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "Error cleaning recipe with Id: {RecipeId}", recipe.Id);
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
                cleanRecipePrompt.GetFunction(),
                1 // Don't retry
            );

            _log.Information("Cleaned recipe data: {@CleanedRecipe}", cleanedRecipe);

            // Create a new Recipe instance
            var newRecipe = mapper.Map<Recipe>(cleanedRecipe);

            // Copy over properties not part of CleanedRecipe
            mapper.Map(recipe, newRecipe);

            // Ensure Cleaned flag is set
            newRecipe.Cleaned = true;

            return newRecipe;
        }
        catch (OpenAiContentFilterException ex)
        {
            _log.Warning(ex, "Unable to clean recipe data due to getting flagged by content filtering");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error cleaning recipe data: {Message}", ex.Message);
        }

        // Return the original recipe
        return recipe;
    }
}