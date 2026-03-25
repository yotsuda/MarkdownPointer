using System.Collections.Generic;
using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using MarkdownPointer.Mcp.Services;

namespace MarkdownPointer.Mcp.Tools;

[McpServerToolType]
public class SlideTools
{
    [McpServerTool]
    [Description("Load a Markdown or YAML deck file and generate PPTX + preview images. Returns slide count and structure info.")]
    public static string LoadDeck(
        SlideService service,
        [Description("Absolute path to a .md or .yaml file")] string path)
    {
        try
        {
            var result = service.Load(path);
            var info = service.GetSlideInfo();
            return $"{result}\n\n{info}";
        }
        catch (Exception ex)
        {
            return $"Error loading {path}: {ex.Message}";
        }
    }

    [McpServerTool]
    [Description("Get structured text description of slide shapes and content. Very low token cost. Use this first before requesting an image.")]
    public static string GetSlideInfo(
        SlideService service,
        [Description("Slide index (0-based). Omit to get all slides.")] int? slide_index = null)
    {
        return service.GetSlideInfo(slide_index);
    }

    [McpServerTool]
    [Description("Get a rendered slide image. Use small width (320-480) to save tokens. Only request when you need to visually verify layout.")]
    public static IEnumerable<ContentBlock> GetSlideImage(
        SlideService service,
        [Description("Slide index (0-based)")] int slide_index,
        [Description("Image width in pixels. Default 480 (270p). Use 320 for quick check, 960 for detail.")] int width = 480)
    {
        try
        {
            var bytes = service.GetSlideImage(slide_index, width);
            if (bytes is null)
                return [new TextContentBlock { Text = "Error: Slide not found or no deck loaded." }];

            return [ImageContentBlock.FromBytes(bytes, "image/jpeg")];
        }
        catch (Exception ex)
        {
            return [new TextContentBlock { Text = $"Error in GetSlideImage: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}" }];
        }
    }

    [McpServerTool]
    [Description("Update a Markdown or YAML deck file and regenerate PPTX + previews. Returns updated slide info.")]
    public static string UpdateDeck(
        SlideService service,
        [Description("Absolute path to the .md or .yaml file")] string path,
        [Description("New full content of the file")] string content)
    {
        try
        {
            return service.UpdateContent(path, content);
        }
        catch (Exception ex)
        {
            return $"Error updating {path}: {ex.Message}";
        }
    }

    [McpServerTool]
    [Description("Import an existing PPTX file and convert it to Markdown. Extracts text, tables, and images (saved to assets/ folder). Returns the Markdown content. Use this to analyze and reuse existing PowerPoint assets.")]
    public static string ImportPptx(
        SlideService service,
        [Description("Absolute path to the .pptx file to import")] string pptx_path,
        [Description("Optional: output path for the .md file. Defaults to same name as the PPTX with .md extension.")] string? output_path = null)
    {
        try
        {
            return service.ImportPptx(pptx_path, output_path);
        }
        catch (Exception ex)
        {
            return $"Error importing {pptx_path}: {ex.Message}\n{ex.StackTrace}";
        }
    }
}
