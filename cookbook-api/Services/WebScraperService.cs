using System.Collections.Concurrent;
using System.Text.Json;
using AngleSharp;
using AngleSharp.Dom;
using Cookbook.Factory.Models;
using ILogger = Cookbook.Factory.Logging.ILogger;

namespace Cookbook.Factory.Services;

public class WebScraperService(ILogger logger)
{
    private const int MaxNumRecipesPerSite = 3;
    private const int MaxParallelTasks = MaxNumRecipesPerSite;
    private const int MaxParallelSites = 5;

    private static Dictionary<string, Dictionary<string, string>>? _siteSelectors;

    internal async Task<List<ScrapedRecipe>> ScrapeForRecipesAsync(string query)
    {
        // Pulls the list of sites from config with their selectors
        _siteSelectors = await LoadSiteSelectors();

        var allRecipes = new ConcurrentBag<ScrapedRecipe>();
        var tasks = new List<Task>();
        var semaphore = new SemaphoreSlim(MaxParallelSites);

        foreach (var site in _siteSelectors)
        {
            if (string.IsNullOrEmpty(site.Value["base_url"]) || string.IsNullOrEmpty(site.Value["search_page"]))
            {
                if (string.IsNullOrEmpty(site.Value["search_page"]))
                {
                    logger.LogWarning($"Site {site.Key} is missing the search page URL");
                }

                continue;
            }

            await semaphore.WaitAsync();

            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    // Use the search page of each site to collect urls for recipe pages
                    var recipeUrls = await SearchSiteForRecipeUrls(site.Key, query);

                    // Use the site's selectors to extract the text from the html and return the data
                    var scrapedRecipes = await ScrapeSiteForRecipesAsync(site.Key, recipeUrls);

                    foreach (var recipe in scrapedRecipes)
                    {
                        allRecipes.Add(recipe);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, $"Error scraping recipes from site: {site.Key}");
                }
                finally
                {
                    semaphore.Release();
                }
            }));
        }

        await Task.WhenAll(tasks);

        var recipes = allRecipes.ToList();
        logger.LogInformation("Web scraped {count} recipes for {query}", recipes.Count, query);

        return recipes;
    }

    private async Task<List<string>> SearchSiteForRecipeUrls(string site, string query)
    {
        var selectors = _siteSelectors![site];
        var siteUrl = selectors["base_url"];

        if (string.IsNullOrEmpty(siteUrl))
        {
            siteUrl = $"https://{site}.com";
        }

        var searchUrl = $"{siteUrl}{selectors["search_page"]}{Uri.EscapeDataString(query)}";

        // Extract the html from the search page
        var html = await Utils.SendGetRequestForHtml(searchUrl, logger);

        if (string.IsNullOrEmpty(html))
        {
            logger.LogWarning("Failed to retrieve HTML content for '{query}' on {site}", query, site);
            return new List<string>();
        }

        var browserContext = BrowsingContext.New(Configuration.Default);
        var htmlDoc = await browserContext.OpenAsync(req => req.Content(html));

        var links = htmlDoc.QuerySelectorAll(selectors["listed_recipe"]);
        var recipeUrls = links.Select(link => link.GetAttribute("href")).Distinct()
            .Where(url => !string.IsNullOrEmpty(url)).ToList();

        if (!recipeUrls.Any())
        {
            logger.LogInformation("No recipes found for '{query}' on {site}", query, site);
            return new List<string>();
        }

        logger.LogInformation("Found {count} recipes for '{query}' on {site}", recipeUrls.Count, query, site);
        return recipeUrls!;
    }

    private async Task<List<ScrapedRecipe>> ScrapeSiteForRecipesAsync(string site, List<string> recipeUrls)
    {
        var scrapedRecipes = new ConcurrentBag<ScrapedRecipe>();
        var tasks = new List<Task>();
        var semaphore = new SemaphoreSlim(MaxParallelTasks);

        foreach (var url in recipeUrls.TakeWhile(_ => scrapedRecipes.Count < MaxNumRecipesPerSite))
        {
            await semaphore.WaitAsync();

            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var fullUrl = url.StartsWith("https://") ? url : $"{_siteSelectors![site]["base_url"]}{url}";
                    try
                    {
                        var recipe = await ParseRecipeFromSite(fullUrl, _siteSelectors![site]);
                        scrapedRecipes.Add(recipe);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, $"Error parsing recipe from URL: {fullUrl}");
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            }));
        }

        await Task.WhenAll(tasks);

        return scrapedRecipes.ToList();
    }

    private async Task<ScrapedRecipe> ParseRecipeFromSite(string url, Dictionary<string, string> selectors)
    {
        var html = await GetHtml(url);
        if (string.IsNullOrEmpty(html))
        {
            throw new Exception("Failed to retrieve HTML content");
        }

        var browserContext = BrowsingContext.New(Configuration.Default);
        var htmlDoc = await browserContext.OpenAsync(req => req.Content(html));

        return new ScrapedRecipe
        {
            Ingredients = ExtractList(htmlDoc, selectors["ingredients"])
                          ?? throw new Exception("No ingredients found"),
            Directions = ExtractList(htmlDoc, selectors["directions"])
                         ?? throw new Exception("No ingredients found"),
            RecipeUrl = url,
            Title = ExtractText(htmlDoc, selectors["title"]),
            Description = ExtractText(htmlDoc, selectors["description"]),
            Servings = ExtractText(htmlDoc, selectors["servings"]),
            PrepTime = ExtractText(htmlDoc, selectors["prep_time"]),
            CookTime = ExtractText(htmlDoc, selectors["cook_time"]),
            TotalTime = ExtractText(htmlDoc, selectors["total_time"]),
            Notes = ExtractText(htmlDoc, selectors["notes"]),
            ImageUrl = ExtractText(htmlDoc, selectors["image"], "data-lazy-src") ??
                       ExtractText(htmlDoc, selectors["image"], "src"),
        };
    }

    private async Task<string> GetHtml(string url)
        => await Utils.SendGetRequestForHtml(url, logger);

    private static List<string>? ExtractList(IDocument document, string selector)
    {
        var results = document.QuerySelectorAll(selector).Select(e => e.TextContent.Trim()).ToList();
        return results.Count == 0 ? null : results;
    }

    private string? ExtractText(IDocument document, string selector, string? attribute = null)
        => Utils.ExtractTextFromHtmlDoc(logger, document, selector, attribute);

    private static async Task<Dictionary<string, Dictionary<string, string>>> LoadSiteSelectors()
    {
        var json = await File.ReadAllTextAsync("site_selectors.json");
        return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json)!;
    }
}