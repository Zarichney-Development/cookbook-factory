using System.Text;
using AutoMapper;
using Cookbook.Factory.Services;

namespace Cookbook.Factory.Models;

public class RecipeProposalResult
{
    public required List<string> Recipes { get; init; }
}

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
    public string? RecipeUrl { get; set; }
    public string? ImageUrl { get; set; }
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

public interface ISynthesizedRecipe
{
    public List<string>? InspiredBy { get; set; }
}

public class SynthesizedRecipe : IRecipe, ISynthesizedRecipe
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
    public List<string>? InspiredBy { get; set; }
    
    public string? ImageUrl { get; set; }

    public int? QualityScore { get; set; }
    public string? Analysis { get; set; }
    public string? Suggestions { get; set; }

    public bool IsAnalyzed =>
        QualityScore.HasValue && !string.IsNullOrEmpty(Analysis) && !string.IsNullOrEmpty(Suggestions);

    public void AddAnalysisResult(RecipeAnalysis analysisResult)
    {
        QualityScore = analysisResult.QualityScore;
        Analysis = analysisResult.Analysis;
        Suggestions = analysisResult.Suggestions;
    }
    
    public string ToMarkdown()
    {
        var sb = new StringBuilder();

        sb.Append(this.ToMarkdownHeader(nameof(Title)));
        sb.Append(this.ToMarkdownSection(nameof(Description), false));

        var rows = new List<List<string>>
        {
            new() { this.ToMarkdownProperty(nameof(Servings)), this.ToMarkdownProperty(nameof(TotalTime)) },
            new() { this.ToMarkdownProperty(nameof(PrepTime)), this.ToMarkdownProperty(nameof(CookTime)) }
        };
        sb.Append(Utils.ToMarkdownTable(rows));
        sb.AppendLine();

        sb.Append(this.ToMarkdownList(nameof(Ingredients)));
        sb.Append(this.ToMarkdownNumberedList(nameof(Directions)));
        sb.Append(this.ToMarkdownSection(nameof(Notes)));
        sb.AppendLine();
        
        sb.Append(this.ToMarkdownImage(nameof(Title), nameof(ImageUrl)));
        
        sb.Append(this.ToMarkdownList(nameof(InspiredBy)));

        return sb.ToString().Trim();
    }
}

public class RecipeAnalysis
{
    public int QualityScore { get; set; }
    public required string Analysis { get; set; }
    public required string Suggestions { get; set; }
}

public class AutoMapperProfile : Profile
{
    public AutoMapperProfile()
    {
        CreateMap<CleanedRecipe, Recipe>().ReverseMap();
        CreateMap<ScrapedRecipe, Recipe>()
            .ForMember(dest => dest.RelevancyScore, opt => opt.MapFrom(src => -1))
            .ReverseMap();
        CreateMap<SynthesizedRecipe, Recipe>().ReverseMap();
    }
}