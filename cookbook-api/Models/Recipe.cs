using AutoMapper;

namespace Cookbook.Factory.Models;

public class Recipe
{
    public string RecipeUrl { get; set; }
    public string Title { get; set; }
    public string Description { get; set; }
    public string ImageUrl { get; set; }
    public string Servings { get; set; }
    public string PrepTime { get; set; }
    public string CookTime { get; set; }
    public string TotalTime { get; set; }
    public List<string> Ingredients { get; set; }
    public List<string> Directions { get; set; }
    public string Notes { get; set; }
    public int Relevancy { get; set; }
    public string? RelevancyReasoning { get; set; }
    public bool Cleaned { get; set; }
}

public class LlmRecipe
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

public class RelevancyFilterResult
{
    public bool IsRelevant { get; set; }
    public int RelevancyScore { get; set; }
    public string? Reasoning { get; set; }
}

public class AutoMapperProfile : Profile
{
    public AutoMapperProfile()
    {
        CreateMap<Recipe, LlmRecipe>().ReverseMap();
    }
}