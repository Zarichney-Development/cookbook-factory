using MigraDoc.Rendering;
using Orionsoft.MarkdownToPdfLib;
using PdfSharp.Pdf;

namespace Cookbook.Factory.Services;

public class PdfCompiler
{
    public PdfDocument CompileCookbook(List<string> markdownContents)
    {
        var pdf = new MarkdownToPdf();
        
        foreach (var markdown in markdownContents)
        {
            pdf.Add(markdown);
        }

        var documentRenderer = new PdfDocumentRenderer(false)
        {
            Document = pdf.MigraDocument
        };
        documentRenderer.RenderDocument();
        
        return documentRenderer.PdfDocument;
    }
}