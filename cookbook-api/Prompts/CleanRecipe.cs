using System.Text.Json;
using AutoMapper;
using Cookbook.Factory.Models;
using Cookbook.Factory.Services;

namespace Cookbook.Factory.Prompts;

public class CleanRecipePrompt(IMapper mapper) : PromptBase
{
    public override string Name => "Recipe Cleaner";
    public override string Description => "Clean and standardize scraped recipe data";
    public override string Model => LlmModels.Gpt4Omini;
    public override string SystemPrompt => 
        """
        You are specialized in cleaning and standardizing scraped recipe data. Follow these guidelines:
        1. Standardize units of measurement (e.g., 'tbsp', 'tsp', 'g').
        2. Correct spacing and spelling issues.
        3. Format ingredients and directions as arrays of strings.
        4. Standardize cooking times and temperatures (e.g., '350°F', '175°C').
        5. Remove irrelevant or accidental scrapped content (e.g. 'Print Pin It').
        6. Do not add new information or change the original recipe.
        7. Leave empty fields as they are, replace nulls with empty strings.
        8. Ensure consistent formatting.
        Return a cleaned, standardized version of the recipe data, preserving original structure and information while improving clarity and consistency.
        """;

    public string GetUserPrompt(Recipe recipe)
        => $"Recipe data:\n{Serialize(recipe)}";
        
    private string Serialize(Recipe recipe)
        => JsonSerializer.Serialize(mapper.Map<ScrapedRecipe>(recipe));  

    public override FunctionDefinition GetFunction() => new()
    {
        Name = "CleanRecipeData",
        Description = "Clean and standardize scraped recipe data",
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
                notes = new { type = "string" },
                ingredients = new { type = "array", items = new { type = "string" } },
                directions = new { type = "array", items = new { type = "string" } },
            },
            required = new[]
            {
                "title", "servings", "description", "prepTime", "cookTime", "totalTime", "notes", "ingredients",
                "directions"
            }
        })
    };
}