using System.Text;
using AutoMapper;
using Cookbook.Factory.Prompts;
using Cookbook.Factory.Services;

namespace Cookbook.Factory.Models;

public class Recipe
{
    public string? Id { get; set; }
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
    public string? RecipeUrl { get; set; }
    public string? ImageUrl { get; set; }
    public required List<string> Aliases { get; set; }
    public required string IndexTitle { get; set; }
    public required Dictionary<string, RelevancyResult> Relevancy { get; set; }
}

public class ScrapedRecipe
{
    public string? Id { get; set; }
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

public class CleanedRecipe
{
    public bool Cleaned { get; set; }
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

public class SynthesizedRecipe
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

    public List<string>? ImageUrls { get; set; }

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

        sb.AppendLine(this.ToMarkdownProperty(nameof(Servings)));
        sb.Append("&emsp;");
        sb.Append("&emsp;");
        sb.Append(this.ToMarkdownProperty(nameof(CookTime)));
        sb.AppendLine();
        sb.Append(this.ToMarkdownProperty(nameof(PrepTime)));
        sb.Append("&emsp;");
        sb.Append("&emsp;");
        sb.Append(this.ToMarkdownProperty(nameof(TotalTime)));
        sb.AppendLine();
        sb.AppendLine();

        sb.AppendLine(this.ToMarkdownSection(nameof(Description), false));
        sb.AppendLine(Utils.ToMarkdownHorizontalRule());

        sb.Append(this.ToMarkdownList(nameof(Ingredients)));
        sb.Append(this.ToMarkdownNumberedList(nameof(Directions)));
        sb.Append(this.ToMarkdownSection(nameof(Notes)));
        sb.AppendLine();

        if (ImageUrls?.Count > 0)
        {
            sb.AppendLine(Utils.ToMarkdownImage(Title, $"{FileService.SanitizeFileName(Title)}.jpg"));
        }

        sb.Append(this.ToMarkdownList(nameof(InspiredBy)));

        return sb.ToString().Trim();
    }
}

public class AutoMapperProfile : Profile
{
    public AutoMapperProfile()
    {
        CreateMap<CleanedRecipe, Recipe>().ReverseMap();
        CreateMap<ScrapedRecipe, Recipe>()
            .ForMember(dest => dest.Aliases, opt => opt.MapFrom(src => new List<string>()))
            .ForMember(dest => dest.Relevancy, opt => opt.MapFrom(src => new Dictionary<string, RelevancyResult>()))
            .ReverseMap();
        CreateMap<SynthesizedRecipe, Recipe>().ReverseMap();
    }
}