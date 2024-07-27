using AutoMapper;

namespace Cookbook.Factory.Models;

public interface IRecipe
{
    public string Title { get; set; }
    public string Description { get; set; }
    public string Servings { get; set; }
    public string PrepTime { get; set; }
    public string CookTime { get; set; }
    public string TotalTime { get; set; }
    public List<string> Ingredients { get; set; }
    public List<string> Directions { get; set; }
    public string Notes { get; set; }
}

public interface IScrapedRecipe
{
    public string RecipeUrl { get; set; }
    public string ImageUrl { get; set; }
}

public interface IRelevancyResult
{
    public int RelevancyScore { get; set; }
    public string? RelevancyReasoning { get; set; }
}

public interface ICleanedRecipe
{
    public bool Cleaned { get; set; }
}

public interface ISynthesizedRecipe
{
    public List<string> InspiredBy { get; set; }
}

public class ScrapedRecipe : IRecipe, IScrapedRecipe
{
    public string? RecipeUrl { get; set; }
    public string? ImageUrl { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? Servings { get; set; }
    public string? PrepTime { get; set; }
    public string? CookTime { get; set; }
    public string? TotalTime { get; set; }
    public string? Notes { get; set; }
    public required List<string> Ingredients { get; set; }
    public required List<string> Directions { get; set; }
}

public class Recipe : IRecipe, ICleanedRecipe, IRelevancyResult, IScrapedRecipe
{
    public required string Title { get; set; }
    public required string Description { get; set; }
    public required string Servings { get; set; }
    public required string PrepTime { get; set; }
    public required string CookTime { get; set; }
    public required string TotalTime { get; set; }
    public required List<string> Ingredients { get; set; }
    public required List<string> Directions { get; set; }
    public required string Notes { get; set; }
    public bool Cleaned { get; set; }
    public int RelevancyScore { get; set; }
    public string? RelevancyReasoning { get; set; }
    public string? RecipeUrl { get; set; }
    public string? ImageUrl { get; set; }
}

public class RelevancyResult : IRelevancyResult
{
    public int RelevancyScore { get; set; }
    public string? RelevancyReasoning { get; set; }
}

public class CleanedRecipe : IRecipe
{
    public required string Title { get; set; }
    public required string Description { get; set; }
    public required string Servings { get; set; }
    public required string PrepTime { get; set; }
    public required string CookTime { get; set; }
    public required string TotalTime { get; set; }
    public required List<string> Ingredients { get; set; }
    public required List<string> Directions { get; set; }
    public required string Notes { get; set; }
}

public class SynthesizedRecipe : IRecipe, ISynthesizedRecipe
{
    public required List<string> InspiredBy { get; set; }
    public required string Title { get; set; }
    public required string Description { get; set; }
    public required string Servings { get; set; }
    public required string PrepTime { get; set; }
    public required string CookTime { get; set; }
    public required string TotalTime { get; set; }
    public required List<string> Ingredients { get; set; }
    public required List<string> Directions { get; set; }
    public required string Notes { get; set; }
}

public class RecipeAnalysis
{
    public bool Passes { get; set; }
    public required string Feedback { get; set; }
}

public class AutoMapperProfile : Profile
{
    public AutoMapperProfile()
    {
        CreateMap<CleanedRecipe, Recipe>().ReverseMap();
        CreateMap<ScrapedRecipe, Recipe>().ReverseMap();
        CreateMap<SynthesizedRecipe, Recipe>().ReverseMap();
    }
}