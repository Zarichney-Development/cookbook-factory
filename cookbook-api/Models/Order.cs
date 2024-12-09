using System.Text.Json.Serialization;
using Cookbook.Factory.Services;

namespace Cookbook.Factory.Models;

public class CookbookOrderSubmission
{
    [JsonConstructor]
    public CookbookOrderSubmission() { }

    public required string Email { get; init; }
    public required CookbookContent CookbookContent { get; init; }
    public required CookbookDetails CookbookDetails { get; init; }
    public required UserDetails UserDetails { get; init; }

    public string ToMarkdown()
        => $"""
            {CookbookContent}
            {CookbookDetails}
            {UserDetails}
            """.Trim();
}

public class CookbookOrder : CookbookOrderSubmission
{
    [JsonConstructor]
    public CookbookOrder() { }

    public CookbookOrder(CookbookOrderSubmission submission, List<string> recipeList)
    {
        OrderId = GenerateOrderId();
        Email = submission.Email;
        CookbookContent = submission.CookbookContent;
        CookbookDetails = submission.CookbookDetails;
        UserDetails = submission.UserDetails;
        RecipeList = recipeList;
    }
    
    private string GenerateOrderId()
        => Guid.NewGuid().ToString()[..8];

    public string OrderId { get; set; } = null!;
    public List<string> RecipeList { get; set; } = null!;
    public List<SynthesizedRecipe> SynthesizedRecipes { get; set; } = [];
    public OrderStatus Status { get; set; } = OrderStatus.Submitted;
}

public enum OrderStatus
{
    Submitted,
    InProgress,
    Completed,
    Paid
}

public class CookbookContent
{
    public string? RecipeSpecificationType { get; set; }
    public List<string>? SpecificRecipes { get; set; }
    public List<string>? GeneralMealTypes { get; set; }
    public int ExpectedRecipeCount { get; set; }

    public override string ToString()
        => this.ToMarkdown(Utils.SplitCamelCase(nameof(CookbookContent)));
}

public class CookbookDetails
{
    public string? Theme { get; set; }
    public string? PrimaryPurpose { get; set; }
    public List<string>? DesiredCuisines { get; set; }
    public string? CulturalExploration { get; set; }
    public string? NutritionalGuidance { get; set; }
    public string? RecipeModification { get; set; }
    public string? IngredientFlexibility { get; set; }
    public string? OverallStyle { get; set; }
    public string? Organization { get; set; }
    public List<string>? SpecialSections { get; set; }
    public string? Storytelling { get; set; }
    public List<string>? EducationalContent { get; set; }
    public List<string>? PracticalFeatures { get; set; }

    public override string ToString()
        => this.ToMarkdown(Utils.SplitCamelCase(nameof(CookbookDetails)));
}

public class UserDetails
{
    public List<string>? DietaryRestrictions { get; set; }
    public List<string>? Allergies { get; set; }
    public string? SkillLevel { get; set; }
    public List<string>? CookingGoals { get; set; }
    public string? TimeConstraints { get; set; }
    public string? HealthFocus { get; set; }
    public string? FamilyConsiderations { get; set; }
    public int ServingSize { get; set; }

    public override string ToString()
        => this.ToMarkdown(Utils.SplitCamelCase(nameof(UserDetails)));
}