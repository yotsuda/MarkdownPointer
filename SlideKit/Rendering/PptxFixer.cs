using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace SlideKit.Rendering;

public static class PptxFixer
{
    /// <summary>
    /// Fix the [Content_Types].xml in a generated PPTX.
    /// OpenXML SDK sometimes puts presentation content type as Default for .xml extension,
    /// which PowerPoint COM rejects. This changes the Default .xml to application/xml
    /// and adds an Override for /ppt/presentation.xml.
    /// </summary>
    public static void FixContentTypes(string pptxPath)
    {
        using var archive = ZipFile.Open(pptxPath, ZipArchiveMode.Update);
        var ctEntry = archive.GetEntry("[Content_Types].xml");
        if (ctEntry is null) return;

        XDocument doc;
        using (var stream = ctEntry.Open())
            doc = XDocument.Load(stream);

        var ns = XNamespace.Get("http://schemas.openxmlformats.org/package/2006/content-types");
        var presContentType = "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml";
        bool modified = false;

        // Find the Default element for xml extension
        foreach (var def in doc.Root!.Elements(ns + "Default").ToList())
        {
            if (def.Attribute("Extension")?.Value == "xml" &&
                def.Attribute("ContentType")?.Value == presContentType)
            {
                // Change to generic XML
                def.Attribute("ContentType")!.Value = "application/xml";

                // Add Override for presentation.xml
                doc.Root.Add(new XElement(ns + "Override",
                    new XAttribute("PartName", "/ppt/presentation.xml"),
                    new XAttribute("ContentType", presContentType)));

                modified = true;
                break;
            }
        }

        if (modified)
        {
            ctEntry.Delete();
            var newEntry = archive.CreateEntry("[Content_Types].xml");
            using var writer = new StreamWriter(newEntry.Open(), Encoding.UTF8);
            doc.Save(writer);
        }
    }
}
