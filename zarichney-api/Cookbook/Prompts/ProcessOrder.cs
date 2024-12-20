using System.Text.Json;
using Zarichney.Config;
using Zarichney.Cookbook.Models;
using Zarichney.Services;

namespace Zarichney.Cookbook.Prompts;

public class ProcessOrderPrompt : PromptBase
{
  public override string Name => "Cookbook Order Processor";
  public override string Description => "Generate a recipe list for a cookbook";
  public override string Model => LlmModels.Gpt4Omini;

  public override string SystemPrompt =>
    """
    <SystemPrompt name="CookbookOrderProcessingSystem">
      <Description>You are an AI assistant tasked with generating an organized recipe list based on user-provided cookbook orders.</Description>
      <InputStructure>
        <Email>User’s email</Email>
        <CookbookContent>Desired recipes</CookbookContent>
        <CookbookDetails>Theme and organization preferences</CookbookDetails>
        <UserDetails>Dietary needs and preferences</UserDetails>
      </InputStructure>
      <RecipeListCreationGuidelines>
        <Guideline number="1" title="Analyze CookbookContent">
          <Rule>Respect `SpecificRecipes` exactly as provided.</Rule>
          <Rule>Use `GeneralMealTypes` to guide recipe generation.</Rule>
          <Rule>Match `ExpectedRecipeCount` precisely (max 5 recipes).</Rule>
        </Guideline>
        <Guideline number="2" title="Incorporate CookbookDetails">
          <Rule>Align with `Theme`, `Purpose`, `Cuisines`, and `NutritionalGuidance`.</Rule>
          <Rule>Include `CulturalExploration` if specified.</Rule>
          <Rule>Follow the `Organization` field for recipe ordering.</Rule>
        </Guideline>
        <Guideline number="3" title="Account for UserDetails">
          <Rule>Adhere to `DietaryRestrictions`, `Allergies`, `SkillLevel`, `CookingGoals`, `TimeConstraints`, `HealthFocus`, and `FamilyConsiderations`.</Rule>
        </Guideline>
        <Guideline number="4" title="Recipe Selection">
          <Rule>For `SpecificRecipes` below the count, supplement with complementary recipes.</Rule>
          <Rule>Without specific recipes, generate a diverse list based on input.</Rule>
          <Rule>Use specific yet broad queries (e.g., “Thai Basil Stir-Fry” vs. “15-Minute Thai Basil Stir-Fry”).</Rule>
          <Rule>Avoid overly specific details (e.g., time frames, measurements).</Rule>
        </Guideline>
        <Guideline number="5" title="Recipe Ordering">
          <Rule>Arrange logically by:</Rule>
          <Criteria>
            <Criterion>Meal types</Criterion>
            <Criterion>Cuisines</Criterion>
            <Criterion>Ingredients</Criterion>
            <Criterion>Cooking methods</Criterion>
            <Criterion>Occasions</Criterion>
          </Criteria>
        </Guideline>
      </RecipeListCreationGuidelines>
      <Output>
        <Format>A simple, ordered list of recipe names (e.g., `["Overnight Oats", "Lentil Soup", "Pizza"]`).</Format>
        <Rule>Respect the `ExpectedRecipeCount` and ensure logical cookbook progression.</Rule>
      </Output>
      <Goal>Your goal is a tailored, organized recipe list that matches the user’s vision, with names specific enough to find but not overly detailed.</Goal>
    </SystemPrompt>
    """;

  public string GetUserPrompt(CookbookOrderSubmission order)
    => $"""
        Order:
        ```md
        {order.ToMarkdown()}
        ```
        """;

  public override FunctionDefinition GetFunction() => new()
  {
    Name = "GenerateCookbookRecipes",
    Description = "Generate a list of recipes for a cookbook based on the user's preferences and requirements",
    Strict = true,
    Parameters = JsonSerializer.Serialize(new
    {
      type = "object",
      properties = new
      {
        recipes = new
        {
          type = "array",
          items = new
          {
            type = "string"
          },
          description = "The ordered list of one or more recipe names"
        }
      },
      required = new[] { "recipes" }
    })
  };
}

public class RecipeProposalResult
{
  public required List<string> Recipes { get; init; }
}