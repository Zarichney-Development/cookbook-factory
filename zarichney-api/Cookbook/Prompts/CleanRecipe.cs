using System.Text.Json;
using AutoMapper;
using Zarichney.Config;
using Zarichney.Cookbook.Models;
using Zarichney.Services;

namespace Zarichney.Cookbook.Prompts;

public class CleanRecipePrompt(IMapper mapper) : PromptBase
{
  public override string Name => "Recipe Cleaner";
  public override string Description => "Clean and standardize recipe data";
  public override string Model => LlmModels.Gpt4Omini;

  public override string SystemPrompt =>
    """
    Please assist me in data migration from our legacy repository system in preparation for a data import.
    Clean and standardize recipe data as follows:
    1. Use consistent units (imperial or metric), and check spacing/spelling.
    2. Format ingredients and directions as string arrays with no prefix. If ingredients/steps are merged, break them out into separate lines.
    3. Remove irrelevant content (e.g., "Print Pin It") or bad encoding chars (e.g. '[]').
    4. Remove redundant whitespace, tabs and newlines (e.g '\n')
    5. Do NOT add or alter recipe details.
    6. Keep empty fields; replace nulls with empty strings.
    7. Ensure consistent formatting.
    8. Exclude field names within field values (e.g., {'servings': '4'} and not {'servings': 'Servings 4'}).
    """;

  public string GetUserPrompt(Recipe recipe)
    => $"""
        Uncleaned Recipe Data:
        ```json
        {Serialize(recipe)}
        ```
        Return a clean json.
        """;

  private string Serialize(Recipe recipe)
    => JsonSerializer.Serialize(mapper.Map<CleanedRecipe>(recipe));

  public override FunctionDefinition GetFunction() => new()
  {
    Name = "CleanRecipeData",
    Description = "Clean and standardize recipe data",
    Strict = true,
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