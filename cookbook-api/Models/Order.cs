using System.Reflection;
using System.Text;

namespace Cookbook.Factory.Models;

public class CookbookOrder
{
    public required CookbookContent CookbookContent { get; init; }
    public required CookbookDetails CookbookDetails { get; init; }
    public required UserDetails UserDetails { get; init; }
    public CookbookOrder() { }

    public override string ToString()
    {
        return $"{CookbookContent}{CookbookDetails}{UserDetails}";
    }
}

public class CookbookContent
{
    public string? RecipeSpecificationType { get; set; }
    public List<string>? SpecificRecipes { get; set; }
    public List<string>? GeneralMealTypes { get; set; }
    public int ExpectedRecipeCount { get; set; }

    public CookbookContent()
    {
    }

    public override string ToString()
    {
        return this.ToFormattedString("Cookbook Content");
    }
}

public class CookbookDetails
{
    public string? Theme { get; set; }
    public string? PrimaryPurpose { get; set; }
    public List<string>? DesiredCuisines { get; set; }
    public string? SkillProgression { get; set; }
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
    
    public CookbookDetails(){}

    public override string ToString()
    {
        return this.ToFormattedString("Cookbook Details");
    }
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
    public required string Email { get; set; }

    public UserDetails()
    {
    }

    public override string ToString()
    {
        return this.ToFormattedString("User Details");
    }
}

public static class ObjectExtensions
{
    public static string ToFormattedString(this object obj, string title)
    {
        var sb = new StringBuilder($"## {title}\n");
        var properties = obj.GetType().GetProperties();

        foreach (var prop in properties)
        {
            AppendPropertyIfNotEmpty(sb, obj, prop);
        }

        return sb.ToString();
    }

    private static void AppendPropertyIfNotEmpty(StringBuilder sb, object obj, PropertyInfo prop)
    {
        var value = prop.GetValue(obj);
        if (IsNullOrEmpty(value)) return;

        sb.Append($"{SplitCamelCase(prop.Name)}: ");
        
        if (value is IEnumerable<object> list)
        {
            sb.AppendLine(string.Join(", ", list));
        }
        else
        {
            sb.AppendLine(value?.ToString());
        }
    }

    private static bool IsNullOrEmpty(object? value)
    {
        if (value == null) return true;
        if (value is string str) return string.IsNullOrEmpty(str);
        if (value is IEnumerable<object> list) return !list.Any();
        if (value.GetType().IsValueType) return value.Equals(Activator.CreateInstance(value.GetType()));
        return false;
    }

    private static string SplitCamelCase(string input)
    {
        return string.Concat(input.Select((x, i) => i > 0 && char.IsUpper(x) ? " " + x : x.ToString()));
    }
}