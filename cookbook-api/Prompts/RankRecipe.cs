using System.Text.Json;
using Cookbook.Factory.Models;

namespace Cookbook.Factory.Prompts;

public class RankRecipePrompt : PromptBase
{
    public override string Name => "Recipe Assessor";
    public override string Description => "Assess the relevancy of a recipe based on a given query";
    public override string SystemPrompt => "You are a specialist in assessing the relevancy of a recipe. Your task is to provide a relevancy score from 0 to 100 on how relevant a recipe is to a given query.";
    public override string Model => "gpt-4o-mini";

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
                relevancyScore = new
                {
                    type = "integer",
                    description = "A score from 0 to 100 indicating how relevant the recipe is to the given query"
                },
                relevancyReasoning = new
                {
                    type = "string",
                    description = "A brief explanation of the relevancy decision"
                },
            },
            required = new[] { "relevancyScore", "relevancyReasoning" }
        })
    };
}