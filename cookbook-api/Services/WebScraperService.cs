using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AngleSharp;
using AngleSharp.Dom;
using Cookbook.Factory.Config;
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

public class WebScraperService(
    WebscraperConfig config,
    ChooseRecipesPrompt chooseRecipesPrompt,
    ILlmService llmService,
    IFileService fileService
)
{
    private readonly ILogger _log = Log.ForContext<WebScraperService>();

    private static Dictionary<string, Dictionary<string, string>>? _siteSelectors;

    internal async Task<List<ScrapedRecipe>> ScrapeForRecipesAsync(string query, string? targetSite = null)
    {
        // Pulls the list of sites from config with their selectors
        _siteSelectors ??= await LoadSiteSelectors();

        // Randomize sites to increase diversification over repeated queries
        _siteSelectors = _siteSelectors.OrderBy(_ => Guid.NewGuid()).ToDictionary(pair => pair.Key, pair => pair.Value);

        // Filter out sites that are missing required selectors or are not the target site
        var sitesToProcess = _siteSelectors.Where(site =>
        {
            if (string.IsNullOrEmpty(site.Value["base_url"]) || string.IsNullOrEmpty(site.Value["search_page"]))
            {
                if (string.IsNullOrEmpty(site.Value["search_page"]))
                {
                    _log.Warning("Site {site} is missing the search page URL", site.Key);
                }

                return false;
            }

            return string.IsNullOrEmpty(targetSite) || site.Key == targetSite;
        });

        var allSiteRecipes = new ConcurrentBag<KeyValuePair<string,List<ScrapedRecipe>>>();

        await Parallel.ForEachAsync(sitesToProcess,
            new ParallelOptions { MaxDegreeOfParallelism = config.MaxParallelSites },
            async (site, _) =>
            {
                try
                {
                    // Use the search page of each site to collect urls for recipe pages
                    var recipeUrls = await SearchSiteForRecipeUrls(site.Key, query);

                    // Rank and filter down search results using LLM to return the most relevant URLs in respect to max per site
                    var relevantUrls = await SelectMostRelevantUrls(site.Key, recipeUrls, query);

                    if (recipeUrls.Count > 0)
                    {
                        // Use the site's selectors to extract the text from the html and return the data
                        var scrapedRecipes = await ScrapeSiteForRecipesAsync(site.Key, relevantUrls, query);
                        
                        allSiteRecipes.Add(new KeyValuePair<string, List<ScrapedRecipe>>(site.Key, scrapedRecipes));
                    }
                }
                catch (Exception ex)
                {
                    _log.Error(ex, $"Error scraping recipes from site: {site.Key}");
                }
            });

        // Sort all recipes by the top choice from each site before going through the next best choice
        var allRecipes = InterleaveRecipes(allSiteRecipes);

        _log.Information("Web scraped a total of {count} recipes for {query}", allRecipes.Count, query);

        return allRecipes;
    }
    
    /// <summary>
    /// Flattens the results from each site into ranked single list
    /// </summary>
    /// <param name="allSiteRecipes">The collection of scraped recipe</param>
    /// <returns>A single list, ordered by each site's top choices</returns>
    private static List<ScrapedRecipe> InterleaveRecipes(ConcurrentBag<KeyValuePair<string, List<ScrapedRecipe>>> allSiteRecipes)
    {
        // Pre-calculate the total capacity needed to avoid resizing
        var totalCapacity = allSiteRecipes.Sum(kvp => kvp.Value.Count);
        var recipeLists = new List<ScrapedRecipe>(totalCapacity);
    
        // Convert ConcurrentBag to array once for better performance
        var allRecipes = allSiteRecipes.Select(kvp => kvp.Value).ToArray();
    
        if (allRecipes.Length == 0)
            return recipeLists;

        // Use Span<T> for better performance when accessing arrays
        var currentIndices = allRecipes.Length <= 1000
            ? stackalloc int[allRecipes.Length]
            : new int[allRecipes.Length];
    
        // Track remaining items for early termination
        var remainingItems = totalCapacity;
    
        while (remainingItems > 0)
        {
            var addedInRound = false;
        
            for (var siteIndex = 0; siteIndex < allRecipes.Length; siteIndex++)
            {
                var currentIndex = currentIndices[siteIndex];
                var recipeList = allRecipes[siteIndex];
            
                if (currentIndex < recipeList.Count)
                {
                    recipeLists.Add(recipeList[currentIndex]);
                    currentIndices[siteIndex]++;
                    remainingItems--;
                    addedInRound = true;
                }
            }
        
            // If no items were added in this round, we're done
            if (!addedInRound)
                break;
        }

        return recipeLists;
    }

    private async Task<List<string>> SelectMostRelevantUrls(string site, List<string> recipeUrls, string? query)
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
            return [];
        }

        var browserContext = BrowsingContext.New(Configuration.Default);
        var htmlDoc = await browserContext.OpenAsync(req => req.Content(html));

        var links = htmlDoc.QuerySelectorAll(selectors["listed_recipe"]);
        var recipeUrls = links.Select(link => link.GetAttribute("href")).Distinct()
            .Where(url => !string.IsNullOrEmpty(url)).ToList();

        if (recipeUrls.Count == 0)
        {
            _log.Information("No recipes found for '{query}' on site {site}", query, site);
            return [];
        }

        _log.Information("Returned {count} search results for recipe '{query}' on site: {site}", recipeUrls.Count,
            query, site);
        return recipeUrls!;
    }
    
    private async Task<List<ScrapedRecipe>> ScrapeSiteForRecipesAsync(string site, List<string> recipeUrls, string? query)
    {
        var scrapedRecipes = new ConcurrentBag<ScrapedRecipe>();
        var amountToScrape = Math.Min(recipeUrls.Count, config.MaxNumRecipesPerSite);

        _log.Information("Scraping {count} recipes from site '{site}' for recipe '{recipe}'",
            amountToScrape, site, query);

        var urlsToProcess = recipeUrls.Take(amountToScrape);

        await Parallel.ForEachAsync(urlsToProcess,
            new ParallelOptions { MaxDegreeOfParallelism = config.MaxParallelTasks },
            async (url, _) =>
            {
                try
                {
                    var fullUrl = url.StartsWith("https://") ? url : $"{_siteSelectors![site]["base_url"]}{url}";
                    try
                    {
                        _log.Information("Scraping {recipe} recipe from {url}", query, url);
                        var recipe = await ParseRecipeFromSite(fullUrl, _siteSelectors![site]);
                        scrapedRecipes.Add(recipe);
                    }
                    catch (Exception ex)
                    {
                        _log.Warning(ex, $"Error parsing recipe from URL: {fullUrl}");
                    }
                }
                catch (Exception ex)
                {
                    _log.Error(ex, $"Error in scraping URL: {url}");
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
            Id = GenerateUrlFingerprint(url),
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

    public static string GenerateUrlFingerprint(string url)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(url));
        var builder = new StringBuilder();
        foreach (var b in bytes)
        {
            builder.Append(b.ToString("x2"));
        }

        return builder.ToString();
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

    private async Task<Dictionary<string, Dictionary<string, string>>> LoadSiteSelectors()
        => await fileService.ReadFromFile<Dictionary<string, Dictionary<string, string>>>("Config", "site_selectors");
}