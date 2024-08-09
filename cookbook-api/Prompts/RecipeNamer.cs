using System.Text.Json;
using Cookbook.Factory.Models;
using Cookbook.Factory.Services;

namespace Cookbook.Factory.Prompts;

public class RecipeNamerPrompt : PromptBase
{
    public override string Name => "Recipe Namer";
    public override string Description => "Generalize recipe names to improve searchability";
    public override string Model => LlmModels.Gpt4Omini;

    public override string SystemPrompt =>
        """
        # Recipe Indexing and Alias Generation
        
        Your task is to create entries for a recipe index and generate searchable aliases.
        
        ## Output:
        1. **Index Entry**: The simplest possible name for the recipe, as it would appear in a cookbook index.
        2. **Aliases**: A list including the original title and variations for searchability.
        
        ## Index Entry Rules:
        1. Use the fewest words possible to describe the core dish.
        2. Remove ALL of the following:
           - Cooking methods (baked, fried, slow cooker, etc.)
           - Descriptive adjectives (easy, creamy, best, etc.)
           - Brand names or appliance types
           - Dietary labels (keto, vegan, etc.)
           - Time-related words (quick, 30-minute, etc.)
        3. Keep only the main ingredient or dish type.
        4. If unsure, ask yourself: "What single page would I look for in a cookbook index to find this recipe?"
        
        ## Alias Rules:
        1. First alias is always the original recipe title.
        2. Include the Index Entry as an alias.
        3. Add variations based on key ingredients and methods.
        
        ## Examples:
        
        1. Input: "Crock Pot Buffalo Chicken Dip"
           Index Entry: "Chicken Dip"
           Aliases: ["Crock Pot Buffalo Chicken Dip", "Chicken Dip", "Buffalo Dip", "Slow Cooker Dip"]
        
        2. Input: "Baked Eggplant With Ricotta, Mozzarella and Anchovy"
           Index Entry: "Eggplant"
           Aliases: ["Baked Eggplant With Ricotta, Mozzarella and Anchovy", "Eggplant", "Cheesy Eggplant", "Eggplant Casserole"]
        
        3. Input: "30-Minute One-Pan Lemon Garlic Shrimp Pasta"
           Index Entry: "Shrimp Pasta"
           Aliases: ["30-Minute One-Pan Lemon Garlic Shrimp Pasta", "Shrimp Pasta", "Lemon Pasta", "Quick Pasta"]
        
        4. Input: "Grandma's Best Old-Fashioned Apple Pie"
           Index Entry: "Apple Pie"
           Aliases: ["Grandma's Best Old-Fashioned Apple Pie", "Apple Pie", "Traditional Pie", "Fruit Pie"]
        
        Remember: The Index Entry should be as simple as possible, like a cookbook index entry. All details go in the Aliases.
        """;

    public string GetUserPrompt(Recipe recipe)
       => $"""
          Recipe:
          ```json
          {JsonSerializer.Serialize(recipe)}
          ```
          """;

    public override FunctionDefinition GetFunction() => new()
    {
        Name = "IndexRecipe",
        Description = "Provide a indexed title and aliases",
        Parameters = JsonSerializer.Serialize(new
        {
            type = "object",
            properties = new
            {
                indexTitle = new { type = "string" },
                aliases = new { type = "array", items = new { type = "string" } },
            },
            required = new[] { "indexTitle", "aliases" }
        })
    };
}

public class RenamerResult
{
   public required string IndexTitle { get; set; }
   public required List<string> Aliases { get; set; }
}