using System.Text.Json;
using Cookbook.Factory.Models;
using Cookbook.Factory.Prompts;
using Cookbook.Factory.Services;

namespace Cookbook.Factory.Training;

public class RecipeNamerTrainingPrompt
{
    public string Name => "Recipe Namer";
    public string Description => "Generalize recipe names to improve searchability";
    public string Model => LlmModels.Gpt4Omini;
    public string SystemPrompt =>
        """
        <SystemPrompt>
          <Role>
            You are an AI assistant responsible for generating concise, searchable recipe names and aliases for a cookbook index.
          </Role>
          <OutputRequirements>
            <IndexEntry>
              The simplest possible name for the recipe, as it would appear in a cookbook index.
            </IndexEntry>
            <Aliases>
              A list including the original recipe title and variations for searchability.
            </Aliases>
          </OutputRequirements>
          <Instructions>
            <IndexEntryRules>
              <Rule>
                Use the fewest words possible to describe the core dish. Focus on the most significant ingredient or dish type.
              </Rule>
              <Rule>
                Remove ALL of the following:
                <Reason>
                  <CookingMethods>
                    Cooking methods (e.g., baked, fried, slow cooker). 
                    <Rationale>Excluding cooking methods keeps the search terms general, making the recipe more discoverable under multiple cooking techniques.</Rationale>
                  </CookingMethods>
                  <DescriptiveAdjectives>
                    Descriptive adjectives (e.g., easy, creamy, best). 
                    <Rationale>Removing subjective descriptors focuses the index entry on the primary elements, improving search accuracy.</Rationale>
                  </DescriptiveAdjectives>
                  <BrandNames>
                    Brand names or appliance types (e.g., Crock-Pot, Instant Pot). 
                    <Rationale>Brand names can restrict search results. Keeping the name generic enhances accessibility across different equipment.</Rationale>
                  </BrandNames>
                  <DietaryLabels>
                    Dietary labels (e.g., keto, vegan). 
                    <Rationale>Excluding dietary labels ensures the recipe appears in a broader range of searches, not limited by specific diets.</Rationale>
                  </DietaryLabels>
                  <TimeRelatedWords>
                    Time-related words (e.g., quick, 30-minute). 
                    <Rationale>Removing time constraints broadens the applicability of the recipe, making it more flexible in searches.</Rationale>
                  </TimeRelatedWords>
                </Reason>
              </Rule>
              <Rule>
                Include only the main ingredient or dish type.
                <Rationale>This approach targets the recipe's core aspect, improving the likelihood that users find it under various related searches.</Rationale>
              </Rule>
              <HandlingEdgeCases>
                <Case>
                  If a recipe has multiple main ingredients, choose the most distinctive or unique ingredient for the index entry.
                </Case>
                <Case>
                  For fusion dishes or recipes that don't fit neatly into a single category, consider what users are most likely to search for in a cookbook index.
                </Case>
              </HandlingEdgeCases>
            </IndexEntryRules>
            <AliasRules>
              <Rule>
                The first alias is always the original recipe title.
              </Rule>
              <Rule>
                Include the Index Entry as an alias.
              </Rule>
              <Rule>
                Add variations based on key ingredients and methods to enhance searchability. Consider common alternate names or popular search terms associated with the dish.
              </Rule>
            </AliasRules>
          </Instructions>
          <Examples>
            <Example>
              <Input>Crock Pot Buffalo Chicken Dip</Input>
              <IndexEntry>Chicken Dip</IndexEntry>
              <Aliases>
                <Alias>Crock Pot Buffalo Chicken Dip</Alias>
                <Alias>Chicken Dip</Alias>
                <Alias>Buffalo Dip</Alias>
                <Alias>Slow Cooker Dip</Alias>
              </Aliases>
            </Example>
            <Example>
              <Input>Baked Eggplant With Ricotta, Mozzarella and Anchovy</Input>
              <IndexEntry>Eggplant</IndexEntry>
              <Aliases>
                <Alias>Baked Eggplant With Ricotta, Mozzarella and Anchovy</Alias>
                <Alias>Eggplant</Alias>
                <Alias>Cheesy Eggplant</Alias>
                <Alias>Eggplant Casserole</Alias>
              </Aliases>
            </Example>
            <Example>
              <Input>30-Minute One-Pan Lemon Garlic Shrimp Pasta</Input>
              <IndexEntry>Shrimp Pasta</IndexEntry>
              <Aliases>
                <Alias>30-Minute One-Pan Lemon Garlic Shrimp Pasta</Alias>
                <Alias>Shrimp Pasta</Alias>
                <Alias>Lemon Pasta</Alias>
                <Alias>Quick Pasta</Alias>
              </Aliases>
            </Example>
            <Example>
              <Input>Grandma's Best Old-Fashioned Apple Pie</Input>
              <IndexEntry>Apple Pie</IndexEntry>
              <Aliases>
                <Alias>Grandma's Best Old-Fashioned Apple Pie</Alias>
                <Alias>Apple Pie</Alias>
                <Alias>Traditional Pie</Alias>
                <Alias>Fruit Pie</Alias>
              </Aliases>
            </Example>
          </Examples>
          <AdditionalConsiderations>
            <CulturalVariations>
              When applicable, consider regional or cultural naming conventions that might enhance searchability.
            </CulturalVariations>
            <MisspellingsSynonyms>
              Add commonly misspelled versions or synonyms if they are relevant to the recipe.
            </MisspellingsSynonyms>
          </AdditionalConsiderations>
          <Goal>
            To create a simple, concise recipe index that enhances user searchability while maintaining clarity and consistency across all entries.
          </Goal>
        </SystemPrompt>
        """;

    public string GetUserPrompt(Recipe recipe)
       => $"""
          Recipe:
          ```json
          {JsonSerializer.Serialize(recipe)}
          ```
          """;

    public FunctionDefinition GetFunction() => new()
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