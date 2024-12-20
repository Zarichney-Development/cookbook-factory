using System.Collections.Concurrent;
using System.Text.Json;
using AutoMapper;
using OpenAI.Assistants;
using OpenAI.Chat;
using Serilog;
using Zarichney.Config;
using Zarichney.Cookbook.Models;
using Zarichney.Cookbook.Prompts;
using Zarichney.Services;
using ILogger = Serilog.ILogger;

namespace Zarichney.Cookbook.Services;

public class RecipeConfig : IConfig
{
  public int RecipesToReturnPerRetrieval { get; init; } = 5;
  public int AcceptableScoreThreshold { get; init; } = 80;
  public int QualityScoreThreshold { get; init; } = 80;
  public int MaxNewRecipeNameAttempts { get; init; } = 6;
  public int MaxParallelTasks { get; init; } = 5;
  public string OutputDirectory { get; init; } = "Recipes";
}

public class RecipeService(
  IRecipeRepository recipeRepository,
  RecipeConfig config,
  ILlmService llmService,
  WebScraperService webscraper,
  IMapper mapper,
  RankRecipePrompt rankRecipePrompt,
  SynthesizeRecipePrompt synthesizeRecipePrompt,
  AnalyzeRecipePrompt analyzeRecipePrompt,
  ProcessOrderPrompt processOrderPrompt
)
{
  private readonly ILogger _log = Log.ForContext<RecipeService>();

  public async Task<List<Recipe>> GetRecipes(string requestedRecipeName, CookbookOrder? cookbookOrder = null,
    int? acceptableScore = null)
  {
    acceptableScore ??= config.AcceptableScoreThreshold;

    var recipes = await GetRecipes(requestedRecipeName, true, acceptableScore);

    var previousAttempts = new List<string>();
    var searchQuery = requestedRecipeName;

    while (recipes.Count == 0)
    {
      previousAttempts.Add(searchQuery);

      var attempt = previousAttempts.Count + 1;
      _log.Warning("[{RecipeName}] - No recipes found using '{SearchQuery}'", requestedRecipeName, searchQuery);

      if (attempt > config.MaxNewRecipeNameAttempts)
      {
        _log.Error("[{RecipeName}] - Aborting recipe searching after '{attempt}' attempts",
          requestedRecipeName, attempt - 1);
        throw new NoRecipeException(previousAttempts);
      }

      // Lower the bar of acceptable for every attempt
      acceptableScore -= 5;

      // With a lower bar, first attempt to check local sources before performing a new web scrape
      recipes = await GetRecipes(searchQuery, false, acceptableScore, requestedRecipeName);
      if (recipes.Count > 0)
      {
        break;
      }

      // Request for new query to scrap with
      searchQuery = await GetSearchQueryForRecipe(requestedRecipeName, previousAttempts, cookbookOrder);

      _log.Information(
        "[{RecipeName}] - Attempting an alternative search query: '{NewRecipeName}'. Acceptable score of {AcceptableScore}.",
        requestedRecipeName, searchQuery, acceptableScore);

      recipes = await GetRecipes(searchQuery, true, acceptableScore, requestedRecipeName);
    }

    return recipes;
  }

  private async Task<string> GetSearchQueryForRecipe(string recipeName, List<string> previousAttempts,
    CookbookOrder? cookbookOrder = null)
  {
    var primaryApproach = previousAttempts.Count < Math.Ceiling(config.MaxNewRecipeNameAttempts / 2.0);

    var messages = new List<ChatMessage>();

    // For the first half of the attempts, request for alternatives
    if (primaryApproach)
    {
      messages.Add(new SystemChatMessage(processOrderPrompt.SystemPrompt));

      if (cookbookOrder != null)
      {
        messages.AddRange([
            new UserChatMessage(processOrderPrompt.GetUserPrompt(cookbookOrder)),
            new SystemChatMessage(string.Join(", ", cookbookOrder.RecipeList))
          ]
        );
      }

      messages.Add(new UserChatMessage(
        $"""
         Thank you. The recipe name "{recipeName}" didn't yield any search results, likely due to its uniqueness or obscurity.
         Please provide a wider search query to retrieve similar recipes. With each failed attempt, generalize the search query.
         Only respond with the new recipe name.
         """));

      foreach (var previousAttempt in previousAttempts.Where(previousAttempt => previousAttempt != recipeName))
      {
        messages.AddRange([
          new SystemChatMessage(previousAttempt),
          new UserChatMessage(
            $"Sorry, that search also returned no matches. Suggest a more generalized query for similar recipes, staying as true as possible to the original recipe '{recipeName}'.")
        ]);
      }
    }
    else
    {
      // For the second half of attempts, aggressively generalize the search query
      messages.AddRange([
        new SystemChatMessage(
          """
          <SystemPrompt>
              <Context>Online Recipe Searching</Context>
              <Goal>Your task is to provide an ideal search query that aims to returns search results yielding recipes that forms the basis of the user's requested recipe.</Goal>
              <Input>A unique recipe name that does not return search results.</Input>
              <Output>Respond with only the new search query and nothing else.</Output>
              <Examples>
                  <Example>
                      <Input>Pan-Seared Partridge with Herb Infusion</Input>
                      <Output>Partridge</Output>
                  </Example>
                  <Example>
                      <Input>Luigi’s Veggie Power-Up Pizza</Input>
                      <Output>Vegetable Pizza</Output>
                  </Example>
                  <Example>
                      <Input>Herb-Crusted Venison with Seasonal Vegetables</Input>
                      <Output>Venison</Output>
                  </Example>
              </Examples>
              <Rules>
                  <Rule>Omit Previous Attempts</Rule>
                  <Rule>The more attempts made, the more generalized the search query should be</Rule>
                  <Rule>As part of your search query response suggestion, do not append 'Recipe' or 'Recipes'.</Rule>
              </Rules>
          </SystemPrompt>
          """
        ),
        new UserChatMessage(
          $"""
           Recipe: {recipeName}
           Previous Attempts: {string.Join(", ", previousAttempts)}
           """
        )
      ]);
    }

    var result = await llmService.GetCompletionContent(messages);

    return result.Trim('"').Trim();
  }

  public async Task<List<Recipe>> GetRecipes(string query, bool scrape, int? acceptableScore = null,
    string? requestedRecipeName = null)
  {
    acceptableScore ??= config.AcceptableScoreThreshold;
    var recipesNeeded = config.RecipesToReturnPerRetrieval;

    // First, attempt to retrieve via local source of JSON files
    var recipes = await recipeRepository.SearchRecipes(query);
    if (recipes.Count != 0)
    {
      _log.Information("Retrieved {count} cached recipes for query '{query}'.", recipes.Count, query);
    }

    await RankUnrankedRecipesAsync(recipes, requestedRecipeName ?? query, acceptableScore.Value);

    // Check if additional recipes should be web-scraped
    if (scrape && recipes.Count(r =>
          (r.Relevancy.TryGetValue(query, out var value) ? value.Score : 0) >= acceptableScore) < recipesNeeded)
    {
      // Proceed with web scraping
      var scrapedRecipes = await webscraper.ScrapeForRecipesAsync(query);

      // Exclude any recipes already in the repository
      var newRecipes = scrapedRecipes.Where(r => !recipeRepository.ContainsRecipe(r.Id!));

      // Map scraped recipes to Recipe objects and add to the list
      recipes.AddRange(mapper.Map<List<Recipe>>(newRecipes));

      // Rank any new unranked recipes
      await RankUnrankedRecipesAsync(recipes, requestedRecipeName ?? query, acceptableScore.Value);
    }

    // Save recipes to the repository
    if (recipes.Count != 0)
    {
      await recipeRepository.AddUpdateRecipes(recipes);
    }

    // Return the top recipes
    return recipes
      .Where(r => r.Relevancy.TryGetValue(query, out var value) && value.Score >= acceptableScore)
      .Take(config.RecipesToReturnPerRetrieval)
      .ToList();
  }

  public async Task<List<Recipe>> RankUnrankedRecipesAsync(IEnumerable<ScrapedRecipe> recipes, string query)
    => await RankUnrankedRecipesAsync(mapper.Map<List<Recipe>>(recipes), query, config.AcceptableScoreThreshold);

  private async Task<List<Recipe>> RankUnrankedRecipesAsync(List<Recipe> recipes, string query, int acceptableScore)
  {
    var unrankedRecipes = recipes.Where(r => !r.Relevancy.ContainsKey(query)).ToList();
    if (unrankedRecipes.Count != 0)
    {
      await RankRecipesAsync(unrankedRecipes, query, acceptableScore);
      // At this point, unrankedRecipes have their Relevancy updated.
      // No need to replace items in the list since the recipes are reference types
      // and their properties have been updated in place.
    }

    // Now sort the recipes list based on the updated relevancy scores.
    recipes.Sort((r1, r2) =>
    {
      var score1 = r1.Relevancy.TryGetValue(query, out var value) ? value.Score : 0;
      var score2 = r2.Relevancy.TryGetValue(query, out var value1) ? value1.Score : 0;
      return score2.CompareTo(score1);
    });

    return recipes;
  }

  private async Task RankRecipesAsync(List<Recipe> recipes, string query, int? acceptableScore = null)
  {
    acceptableScore ??= config.AcceptableScoreThreshold;

    var rankedRecipes = new ConcurrentBag<Recipe>();
    var cts = new CancellationTokenSource();

    try
    {
      await Parallel.ForEachAsync(recipes, new ParallelOptions
        {
          MaxDegreeOfParallelism = config.MaxParallelTasks,
          CancellationToken = cts.Token
        },
        async (recipe, ct) =>
        {
          // Check if cancellation has been requested
          if (ct.IsCancellationRequested)
            return;

          try
          {
            if (!recipe.Relevancy.TryGetValue(query, out var value) || value.Score < acceptableScore)
            {
              // Evaluate relevancy if not already
              value = await RankRecipe(recipe, query);
              recipe.Relevancy[query] = value;
            }

            if (value.Score >= acceptableScore)
            {
              rankedRecipes.Add(recipe);

              // Check if we have collected enough recipes
              if (rankedRecipes.Count >= config.RecipesToReturnPerRetrieval)
              {
                await cts.CancelAsync(); // Cancel remaining tasks
              }
            }
          }
          catch (OperationCanceledException)
          {
            // Task was cancelled, just return
          }
          catch (Exception ex)
          {
            _log.Error(ex, $"Error ranking recipe: {recipe.Id}");
            throw;
          }
        });
    }
    catch (OperationCanceledException)
    {
      // The operation was cancelled because enough recipes were found
    }
    finally
    {
      cts.Dispose();
    }
  }

  private async Task<RelevancyResult> RankRecipe(Recipe recipe, string? query)
  {
    var result = await llmService.CallFunction<RelevancyResult>(
      rankRecipePrompt.SystemPrompt,
      rankRecipePrompt.GetUserPrompt(recipe, query),
      rankRecipePrompt.GetFunction()
    );

    result.Query = query;

    _log.Information("Relevancy filter result: {@Result}", result);

    return result;
  }

  public async Task<(SynthesizedRecipe, List<SynthesizedRecipe>)> SynthesizeRecipe(List<Recipe> recipes,
    CookbookOrder order, string recipeName)
  {
    SynthesizedRecipe synthesizedRecipe;
    List<SynthesizedRecipe> rejectedRecipes = [];

    var synthesizingAssistantId = await llmService.CreateAssistant(synthesizeRecipePrompt);
    var analyzeAssistantId = await llmService.CreateAssistant(analyzeRecipePrompt);

    var synthesizingThreadId = await llmService.CreateThread();
    var analyzeThreadId = await llmService.CreateThread();

    string synthesizeRunId = null!;
    string analysisRunId = null!;

    _log.Information("[{Recipe}] Synthesizing using recipes: {@Recipes}", recipeName, recipes);

    try
    {
      var synthesizePrompt = synthesizeRecipePrompt.GetUserPrompt(recipeName, recipes, order);

      await llmService.CreateMessage(synthesizingThreadId, synthesizePrompt);

      synthesizeRunId = await llmService.CreateRun(synthesizingThreadId, synthesizingAssistantId);

      var count = 0;
      string? analysisToolCallId = null;

      while (true)
      {
        count++;

        (var synthesizeToolCallId, synthesizedRecipe) =
          await ProcessSynthesisRun(synthesizingThreadId, synthesizeRunId);

        _log.Information("[{Recipe} - Run {count}] Synthesized recipe: {@SynthesizedRecipe}", recipeName, count,
          synthesizedRecipe);

        if (count == 1)
        {
          var analysisPrompt = analyzeRecipePrompt.GetUserPrompt(synthesizedRecipe, order, recipeName);
          await llmService.CreateMessage(analyzeThreadId, analysisPrompt);

          analysisRunId = await llmService.CreateRun(analyzeThreadId, analyzeAssistantId);
        }
        else
        {
          await ProvideRevisedRecipe(analyzeThreadId, analysisRunId, analysisToolCallId!, synthesizedRecipe);
        }

        (analysisToolCallId, var analysisResult) = await ProcessAnalysisRun(analyzeThreadId, analysisRunId);

        _log.Information("[{Recipe} - Run {count}] Analysis result: {@Analysis}",
          recipeName, count, analysisResult);

        synthesizedRecipe.AddAnalysisResult(analysisResult);

        if (analysisResult.QualityScore >= config.QualityScoreThreshold)
        {
          break;
        }

        // In case LLM fails to provide theses
        if (string.IsNullOrWhiteSpace(analysisResult.Suggestions))
        {
          analysisResult.Suggestions =
            "Please pay attention to what is desired from the cookbook order and synthesize another one.";
        }

        if (string.IsNullOrEmpty(analysisResult.Analysis))
        {
          analysisResult.Analysis = "The recipe is not suitable enough for the cookbook order.";
        }

        rejectedRecipes.Add(synthesizedRecipe);

        await ProvideAnalysisFeedback(synthesizingThreadId, synthesizeRunId, synthesizeToolCallId,
          analysisResult);
      }

      _log.Information("[{Recipe}] Synthesized: {@Recipes}", recipeName, synthesizedRecipe);
    }
    catch (Exception ex)
    {
      _log.Error(ex,
        "[{Recipe}] Error synthesizing recipe: {Message}. Synthesizer Assistant: {SynthesizerAssistant}, Thread: {SynthesizingThread}, Run: {SynthesizingRun}. Analyzer Assistant: {AnalyzerAssistant}, Thread: {AnalyzeThread}, Run: {AnalyzeRun}",
        recipeName, ex.Message, synthesizingAssistantId, synthesizingThreadId, synthesizeRunId,
        analyzeAssistantId, analyzeThreadId, analysisRunId);
      throw;
    }
    finally
    {
      await llmService.CancelRun(analyzeThreadId, analysisRunId);
      await llmService.CancelRun(synthesizingThreadId, synthesizeRunId);
      await llmService.DeleteAssistant(synthesizingAssistantId);
      await llmService.DeleteAssistant(analyzeAssistantId);
      await llmService.DeleteThread(synthesizingThreadId);
      await llmService.DeleteThread(analyzeThreadId);
    }

    return (synthesizedRecipe, rejectedRecipes);
  }

  private async Task<(string, SynthesizedRecipe)> ProcessSynthesisRun(string threadId, string runId)
  {
    while (true)
    {
      var (isComplete, status) = await llmService.GetRun(threadId, runId);

      if (isComplete)
      {
        throw new Exception("Synthesis run completed without producing a recipe");
      }

      if (status == RunStatus.RequiresAction)
      {
        var (toolCallId, result) = await llmService.GetRunAction<SynthesizedRecipe>(threadId, runId,
          synthesizeRecipePrompt.GetFunction().Name);

        result.InspiredBy ??= [];

        return (toolCallId, result);
      }

      await Task.Delay(TimeSpan.FromSeconds(1));
    }
  }

  private async Task<(string, RecipeAnalysis)> ProcessAnalysisRun(string threadId, string runId)
  {
    while (true)
    {
      var (isComplete, status) = await llmService.GetRun(threadId, runId);

      if (isComplete)
      {
        throw new Exception("Analysis run completed without producing a result");
      }

      if (status == RunStatus.RequiresAction)
      {
        return await llmService.GetRunAction<RecipeAnalysis>(threadId, runId,
          analyzeRecipePrompt.GetFunction().Name);
      }

      await Task.Delay(TimeSpan.FromSeconds(1));
    }
  }

  private async Task ProvideRevisedRecipe(string threadId, string runId, string toolCallId, SynthesizedRecipe recipe)
  {
    await llmService.SubmitToolOutputToRun(
      threadId,
      runId,
      toolCallId,
      JsonSerializer.Serialize(recipe)
    );
  }

  private async Task ProvideAnalysisFeedback(string threadId, string runId, string toolCallId,
    RecipeAnalysis analysis)
  {
    var toolOutput =
      $"""
         A new revision is required. Refer to the QA analysis:
         ```json
         {JsonSerializer.Serialize(analysis)}
         ```
         """.Trim();

    await llmService.SubmitToolOutputToRun(
      threadId,
      runId,
      toolCallId,
      toolOutput
    );
  }
}

public class NoRecipeException(List<string> previousAttempts) : Exception("No recipes found")
{
  public List<string> PreviousAttempts { get; } = previousAttempts;
}