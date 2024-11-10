using System.Text;
using Cookbook.Factory.Config;
using Cookbook.Factory.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Serilog;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;
using ILogger = Serilog.ILogger;
using Image = SixLabors.ImageSharp.Image;
using Size = SixLabors.ImageSharp.Size;

namespace Cookbook.Factory.Services;

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

    private readonly MarkdownPipeline _markdownPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseAutoIdentifiers()
        .UseEmphasisExtras()
        .UseGridTables()
        .UsePipeTables()
        .Build();

    public async Task<byte[]> CompileCookbook(CookbookOrder order)
    {
        QuestPDF.Settings.License = LicenseType.Community;

        var recipes = order.SynthesizedRecipes!;
        var processedContent = new List<(MarkdownDocument Content, string? ImagePath)>();

        try
        {
            // Process all recipes and images first
            foreach (var recipe in recipes)
            {
                var markdown = recipe.ToMarkdown();
                var parsedContent = Markdown.Parse(markdown, _markdownPipeline);

                string? imagePath = null;
                if (recipe.ImageUrls?.Any() == true)
                {
                    imagePath = await ProcessFirstValidImage(recipe.ImageUrls, recipe.Title);
                }

                processedContent.Add((parsedContent, imagePath));
            }

            return GeneratePdf(processedContent);
        }
        finally
        {
            await CleanupImages(recipes.Select(x => x.Title).ToList());
        }
    }

    private async Task<string?> ProcessFirstValidImage(IEnumerable<string> imageUrls, string recipeTitle)
    {
        var urlList = imageUrls.ToList();
        _log.Information("Attempting to process {Count} image URLs for recipe: {RecipeTitle}",
            urlList.Count, recipeTitle);

        for (var i = 0; i < urlList.Count; i++)
        {
            var url = urlList[i];
            _log.Information("Trying image URL {Index}/{Total} for {RecipeTitle}: {Url}",
                i + 1, urlList.Count, recipeTitle, url);

            try
            {
                if (!IsValidImageUrl(url))
                {
                    _log.Warning("Invalid image URL format at index {Index} for {RecipeTitle}: {Url}",
                        i, recipeTitle, url);
                    continue;
                }

                var imagePath = await ProcessImage(url, recipeTitle);
                if (!string.IsNullOrEmpty(imagePath))
                {
                    _log.Information("Successfully processed image {Index}/{Total} for {RecipeTitle}",
                        i + 1, urlList.Count, recipeTitle);
                    return imagePath;
                }
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Failed to process image URL {Index}/{Total} for {RecipeTitle}: {Url}",
                    i + 1, urlList.Count, recipeTitle, url);
            }
        }

        _log.Warning("No valid images found for recipe: {RecipeTitle} after trying {Count} URLs",
            recipeTitle, urlList.Count);
        return null;
    }

    private bool IsValidImageUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (url.StartsWith("data:image/"))
        {
            try
            {
                var base64Data = url.Split(',')[1];
                _ = Convert.FromBase64String(base64Data);
                return true;
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Invalid data URL format");
                return false;
            }
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uriResult)
               && (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps);
    }

    private async Task<string> ProcessImage(string url, string fileName)
    {
        try
        {
            byte[] imageBytes;

            if (url.StartsWith("data:image/"))
            {
                _log.Information("Processing data URL image for {FileName}", fileName);
                var base64Data = url.Split(',')[1];
                imageBytes = Convert.FromBase64String(base64Data);
            }
            else if (url.StartsWith("http://") || url.StartsWith("https://"))
            {
                _log.Information("Downloading image from URL for {FileName}", fileName);
                using var httpClient = new HttpClient();
                imageBytes = await httpClient.GetByteArrayAsync(url);
            }
            else
            {
                _log.Error("Unsupported image URL scheme for {FileName}: {Url}", fileName, url);
                return string.Empty;
            }

            if (!Directory.Exists(config.ImageDirectory))
            {
                Directory.CreateDirectory(config.ImageDirectory);
            }

            var outputPath = Path.Combine(
                config.ImageDirectory,
                $"{FileService.SanitizeFileName(fileName)}_{Guid.NewGuid():N}.jpg"
            );

            using (var image = Image.Load(imageBytes))
            {
                image.Mutate(x => x.Resize(new ResizeOptions
                {
                    Size = new Size(config.ImageWidth, 0),
                    Mode = ResizeMode.Max
                }));

                await using var fileStream = File.Create(outputPath);
                await image.SaveAsJpegAsync(fileStream, new JpegEncoder { Quality = 90 });
            }

            _log.Information("Image processed successfully: {FileName}", outputPath);
            return outputPath;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to process image for recipe: {FileName}. URL: {Url}", fileName, url);
            return string.Empty;
        }
    }

    private Task CleanupImages(List<string> fileNames)
    {
        foreach (var fileName in fileNames)
        {
            var pattern = Path.Combine(config.ImageDirectory, $"{FileService.SanitizeFileName(fileName)}*.jpg");
            try
            {
                foreach (var file in Directory.GetFiles(Path.GetDirectoryName(pattern)!,
                             Path.GetFileName(pattern)))
                {
                    File.Delete(file);
                    _log.Information("Deleted image: {FileName}", file);
                }
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Error cleaning up image: {FileName}", fileName);
            }
        }

        return Task.CompletedTask;
    }

    private byte[] GeneratePdf(IReadOnlyList<(MarkdownDocument Content, string? ImagePath)> content)
    {
        return Document.Create(container =>
        {
            container.Page(page =>
            {
                // Page setup
                page.Size(PageSizes.A4);
                page.Margin(1, Unit.Centimetre);
                page.PageColor(Colors.White);
                page.DefaultTextStyle(x => x
                    .FontFamily(config.FontName)
                    .FontSize(config.FontSize)
                    .LineHeight(1.2f));

                // Main content
                page.Content()
                    .Shrink() // Allows content to take minimum required space
                    .Column(column =>
                    {
                        column.Spacing(20);

                        foreach (var (doc, imagePath) in content)
                        {
                            column.Item().Component(new RecipeComponent(doc, imagePath, config));

                            // Only add page break if not the last item
                            if (doc != content.Last().Content)
                            {
                                column.Item().PageBreak();
                            }
                        }
                    });

                // Footer
                page.Footer()
                    .AlignCenter()
                    .Text(text =>
                    {
                        text.Span("Page ");
                        text.CurrentPageNumber();
                    });
            });
        }).GeneratePdf();
    }

    private class RecipeComponent(MarkdownDocument document, string? imagePath, PdfCompilerConfig config)
        : IComponent
    {
        public void Compose(IContainer container)
        {
            container
                .Shrink() // Allow content to take minimum space needed
                .Column(column =>
                {
                    column.Spacing(10);
                    var isFirstBlock = true;

                    foreach (var block in document)
                    {
                        // Special handling for title
                        if (isFirstBlock && block is HeadingBlock titleBlock)
                        {
                            column.Item()
                                .PaddingBottom(10)
                                .Text(text => text
                                    .Span(GetInlineText(titleBlock.Inline!))
                                    .FontSize(config.FontSize + 8)
                                    .Bold());
                            isFirstBlock = false;
                            continue;
                        }

                        column.Item().Element(elementContainer =>
                        {
                            switch (block)
                            {
                                case HeadingBlock heading:
                                    RenderHeading(elementContainer, heading);
                                    break;
                                case ParagraphBlock paragraph:
                                    RenderParagraph(elementContainer, paragraph);
                                    break;
                                case ListBlock list:
                                    RenderList(elementContainer, list);
                                    break;
                                case ThematicBreakBlock:
                                    elementContainer
                                        .Border(1)
                                        .BorderColor(Colors.Grey.Lighten2);
                                    break;
                            }
                        });
                    }
                });
        }

        private void RenderHeading(IContainer container, HeadingBlock heading)
        {
            var fontSize = heading.Level switch
            {
                2 => config.FontSize + 6,
                3 => config.FontSize + 4,
                _ => config.FontSize + 2
            };

            container
                .Shrink()
                .PaddingVertical(5)
                .Text(text => text
                    .Span(GetInlineText(heading.Inline!))
                    .FontSize(fontSize)
                    .Bold());
        }

        private void RenderParagraph(IContainer container, LeafBlock paragraph)
        {
            // Handle images
            if (paragraph.Inline?.FirstOrDefault(x => x is LinkInline { IsImage: true }) is LinkInline &&
                !string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
            {
                container
                    .Shrink()
                    .MaxHeight(180)
                    .Image(imagePath)
                    .FitArea();
                return;
            }

            // Handle text
            var text = GetInlineText(paragraph.Inline!);
            if (IsMetadataLine(text))
            {
                RenderMetadataLine(container, text);
            }
            else
            {
                container
                    .Shrink()
                    .PaddingVertical(5)
                    .Text(textContent => RenderInlines(textContent, paragraph.Inline));
            }
        }

        private void RenderList(IContainer container, ListBlock list)
        {
            container
                .Shrink()
                .PaddingLeft(20)
                .Column(column =>
                {
                    column.Spacing(2);
                    var index = 1;

                    foreach (var item in list.Cast<ListItemBlock>())
                    {
                        if (item.Descendants<ParagraphBlock>().FirstOrDefault() is not { } paragraph)
                            continue;

                        var index1 = index;
                        column.Item().Row(row =>
                        {
                            var currentIndex = index1;
                            row.ConstantItem(20).Text(text =>
                                text.Span(list.IsOrdered ? $"{currentIndex}." : "•"));

                            row.RelativeItem().Text(text =>
                                RenderInlines(text, paragraph.Inline));
                        });

                        index++;
                    }
                });
        }

        private void RenderMetadataLine(IContainer container, string text)
        {
            container
                .Shrink()
                .Row(row =>
                {
                    var parts = text.Split(new[] { "**" }, StringSplitOptions.None);
                    for (var i = 0; i < parts.Length; i++)
                    {
                        if (string.IsNullOrEmpty(parts[i])) continue;

                        var index = i;
                        row.AutoItem().Text(textContent =>
                        {
                            var span = textContent.Span(parts[index]);
                            if (index % 2 == 1) // Bold sections
                            {
                                span.Bold();
                            }
                        });

                        if (i < parts.Length - 1)
                        {
                            row.AutoItem().Width(10);
                        }
                    }
                });
        }

        private void RenderInlines(TextDescriptor text, ContainerInline? inlines)
        {
            if (inlines == null) return;

            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case LiteralInline literal:
                        text.Span(literal.Content.ToString());
                        break;

                    case EmphasisInline emphasis:
                        var span = text.Span(GetInlineText(emphasis));
                        if (emphasis.DelimiterCount == 2)
                            span.Bold();
                        else
                            span.Italic();
                        break;

                    case LinkInline { IsImage: false } link:
                        text.Hyperlink(link.Title ?? link.Url, link.Url);
                        break;
                }
            }
        }

        private static bool IsMetadataLine(string text)
        {
            var normalizedText = text.Replace("**", "");
            return normalizedText.Contains("Servings:", StringComparison.OrdinalIgnoreCase) ||
                   normalizedText.Contains("Prep Time:", StringComparison.OrdinalIgnoreCase) ||
                   normalizedText.Contains("Cook Time:", StringComparison.OrdinalIgnoreCase) ||
                   normalizedText.Contains("Total Time:", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetInlineText(ContainerInline inline)
        {
            var text = new StringBuilder();
            foreach (var item in inline)
            {
                switch (item)
                {
                    case LiteralInline literal:
                        text.Append(literal.Content);
                        break;
                    case EmphasisInline emphasis:
                        var emphasisText = GetInlineText(emphasis);
                        var marker = emphasis.DelimiterCount == 2 ? "**" : "*";
                        text.Append($"{marker}{emphasisText}{marker}");
                        break;
                }
            }

            return text.ToString();
        }
    }
}