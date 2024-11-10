using System.Text.Json;
using Cookbook.Factory.Models;
using Cookbook.Factory.Services;

namespace Cookbook.Factory.Prompts;

public class RankRecipePrompt : PromptBase
{
    public override string Name => "Recipe Assessor";
    public override string Description => "Assess the relevancy of a recipe based on a given query";

    public override string SystemPrompt
        => """
           You are a specialist in assessing the relevancy of a recipe given a query.
           Your task is to identify whether the given recipe contains data related to an actual recipe.
           Your goal is to provide a relevancy score of 0 if it's not a recipe and a score from 1 to 100 on how relevant a recipe is to the given query.";
           """;

    public override string Model => LlmModels.Gpt4Omini;

    public string GetUserPrompt(Recipe recipe, string query) =>
        $"Query: '{query}'\n\nRecipe data:\n{JsonSerializer.Serialize(recipe)}";

    public override FunctionDefinition GetFunction() => new()
    {
        Name = "RankRecipe",
        Description = "Assess the relevancy of a recipe based on a given query",
        Parameters = JsonSerializer.Serialize(new
        {
            type = "object",
            properties = new
            {
                score = new
                {
                    type = "integer",
                    description =
                        "A score of 0 if this is not a recipe or a score from 1 to 100 indicating how relevant the recipe is to the given query"
                },
                reasoning = new
                {
                    type = "string",
                    description = "A brief explanation of the relevancy decision"
                },
            },
            required = new[] { "score", "reasoning" }
        })
    };
}

public class RelevancyResult
{
    public required string Query { get; set; }
    public int Score { get; set; }
    public string? Reasoning { get; set; }
}