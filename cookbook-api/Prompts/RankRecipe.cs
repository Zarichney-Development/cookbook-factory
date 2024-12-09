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
           You are a specialist in assessing the relevancy of a recipe against the search query.
           Your task is to identify the degree to how relevant is a given recipe against the desired recipe query name.
           Assign a relevancy score from 0 to 100, where:
              - 0: The recipe data is for something else.
              - 1-29: The recipe data is not relevant.
              - 30-49: Somewhat relevant to the query.
              - 50-69: This recipe is similar enough.
              - 70-79: Relevant.
              - 80-99: This recipe is expected to be a top search results.
              - 100: Perfect match, exactly the same.
           Along with the score, provide a brief and concise justification of your decision.
           """;

    public override string Model => LlmModels.Gpt4Omini;

    public string GetUserPrompt(Recipe recipe, string? query) =>
        $"""
         Query: '{query}'

         Recipe data:
         ```json
         {JsonSerializer.Serialize(recipe)}
         ```
         """;

    public override FunctionDefinition GetFunction() => new()
    {
        Name = "RankRecipe",
        Description = "Assess the relevancy of a recipe based on a given query",
        Strict = true,
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
                    description = "A concise explanation of the relevancy decision"
                },
            },
            required = new[] { "score", "reasoning" }
        })
    };
}

public class RelevancyResult
{
    public string? Query { get; set; }
    public int Score { get; set; }
    public string? Reasoning { get; set; }
}