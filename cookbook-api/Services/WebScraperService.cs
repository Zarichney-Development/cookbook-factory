using System.Collections.Concurrent;
using System.Text.Json;
using AngleSharp;
using AngleSharp.Dom;
using Cookbook.Factory.Models;
using Cookbook.Factory.Prompts;
using Serilog;
using ILogger = Serilog.ILogger;

namespace Cookbook.Factory.Services;

public class WebscraperConfig : IConfig
{
    public int MaxNumRecipesPerSite { get; init; } = 3;
    public int MaxParallelTasks { get; init; } = 5;
    public int MaxParallelSites { get; init; } = 5;
}

public class WebScraperService(WebscraperConfig config, ChooseRecipesPrompt chooseRecipesPrompt, ILlmService llmService)
{
    private readonly ILogger _log = Log.ForContext<WebScraperService>();

    private static Dictionary<string, Dictionary<string, string>>? _siteSelectors;

    internal async Task<List<ScrapedRecipe>> ScrapeForRecipesAsync(string query)
    {
        // Pulls the list of sites from config with their selectors
        _siteSelectors = await LoadSiteSelectors();

        var allRecipes = new ConcurrentBag<ScrapedRecipe>();
        var tasks = new List<Task>();
        var semaphore = new SemaphoreSlim(config.MaxParallelSites);

        foreach (var site in _siteSelectors)
        {
            if (string.IsNullOrEmpty(site.Value["base_url"]) || string.IsNullOrEmpty(site.Value["search_page"]))
            {
                if (string.IsNullOrEmpty(site.Value["search_page"]))
                {
                    _log.Warning("Site {site} is missing the search page URL", site.Key);
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

                    // Conditionally use LLM to return the most relevant URLs
                    var relevantUrls = await SelectMostRelevantUrls(site.Key, recipeUrls, query);
                    
                    // Use the site's selectors to extract the text from the html and return the data
                    var scrapedRecipes = await ScrapeSiteForRecipesAsync(site.Key, relevantUrls, query);

                    foreach (var recipe in scrapedRecipes)
                    {
                        allRecipes.Add(recipe);
                    }
                }
                catch (Exception ex)
                {
                    _log.Error(ex, $"Error scraping recipes from site: {site.Key}");
                }
                finally
                {
                    semaphore.Release();
                }
            }));
        }

        await Task.WhenAll(tasks);

        var recipes = allRecipes.ToList();
        _log.Information("Web scraped {count} recipes for {query}", recipes.Count, query);

        return recipes;
    }

    private async Task<List<string>> SelectMostRelevantUrls(string site, List<string> recipeUrls, string query)
    {
        if (recipeUrls.Count <= config.MaxNumRecipesPerSite)
        {
            return recipeUrls;
        }
        
        try
        {
            var result = await llmService.CallFunction<SearchResult>(
                chooseRecipesPrompt.SystemPrompt,
                chooseRecipesPrompt.GetUserPrompt(query, recipeUrls, config.MaxNumRecipesPerSite),
                chooseRecipesPrompt.GetFunction()
            );

            var indices = result.SelectedIndices;

            if (indices.Count == 0)
            {
                throw new Exception("No indices selected");
            }

            var selectedUrls = recipeUrls.Where((_, index) => indices.Contains(index + 1)).ToList();
            _log.Information("Selected {count} URLs for {query} on {site}", selectedUrls.Count, query, site);

            if (selectedUrls.Count == 0)
            {
                throw new Exception("Index mismatch issue");
            }

            return selectedUrls;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error selecting URLs for {query} on {site}. Urls: {@Urls}", query, site, recipeUrls);
        }

        return recipeUrls;
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
        var html = await SendGetRequestForHtml(searchUrl);

        if (string.IsNullOrEmpty(html))
        {
            _log.Warning("Failed to retrieve HTML content for '{query}' on {site}", query, site);
            return new List<string>();
        }

        var browserContext = BrowsingContext.New(Configuration.Default);
        var htmlDoc = await browserContext.OpenAsync(req => req.Content(html));

        var links = htmlDoc.QuerySelectorAll(selectors["listed_recipe"]);
        var recipeUrls = links.Select(link => link.GetAttribute("href")).Distinct()
            .Where(url => !string.IsNullOrEmpty(url)).ToList();

        if (!recipeUrls.Any())
        {
            _log.Information("No recipes found for '{query}' on {site}", query, site);
            return new List<string>();
        }

        _log.Information("Found {count} recipes for '{query}' on {site}", recipeUrls.Count, query, site);
        return recipeUrls!;
    }

    private async Task<List<ScrapedRecipe>> ScrapeSiteForRecipesAsync(string site, List<string> recipeUrls,
        string query)
    {
        var scrapedRecipes = new ConcurrentQueue<ScrapedRecipe>();
        var semaphore = new SemaphoreSlim(config.MaxParallelTasks);

        _log.Information("Scraping the first {count} recipes from {site} for {recipe}", config.MaxNumRecipesPerSite,
            site,
            query);

        await Parallel.ForEachAsync(recipeUrls.TakeWhile(_ => scrapedRecipes.Count < config.MaxNumRecipesPerSite),
            new ParallelOptions { MaxDegreeOfParallelism = config.MaxParallelTasks },
            async (url, ct) =>
            {
                await semaphore.WaitAsync(ct);

                try
                {
                    var fullUrl = url.StartsWith("https://") ? url : $"{_siteSelectors![site]["base_url"]}{url}";
                    try
                    {
                        _log.Information("Scraping {recipe} recipe from {url}", query, url);
                        var recipe = await ParseRecipeFromSite(fullUrl, _siteSelectors![site]);
                        scrapedRecipes.Enqueue(recipe);
                    }
                    catch (Exception ex)
                    {
                        _log.Warning(ex, $"Error parsing recipe from URL: {fullUrl}");
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            });

        return scrapedRecipes.ToList();
    }

    private async Task<ScrapedRecipe> ParseRecipeFromSite(string url, Dictionary<string, string> selectors)
    {
        var html = await SendGetRequestForHtml(url);
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

    private string? ExtractText(IDocument document, string selector, string? attribute = null)
        => ExtractTextFromHtmlDoc(document, selector, attribute);

    private string? ExtractTextFromHtmlDoc(IDocument document, string selector, string? attribute = null)
    {
        if (string.IsNullOrEmpty(selector)) return null;

        try
        {
            var element = document.QuerySelector(selector);
            if (element != null)
            {
                return attribute != null ? element.GetAttribute(attribute)! : element.TextContent.Trim();
            }
        }
        catch (Exception e)
        {
            _log.Error(e, $"Error occurred with selector {selector} during extract_text");
        }

        return null;
    }

    private async Task<string> SendGetRequestForHtml(string url)
    {
        try
        {
            _log.Information("Running GET request for URL: {url}", url);
            var client = new HttpClient();
            var response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }
        catch (HttpRequestException e)
        {
            _log.Warning(e, "HTTP error occurred for url {urL}", url);
        }
        catch (TaskCanceledException e)
        {
            _log.Warning(e, "Timeout occurred for url {urL}", url);
        }
        catch (Exception e)
        {
            _log.Warning(e, "Error occurred during GetHtmlAsync for url {urL}", url);
        }

        return null!;
    }

    private static List<string>? ExtractList(IDocument document, string selector)
    {
        var results = document.QuerySelectorAll(selector).Select(e => e.TextContent.Trim()).ToList();
        return results.Count == 0 ? null : results;
    }

    private static async Task<Dictionary<string, Dictionary<string, string>>> LoadSiteSelectors()
    {
        var json = await File.ReadAllTextAsync("site_selectors.json");
        return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json)!;
    }
}