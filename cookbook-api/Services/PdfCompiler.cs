using Cookbook.Factory.Models;
using MigraDoc.Rendering;
using Orionsoft.MarkdownToPdfLib;
using Orionsoft.MarkdownToPdfLib.Styling;
using PdfSharp.Pdf;
using Serilog;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;
using ILogger = Serilog.ILogger;

namespace Cookbook.Factory.Services;

// Third party library for converting markdown to PDF
// https://github.com/tkubec/MarkdownToPdf/tree/master

public class PdfCompilerConfig : IConfig
{
    public string FontName { get; init; } = "Garamond";
    public int FontSize { get; init; } = 12;
    public string Title { get; init; } = "Cookbook";
    public string Author { get; init; } = "Zarichney Development";
    public string ImageDirectory { get; init; } = "temp";
    public int ImageWidth { get; init; } = 400;
}

public class PdfCompiler(PdfCompilerConfig config)
{
    private readonly ILogger _log = Log.ForContext<PdfCompiler>();

    public async Task<PdfDocument> CompileCookbook(CookbookOrder order)
    {
        var recipePages = order.SynthesizedRecipes!;

        var pdf = new MarkdownToPdf()
            .DefaultFont(config.FontName, config.FontSize)
            .Title(config.Title)
            .Author(config.Author)
            .ImageDir(config.ImageDirectory)
            .PageMargins(
                Dimension.FromCentimeters(1.75),
                Dimension.FromCentimeters(1.75),
                Dimension.FromCentimeters(1),
                Dimension.FromCentimeters(1.75)
            )
            .AddFooter("{align=center}\r\n\\- [](md:page) - ");

        var pStyle = pdf.StyleManager.Styles[MarkdownStyleNames.Paragraph];
        pStyle.Margin.Top = 0;
        pStyle.Margin.Bottom = 0;
        pStyle.Padding.Top = 0;
        pStyle.Padding.Bottom = 0;
        pdf.StyleManager.ForElement(ElementType.Paragraph).Bind(pStyle);

        var tableStyle = pdf.StyleManager.Styles[MarkdownStyleNames.Table];
        tableStyle.Table.Width = "25%";
        pdf.StyleManager.ForElement(ElementType.Table).Bind(tableStyle);

        var imgStyle = pdf.StyleManager.Styles[MarkdownStyleNames.Image];
        // imgStyle.
        pdf.StyleManager.ForElement(ElementType.Image).Bind(imgStyle);

        PdfDocument result;
        try
        {
            var lastRecipe = recipePages.Last().Title;

            foreach (var recipe in recipePages)
            {
                var markdown = recipe.ToMarkdown();
                pdf.Add(markdown);
                
                await DownloadRecipeImage(recipe);

                // Page break
                if (recipe.Title != lastRecipe)
                {
                    pdf.AddSection();
                }
            }

            var docRenderer = new PdfDocumentRenderer(false)
            {
                Document = pdf.MigraDocument
            };
            docRenderer.RenderDocument();
            result = docRenderer.PdfDocument;
        }
        finally
        {
            var imageFiles = recipePages.Select(r => FileService.SanitizeFileName(r.Title)).ToList();
            CleanupImages(imageFiles);
        }

        return result;
    }

    private async Task DownloadRecipeImage(SynthesizedRecipe recipe)
    {
        var firstImgUrl = recipe.ImageUrls?.FirstOrDefault();
        if (!string.IsNullOrEmpty(firstImgUrl))
        {
            var imgFileName = recipe.Title;

            foreach (var imgUrl in recipe.ImageUrls!)
            {
                try
                {
                    await DownloadImage(imgUrl, FileService.SanitizeFileName(imgFileName));
                    break; // Only download the first successful image
                }
                catch (Exception ex)
                {
                    _log.Information(ex, "Error downloading image {imgUrl} for recipe: {RecipeTitle}", imgUrl,
                        recipe.Title);
                    // Attempt the next url
                }
            }
        }
    }

    private async Task DownloadImage(string url, string fileName)
    {
        try
        {
            using var httpClient = new HttpClient();
            using var response = await httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var image = await Image.LoadAsync(stream);

            // Resize the image
            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new Size(config.ImageWidth, 0),
                Mode = ResizeMode.Max
            }));

            // Ensure the image directory exists
            Directory.CreateDirectory(config.ImageDirectory);

            // Save as JPG
            var outputPath = Path.Combine(config.ImageDirectory, $"{fileName}.jpg");
            await image.SaveAsync(outputPath, new JpegEncoder());

            _log.Information("Image downloaded and processed successfully: {FileName}", outputPath);
        }
        catch (HttpRequestException ex)
        {
            _log.Warning(ex, "Error downloading image from URL: {Url}", url);
            throw;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Error processing image for recipe: {FileName}", fileName);
            throw;
        }
    }

    private void CleanupImages(List<string> fileNames)
    {
        foreach (var filename in fileNames)
        {
            var filePath = Path.Combine(config.ImageDirectory, $"{filename}.jpg");
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    _log.Information("Deleted image: {FileName}", filePath);
                }
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Error deleting image: {FileName}", filePath);
            }
        }
    }
}