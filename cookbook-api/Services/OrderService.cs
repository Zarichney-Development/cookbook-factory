using System.Collections.Concurrent;
using Cookbook.Factory.Models;
using Cookbook.Factory.Prompts;
using Polly;
using Polly.Retry;
using Serilog;
using ILogger = Serilog.ILogger;

namespace Cookbook.Factory.Services;

public class OrderConfig : IConfig
{
    public int MaxParallelTasks { get; init; } = 5;
    public int MaxSampleRecipes { get; init; } = 3;
    public string OutputDirectory { get; init; } = "Orders";
}

public class OrderService(
    OrderConfig config,
    ILlmService llmService,
    FileService fileService,
    RecipeService recipeService,
    ProcessOrderPrompt processOrderPrompt,
    PdfCompiler pdfCompiler,
    IEmailService emailService,
    LlmConfig llmConfig
)
{
    private readonly ILogger _log = Log.ForContext<OrderService>();

    private readonly AsyncRetryPolicy _retryPolicy = Policy
        .Handle<Exception>()
        .WaitAndRetryAsync(
            retryCount: llmConfig.RetryAttempts,
            sleepDurationProvider: _ => TimeSpan.FromSeconds(1),
            onRetry: (exception, _, retryCount, context) =>
            {
                Log.Warning(exception, "Attempt {retryCount}: Retrying due to {exception}. Retry Context: {@Context}",
                    retryCount, exception.Message, context);
            }
        );

    public async Task<CookbookOrder> ProcessOrderSubmission(CookbookOrderSubmission submission)
    {
        var result = await llmService.CallFunction<RecipeProposalResult>(
            processOrderPrompt.SystemPrompt,
            processOrderPrompt.GetUserPrompt(submission),
            processOrderPrompt.GetFunction()
        );

        var order = new CookbookOrder(submission, result.Recipes)
        {
            Email = submission.Email,
            CookbookContent = submission.CookbookContent,
            CookbookDetails = submission.CookbookDetails,
            UserDetails = submission.UserDetails
        };

        _log.Information("Order intake: {@Order}", order);

        CreateOrderDirectory(order);

        return order;
    }

    public async Task<CookbookOrder> GetOrder(string orderId)
        => await fileService.ReadFromFile<CookbookOrder>(Path.Combine(config.OutputDirectory, orderId), "Order");

    public async Task<CookbookOrder> GenerateCookbookAsync(CookbookOrder order, bool isSample = false)
    {
        var completedRecipes = new ConcurrentQueue<SynthesizedRecipe>();

        var maxParralelTasks = config.MaxParallelTasks;
        if (isSample)
        {
            maxParralelTasks = Math.Min(maxParralelTasks, config.MaxSampleRecipes);
        }

        var semaphore = new SemaphoreSlim(maxParralelTasks);
        var processingTasks = new List<Task>();

        foreach (var recipeName in order.RecipeList)
        {
            if (isSample && completedRecipes.Count >= config.MaxSampleRecipes)
            {
                _log.Information("Sample size reached, stopping processing");
                break;
            }

            processingTasks.Add(Task.Run(async () =>
            {
                try
                {
                    await ProcessRecipe(recipeName);
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "Unhandled exception in ProcessRecipe for {RecipeName}", recipeName);
                }
            }));

            if (processingTasks.Count >= config.MaxParallelTasks)
            {
                await Task.WhenAny(processingTasks);
                processingTasks.RemoveAll(t => t.IsCompleted);
            }
        }

        try
        {
            await Task.WhenAll(processingTasks);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Unhandled exception in one of the processing tasks.");
        }

        order.SynthesizedRecipes = completedRecipes.ToList();

        UpdateOrderFile(order);

        return order;

        async Task ProcessRecipe(string recipeName)
        {
            await semaphore.WaitAsync();
            try
            {
                if (isSample && completedRecipes.Count >= config.MaxSampleRecipes)
                {
                    _log.Information("Sample size reached, stopping processing");
                    return;
                }

                List<Recipe> recipes;
                try
                {
                    recipes = await recipeService.GetRecipes(recipeName, order);
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "Omitting recipe {RecipeName} due to no recipes found", recipeName);
                    return; // is this the right way to exit ProcessRecipe? 
                }

                var (result, rejects) = await recipeService.SynthesizeRecipe(recipes, order, recipeName);

                var count = 0;
                foreach (var recipe in rejects)
                {
                    WriteRejectToOrderDir(order.OrderId, $"{++count}. {recipeName}", recipe);
                }

                // Add image to recipe
                result.ImageUrls = GetImageUrls(result, recipes);

                WriteRecipeToOrderDir(order.OrderId, recipeName, result);
                completedRecipes.Enqueue(result);
            }
            finally
            {
                semaphore.Release();
            }
        }
    }

    private List<string> GetImageUrls(SynthesizedRecipe result, List<Recipe> recipes)
    {
        var relevantRecipes = recipes.Where(r => result.InspiredBy?.Contains(r.RecipeUrl!) ?? false).ToList();

        if (relevantRecipes.Count == 0)
        {
            // Fallback to using any of the provided recipes
            relevantRecipes = recipes;
        }

        return relevantRecipes
            .Where(r => !string.IsNullOrWhiteSpace(r.ImageUrl))
            .Select(r => r.ImageUrl!)
            .ToList();
    }

    private void CreateOrderDirectory(CookbookOrder order)
        => UpdateOrderFile(order);

    private void UpdateOrderFile(CookbookOrder order)
        => fileService.WriteToFile(
            Path.Combine(config.OutputDirectory, order.OrderId),
            "Order",
            order
        );

    private void WriteRejectToOrderDir(string orderId, string recipeName, SynthesizedRecipe recipe)
        => fileService.WriteToFile(
            Path.Combine(config.OutputDirectory, orderId, "recipes", "rejects"),
            recipeName,
            recipe
        );

    private void WriteRecipeToOrderDir(string orderId, string recipeName, SynthesizedRecipe recipe)
        => fileService.WriteToFile(
            Path.Combine(config.OutputDirectory, orderId, "recipes"),
            recipeName,
            recipe
        );

    public async Task CompilePdf(CookbookOrder order)
    {
        if (!(order.SynthesizedRecipes?.Count > 0))
        {
            throw new Exception("Cannot assemble pdf as this order contains no recipes");
        }

        foreach (var recipe in order.SynthesizedRecipes)
        {
            fileService.WriteToFile(
                Path.Combine(config.OutputDirectory, order.OrderId, "recipes"),
                recipe.Title,
                recipe.ToMarkdown(),
                "md"
            );
        }

        var pdf = await pdfCompiler.CompileCookbook(order);

        _log.Information("Cookbook compiled for order {OrderId}. Writing to disk", order.OrderId);

        fileService.WriteToFile(
            Path.Combine(config.OutputDirectory, order.OrderId),
            "Cookbook",
            pdf,
            "pdf"
        );
    }

    public async Task EmailCookbook(string orderId)
    {
        var emailTitle = "Your Cookbook is Ready!";
        var templateData = new Dictionary<string, object>
        {
            { "title", emailTitle },
            { "company_name", "Zarichney Development" },
            { "current_year", DateTime.Now.Year },
            { "unsubscribe_link", "https://zarichney.com/unsubscribe" },
        };

        await _retryPolicy.ExecuteAsync(async () =>
        {
            var order = await GetOrder(orderId);

            _log.Information("Retrieved order {OrderId} for email", orderId);

            var pdf = await fileService.ReadFromFile<byte[]>(Path.Combine(config.OutputDirectory, orderId), "Cookbook",
                "pdf");

            _log.Information("Emailing cookbook to {Email}", order.Email);

            await emailService.SendEmail(
                order.Email,
                emailTitle,
                "cookbook-ready",
                templateData,
                pdf
            );
        });
    }
}