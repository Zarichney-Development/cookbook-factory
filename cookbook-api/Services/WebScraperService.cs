using System.Collections.Concurrent;
using System.Net;
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
    public int MaxWaitTimeMs { get; init; } = 10000;
    public int MaxParallelPages { get; init; } = 2;
}

public class WebScraperService(
    WebscraperConfig config,
    ChooseRecipesPrompt chooseRecipesPrompt,
    ILlmService llmService,
    IFileService fileService,
    IBrowserService browserService
)
{
    private readonly ILogger _log = Log.ForContext<WebScraperService>();

    private static Dictionary<string, Dictionary<string, string>>? _siteSelectors;
    private static Dictionary<string, Dictionary<string, string>>? _siteTemplates;

    internal async Task<List<ScrapedRecipe>> ScrapeForRecipesAsync(string query, string? targetSite = null)
    {
        // Pulls the list of sites from config with their selectors
        await LoadSiteSelectors();

        var sitesToProcess = _siteSelectors!
            .Where(site => string.IsNullOrEmpty(targetSite) || site.Key == targetSite);

        var allSiteRecipes = new ConcurrentBag<KeyValuePair<string, List<ScrapedRecipe>>>();

        await Parallel.ForEachAsync(sitesToProcess,
            new ParallelOptions { MaxDegreeOfParallelism = config.MaxParallelSites },
            async (site, _) =>
            {
                try
                {
                    // Use the search page of each site to collect urls for recipe pages
                    var recipeUrls = await SearchSiteForRecipeUrls(site.Key, query);

                    if (recipeUrls.Count > 0)
                    {
                        // Rank and filter down search results using LLM to return the most relevant URLs in respect to max per site
                        var relevantUrls = await SelectMostRelevantUrls(site.Key, recipeUrls, query);

                        if (relevantUrls.Count > 0)
                        {
                            // Use the site's selectors to extract the text from the html and return the data
                            var scrapedRecipes = await ScrapeSiteForRecipesAsync(site.Key, relevantUrls, query);

                            allSiteRecipes.Add(new KeyValuePair<string, List<ScrapedRecipe>>(site.Key, scrapedRecipes));
                        }
                    }
                    else
                    {
                        _log.Debug("No recipes found for {query} on site {site}", query, site.Key);
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
    private static List<ScrapedRecipe> InterleaveRecipes(
        ConcurrentBag<KeyValuePair<string, List<ScrapedRecipe>>> allSiteRecipes)
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
        var siteUrl = selectors.GetValueOrDefault("base_url", $"https://{site}.com");

        var searchQuery = selectors["search_page"];

        if (searchQuery.Contains("{query}"))
        {
            searchQuery = searchQuery.Replace("{query}", Uri.EscapeDataString(query));
        }
        else
        {
            searchQuery += Uri.EscapeDataString(query);
        }

        var searchUrl = $"{siteUrl}{searchQuery}";

        var isStreamSearch = selectors.TryGetValue("stream_search", out var streamSearchValue) &&
                             streamSearchValue == "true";

        var recipeUrls = isStreamSearch
            ? await browserService.GetContentAsync(searchUrl, selectors["search_results"])
            : await ExtractRecipeUrls(searchUrl, selectors);

        if (recipeUrls.Count == 0)
        {
            _log.Information("No search results found for '{query}' on site {site}", query, site);
            return [];
        }

        _log.Information("Returned {count} search results for recipe '{query}' on site: {site}", recipeUrls.Count,
            query, site);
        return recipeUrls;
    }

    private async Task<List<string>> ExtractRecipeUrls(string url, Dictionary<string, string> selectors)
    {
        // Extract the html from the search page
        var html = await SendGetRequestForHtml(url);

        if (string.IsNullOrEmpty(html))
        {
            _log.Warning("Failed to retrieve HTML content for URL: {url}", url);
            return [];
        }

        var browserContext = BrowsingContext.New(Configuration.Default);
        var htmlDoc = await browserContext.OpenAsync(req => req.Content(html));

        var searchResultSelector = selectors["search_results"].Replace("\\\"", "\"");

        var links = htmlDoc.QuerySelectorAll(searchResultSelector);
        return links.Select(link => link.GetAttribute("href"))
            .Where(urlAttribute => !string.IsNullOrEmpty(urlAttribute))
            .Distinct()
            .ToList()!;
    }

    private async Task<List<ScrapedRecipe>> ScrapeSiteForRecipesAsync(string site, List<string> recipeUrls,
        string? query)
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
                    var fullUrl = url switch
                    {
                        _ when url.StartsWith("https://") => url,
                        _ when url.StartsWith("//") => $"https:{url}",
                        _ => $"{_siteSelectors![site]["base_url"]}{url}"
                    };
                    
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

        string? imageUrl = null;
        try
        {
            imageUrl = ExtractText(htmlDoc, selectors["image"], "data-lazy-src") ??
                       ExtractText(htmlDoc, selectors["image"], "src") ??
                       ExtractText(htmlDoc, selectors["image"], "srcset")?.Split(" ")[0];
        }
        catch (Exception)
        {
            _log.Debug("No image found for {url}", url);
        }

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
            ImageUrl = imageUrl,
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

    private async Task<string> SendGetRequestForHtml(string url, CancellationToken ctsToken = default)
    {
        try
        {
            _log.Information("Running GET request for URL: {url}", url);

            // Create an HttpClientHandler with automatic decompression
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };

            using var client = new HttpClient(handler);

            // Create the request message
            var request = new HttpRequestMessage(HttpMethod.Get, url);

            // Add headers to mimic a real browser
            request.Headers.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/98.0.4758.102 Safari/537.36");
            request.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            request.Headers.Add("Accept-Language", "en-US,en;q=0.5");
            // Note: AutomaticDecompression handles Accept-Encoding

            // Send the request
            var response = await client.SendAsync(request, ctsToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(ctsToken);
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

    private async Task LoadSiteSelectors()
    {
        if (_siteSelectors != null && _siteTemplates != null)
            return;

        var selectorsData = await fileService.ReadFromFile<SiteSelectors>("Config", "site_selectors");
        _siteTemplates = selectorsData.Templates;
        _siteSelectors = new Dictionary<string, Dictionary<string, string>>();

        // Process sites and apply templates
        foreach (var (siteKey, siteConfig) in selectorsData.Sites)
        {
            if (siteConfig.TryGetValue("use_template", out var templateName))
            {
                if (!_siteTemplates.TryGetValue(templateName, out var templateConfig))
                {
                    throw new Exception($"Template {templateName} not found for site {siteKey}");
                }

                // Merge template and site config
                var mergedConfig = new Dictionary<string, string>(templateConfig);

                // Overwrite with site-specific values
                foreach (var kvp in siteConfig)
                {
                    mergedConfig[kvp.Key] = kvp.Value;
                }

                _siteSelectors[siteKey] = mergedConfig;
            }
            else
            {
                _siteSelectors[siteKey] = siteConfig;
            }
        }
    }
}

class SiteSelectors
{
    public Dictionary<string, Dictionary<string, string>> Sites { get; set; } = new();
    public Dictionary<string, Dictionary<string, string>> Templates { get; set; } = new();
}