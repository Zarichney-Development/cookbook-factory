using System.Text.Json;
using Cookbook.Factory.Models;

namespace Cookbook.Factory.Prompts;

public class AnalyzeRecipePrompt : PromptBase
{
    public override string Name => "Analyze Recipe";
    public override string Description => "Analyze the quality and relevancy of a recipe";
    public override string Model => "gpt-4o-mini";
    public override string SystemPrompt =>
        """
        # Recipe Analysis System Prompt

        You are an AI assistant specialized in recipe analysis and quality assurance. Your task is to thoroughly review a curated recipe and ensure it meets the user's specifications as outlined in their cookbook order. Follow these steps:

        1. Carefully review the provided recipe and cookbook order details.

        2. Verify that the recipe adheres to:
           - Dietary restrictions and allergies
           - Skill level appropriateness
           - Cooking goals and preferences
           - Cultural exploration aims
           - Cookbook theme and style

        3. Assess the recipe for:
           - Completeness and clarity of instructions
           - Appropriate serving size
           - Realistic prep and cook times
           - Inclusion of required elements (e.g., cultural context, educational content, practical tips)

        4. Determine if the recipe passes the quality assurance check:
           - If it passes, provide a brief justification highlighting its strengths.
           - If it doesn't pass, offer specific critiques and suggestions for improvement.

        5. Call the AnalyzeRecipe function with your assessment, including a boolean 'passes' flag and your detailed feedback.
        Wait for a new recipe to be provided via tool output, then analyze it again.
        Repeat this process until a recipe passes your quality check.
        """;

    public string GetUserPrompt(SynthesizedRecipe curatedRecipe, CookbookOrder order, string recipeName) => 
        $"""
         <requested-recipe-name>{recipeName}</requested-recipe-name>
         <recipe-data>
         ```json
         {JsonSerializer.Serialize(curatedRecipe)}
         ```
         </recipe-data>
         <cookbook-order>
         ```md
         {order.ToMarkdown()}
         ```
         </cookbook-order>
         <goal>Analyze the recipe and provide feedback on its quality and relevancy</goal>
         """;

    public override FunctionDefinition GetFunction() => new()
    {
        Name = "AnalyzeRecipe",
        Description = "Analyze the quality and relevancy of a recipe",
        Parameters = JsonSerializer.Serialize(new
        {
            type = "object",
            properties = new
            {
                passes = new { type = "boolean" },
                feedback = new { type = "string" }
            },
            required = new[] { "passes", "feedback" }
        })
    };
}