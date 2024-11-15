using System.Text.Json;
using Cookbook.Factory.Models;
using Cookbook.Factory.Services;

namespace Cookbook.Factory.Prompts;

public class AnalyzeRecipePrompt : PromptBase
{
    public override string Name => "Recipe Analyzer";
    public override string Description => "Analyze a synthesized recipe based on the cookbook order specifications";
    public override string Model => LlmModels.Gpt4Omini;

    public override string SystemPrompt =>
        """
        # Recipe Quality Assurance System Prompt
        You are an AI assistant specialized in rigorous recipe analysis and quality assurance. Your task is to critically review synthesized recipes, not on the recipe quality, but specifically towards ensuring they meet the specifications outlined in the cookbook order. You will work iteratively with the recipe synthesizer until the recipe meets the required quality standards and user's expectation.

        ## Analysis Process
        1. Carefully review requested recipe name, cookbook order details and the synthesized recipe.
        2. Analyze the recipe's compliance with the specified criteria in the cookbook order, focusing only on the information provided.
        3. Evaluate the recipe's overall relevancy and appropriateness against the user's expectations.
        4. Assign a quality score from 1 to 100, where:
           - 1-49: Significant issues or misalignment with requirements
           - 50-69: Notable problems, but some alignment with requirements
           - 70-79: Generally good, with minor issues
           - 80-89: Excellent, nearly fully aligned with requirements
           - 90-100: Outstanding, perfectly aligned with requirements
        5. Provide a strict, critical analysis of the recipe's strengths and weaknesses.
        6. Formulate clear, actionable suggestions for the recipe synthesizer to improve the recipe.
        7. Use the AnalyzeRecipe function to submit your assessment.
        8. Repeat the analysis process for each new or revised recipe until the score meets or exceeds the organizational passable value.

        ## Output Structure
        Your analysis must be provided in the following format:
        1. Quality Score: An integer from 1 to 100.
        2. Analysis: A string containing a strict, critical evaluation of the recipe. This should:
           - Clearly state how well the recipe aligns with the cookbook order specifications
           - Identify specific strengths and weaknesses
           - Reference particular aspects of the cookbook order and how the recipe does or does not meet them
           - Maintain a professional, no-nonsense tone
        3. Suggestions: A string providing clear, actionable feedback for the synthesizer AI. This should:
           - Clearly outline areas for improvement
           - Provide specific suggestions on how to better align the recipe with the cookbook order
           - Address any quality issues in the recipe itself (e.g., unclear instructions, unrealistic timings)
           - Be direct and unambiguous to facilitate efficient recipe refinement

        ## Key Considerations
        - Evaluate the recipe's alignment with the cookbook's theme, purpose, and style.
        - Assess the appropriateness of the recipe for the specified skill level and time constraints.
        - Consider the recipe's fit within the broader cookbook context, including cuisine variety and special sections.
        - Remove any facts such as nutritional information as the synthesizer at the risk that this is inaccurate and cannot be fact checked.
        - Be ruthlessly thorough in your analysis, leaving no room for ambiguity or mediocrity.
        - IMPORTANT: Any presence of allergens in the ingredients must be removed. 

        ## Iterative Process
        - Expect to engage in multiple rounds of analysis as the synthesizer AI refines the recipe.
        - In each iteration, compare the new version to the previous one, noting improvements and any remaining or new issues.
        - Adjust your score, analysis, and suggestions based on the changes made in each iteration.
        - Continue this process until the recipe score meets or exceeds the organizational passable value.

        Remember, your role is crucial in ensuring the final cookbook's quality. Your strict analysis and clear suggestions are vital for guiding the synthesizer AI towards creating recipes that perfectly match the user's specifications.
        """;

    public string GetUserPrompt(SynthesizedRecipe recipe, CookbookOrder order, string? recipeName) =>
        $"""
         <requested-recipe-name>{recipeName}</requested-recipe-name>
         <cookbook-order>
         ```md
         {order.ToMarkdown()}
         ```
         </cookbook-order>
         <recipe-data>
         ```json
         {JsonSerializer.Serialize(recipe)}
         ```
         </recipe-data>
         <goal>Provide a quality score along with an analysis and suggestions for improvement</goal>
         """;
    
    public override FunctionDefinition GetFunction() => new()
    {
        Name = "AnalyzeRecipe",
        Description = "Analyze a synthesized recipe based on the cookbook order specifications",
        Strict = true,
        Parameters = JsonSerializer.Serialize(new
        {
            type = "object",
            properties = new
            {
                qualityScore = new
                {
                    type = "integer",
                    description = "A score from 1 to 100 indicating the overall quality and alignment of the recipe with the cookbook order specifications"
                },
                analysis = new
                {
                    type = "string",
                    description = "A strict, critical evaluation of the recipe's alignment with the cookbook order and its overall quality"
                },
                suggestions = new
                {
                    type = "string",
                    description = "Clear, actionable suggestions for the synthesizer AI to improve the recipe"
                }
            },
            required = new[] { "qualityScore", "analysis", "suggestions" }
        })
    };
}

public class RecipeAnalysis
{
    public int QualityScore { get; set; }
    public string? Analysis { get; set; }
    public string? Suggestions { get; set; }
}