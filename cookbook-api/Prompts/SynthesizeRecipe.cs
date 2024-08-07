using System.Collections.Generic;
using System.Text.Json;
using Cookbook.Factory.Models;
using Cookbook.Factory.Services;

namespace Cookbook.Factory.Prompts;

public class SynthesizeRecipePrompt : PromptBase
{
    public override string Name => "Recipe Maker";
    public override string Description => "Synthesize a new recipe based on existing recipes and user preferences";
    public override string Model => LlmModels.Gpt4Omini;
    public override string SystemPrompt =>
        """
        # Recipe Curation System Prompt

        **Role:** AI assistant for personalized recipe creation.

        **Steps:**
        1. **Analyze Cookbook Order:**
           - Review Cookbook Content, Details, and User Details.
           - Focus on dietary restrictions, allergies, skill level, and cooking goals.
        2. **Evaluate Provided Recipes:**
           - Analyze recipes from various sources for inspiration.
        3. **Create New Recipe:**
           - Blend elements from relevant recipes.
           - Ensure it meets dietary needs, preferences, skill level, and time constraints.
           - Align with the cookbook's theme and cultural goals.
        4. **Customize Recipe:**
           - Modify ingredients and techniques for dietary restrictions and allergies.
           - Adjust serving size.
           - Include alternatives or substitutions.
        5. **Enhance Recipe:**
           - Add cultural context or storytelling.
           - Incorporate educational content.
           - Include meal prep tips or leftover ideas.
        6. **Format Output:**
           - Use provided Recipe class structure.
           - Fill out all fields.
           - Include customizations, cultural context, or educational content in Notes.
        7. **Provide Attribution:**
           - List "Inspired by" URLs from original recipes.
           - Only include those that contributed towards the synthesized recipe.
        8. **Review and Refine:**
           - The synthesized recipe will be assess for quality assurance.
           - Provide a new revision when provided suggestions for improvement.

        **Goal:** Tailor recipes to user needs and preferences while maintaining original integrity and cookbook theme.
        """;

    public string GetUserPrompt(List<Recipe> recipes, CookbookOrder order) => 
        $"""
         # Recipe data:
         ```json
         {JsonSerializer.Serialize(recipes)}
         ```

         # Cookbook Order:
         {order.ToMarkdown()}

         Please synthesize a personalized recipe.
         """;

    public override FunctionDefinition GetFunction() => new()
    {
       Name = "SynthesizeRecipe",
       Description = "Refer to existing recipes and synthesize a new one based on user expectations",
       Parameters = JsonSerializer.Serialize(new
       {
          type = "object",
          properties = new
          {
             title = new { type = "string" },
             description = new { type = "string" },
             servings = new { type = "string" },
             prepTime = new { type = "string" },
             cookTime = new { type = "string" },
             totalTime = new { type = "string" },
             ingredients = new { type = "array", items = new { type = "string" } },
             directions = new { type = "array", items = new { type = "string" } },
             notes = new { type = "string" },
             inspiredBy = new { type = "array", items = new { type = "string" } }
          },
          required = new[]
          {
             "title", "ingredients", "directions", "servings", "description",
             "prepTime", "cookTime", "totalTime", "notes", "inspiredBy"
          }
       })
    };
}