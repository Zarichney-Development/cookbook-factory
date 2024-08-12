using System.Collections.Concurrent;
using Cookbook.Factory.Models;
using Cookbook.Factory.Prompts;
using Serilog;
using ILogger = Serilog.ILogger;

namespace Cookbook.Factory.Services;

public interface IRecipeRepository
{
    Task InitializeAsync();
    Task<List<Recipe>> SearchRecipes(string query);
    Task AddRecipes(List<Recipe> recipes);
    bool ContainsRecipe(string recipeId);
}

public class RecipeRepository(
    RecipeConfig config,
    FileService fileService,
    ILlmService llmService,
    RecipeNamerPrompt recipeNamerPrompt)
    : IRecipeRepository
{
    private readonly ILogger _log = Log.ForContext<RecipeRepository>();
    private readonly ConcurrentDictionary<string, List<Recipe>> _recipes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentBag<string> _recipeIds = new();

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
            var recipeFiles = Directory.GetFiles(config.OutputDirectory);
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
                    _log.Error(ex, $"Error loading recipes from file: {fileName}");
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
        return _recipeIds.Contains(recipeId);
    }

    private void AddRecipeToRepository(Recipe recipe)
    {
        if (string.IsNullOrEmpty(recipe.Id))
        {
            if (string.IsNullOrEmpty(recipe.RecipeUrl))
            {
                _log.Warning("Recipe with ID {id} has no URL", recipe.Id);
                return;
            }

            recipe.Id = WebScraperService.GenerateUrlFingerprint(recipe.RecipeUrl);
        }

        if (ContainsRecipe(recipe.Id))
        {
            _recipeIds.Add(recipe.Id);
        }

        AddToDictionary(recipe.Title, recipe);
        foreach (var alias in recipe.Aliases)
        {
            AddToDictionary(alias, recipe);
        }
    }

    private void AddToDictionary(string key, Recipe recipe)
    {
        _recipes.AddOrUpdate(key,
            _ => [recipe],
            (_, list) =>
            {
                if (list.Contains(recipe))
                {
                    // Update existing
                    list[list.IndexOf(recipe)] = recipe;
                }
                else
                {
                    list.Add(recipe);
                }

                return list;
            });
    }

    public async Task<List<Recipe>> SearchRecipes(string query)
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

        _log.Information($"Searching for recipes matching query: {query}");

        return await Task.Run(() =>
        {
            var results = new List<Recipe>();
            if (_recipes.TryGetValue(query, out var exactMatches))
            {
                results.AddRange(exactMatches);
            }

            var fuzzyMatches = _recipes
                .Where(kvp =>
                    kvp.Key.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    query.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                .SelectMany(kvp => kvp.Value)
                .Distinct()
                .ToList();

            results.AddRange(fuzzyMatches);

            var recipes = results
                .DistinctBy(r => r.Id)
                .OrderByDescending(r => CalculateRelevanceScore(r, query))
                .ToList();

            _log.Information("Found {count} recipes matching query: {query}", recipes.Count, query);

            return recipes;
        });
    }

    public async Task AddRecipes(List<Recipe> recipes)
    {
        // Process recipes and organize them into new files
        var filesToWrite = new ConcurrentDictionary<string, List<Recipe>>();
        var tasks = recipes.Select(async recipe =>
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

                    Log.Information("Received response from model for recipe {recipe}: {@result}", recipe.Title,
                        result);

                    recipe.Aliases = result.Aliases.Select(a => a.Replace("Print Pin It", "").Trim()).ToList();
                    recipe.IndexTitle = result.IndexTitle;
                    recipe.Title = recipe.Title.Replace("Print Pin It", "").Trim();
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "Error processing recipe '{RecipeId}'", recipe.Id);
                }
            }

            lock (filesToWrite)
            {
                if (!filesToWrite.TryGetValue(recipe.IndexTitle, out var recipeList))
                {
                    recipeList = new List<Recipe>();
                    filesToWrite[recipe.IndexTitle] = recipeList;
                }

                recipeList.Add(recipe);
            }
        });

        // Wait until all recipes have been indexed
        await Task.WhenAll(tasks);

        foreach (var (title, recipeList) in filesToWrite)
        {
            foreach (var recipe in recipeList)
            {
                AddRecipeToRepository(recipe);
            }

            var existingRecipes = await fileService.ReadFromFile<List<Recipe>?>(config.OutputDirectory, title) ?? [];

            var newIds = recipeList.Select(r => r.Id).ToList();
            var unchangedRecipes = existingRecipes
                .Where(r => !newIds.Contains(r.Id))
                .ToList();

            var combinedRecipes = recipeList
                .Concat(unchangedRecipes)
                .ToList();

            fileService.WriteToFile(config.OutputDirectory, title, combinedRecipes);
        }
    }

    private static double CalculateRelevanceScore(Recipe recipe, string query)
    {
        // Simple relevance scoring based on title match
        if (recipe.Title.Equals(query, StringComparison.OrdinalIgnoreCase))
            return 1.0;
        if (recipe.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 0.8;
        if (recipe.Aliases.Any(a => a.Equals(query, StringComparison.OrdinalIgnoreCase)))
            return 0.6;
        if (recipe.Aliases.Any(a => a.Contains(query, StringComparison.OrdinalIgnoreCase)))
            return 0.4;
        return 0.2; // Fallback score for partial matches
    }
}