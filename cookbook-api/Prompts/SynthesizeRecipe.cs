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
           - You are provided with a list of recipes scraped from the internet.
           - Use these recipes as a basis for inspiration for the new synthesized recipe, mixing and matching elements.
        3. **Create New Recipe:**
           - Blend elements from relevant recipes.
           - Ensure it meets dietary needs, preferences, skill level, and time constraints.
           - Align with the cookbook's theme and cultural goals.
           - Don't include the word "Step" in the enumerated directions.
        4. **Customize Recipe:**
           - Scale the ingredients to adjust for the desired serving size. If a recipe serves 2 and the user wants 4 servings, double the ingredients.
           - Include alternatives or substitutions when the provided recipes are in conflict of the user's dietary restrictions.
           - IMPORTANT: Always alter the synthesized recipe given the user's allergies.
        5. **Enhance Recipe:**
           - Add cultural context or storytelling.
           - Incorporate educational content.
           - Include meal prep tips or leftover ideas.
           - Scale the amount of detail according to how specific the user's cookbook expectations are. Keep it concise for simple orders.
           - Do not add factual information such as nutritional data or facts.
        6. **Format Output:**
           - Use provided Recipe class structure.
           - Fill out all fields.
           - Include customizations, cultural context, or educational content in Notes. Use Markdown for formatting (triple ###).
           - Omit a conclusion.
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
        Description = "Synthesize a personalized recipe using existing recipes and user's cookbook order",
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
                inspiredBy = new { type = "array", items = new { type = "string" } },
                notes = new { type = "string" },
            },
            required = new[]
            {
                "title", "description", "servings", "prepTime", "cookTime", "totalTime",
                "ingredients", "directions", "inspiredBy", "notes"
            }
        })
    };
}