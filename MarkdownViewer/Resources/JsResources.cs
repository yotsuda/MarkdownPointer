using System.IO;
using System.Reflection;

namespace MarkdownViewer.Resources
{
    /// <summary>
    /// JavaScript resources for rendered Markdown content.
    /// Scripts are stored as embedded resources in Resources/Scripts/*.js
    /// </summary>
    public static class JsResources
    {
        private static readonly Assembly _assembly = typeof(JsResources).Assembly;
        private static readonly Dictionary<string, string> _cache = new();

        /// <summary>
        /// Core event handlers: link clicks, hovers, zoom, mouse events.
        /// </summary>
        public static string CoreEventHandlers => GetScript("CoreEventHandlers.js");

        /// <summary>
        /// Scroll to line function and pointing mode variables.
        /// </summary>
        public static string ScrollAndPointingMode => GetScript("ScrollAndPointingMode.js");

        /// <summary>
        /// Pointing mode helper functions: getPointableElement, getElementLine, etc.
        /// </summary>
        public static string PointingHelpers => GetScript("PointingHelpers.js");

        /// <summary>
        /// getElementContent function for pointing mode content extraction.
        /// </summary>
        public static string GetElementContent => GetScript("GetElementContent.js");

        /// <summary>
        /// Pointing mode mouse event handlers.
        /// </summary>
        public static string PointingEventHandlers => GetScript("PointingEventHandlers.js");

        /// <summary>
        /// DOMContentLoaded handler for KaTeX and Mermaid rendering.
        /// </summary>
        public static string DomContentLoadedHandler => GetScript("DomContentLoadedHandler.js");

        /// <summary>
        /// Mermaid node processing functions for click handling and line mapping.
        /// </summary>
        public static string MermaidNodeProcessing => GetScript("MermaidNodeProcessing.js");

        /// <summary>
        /// Reads an embedded JavaScript resource.
        /// </summary>
        private static string GetScript(string filename)
        {
            if (_cache.TryGetValue(filename, out var cached))
                return cached;

            var resourceName = $"MarkdownViewer.App.Resources.Scripts.{filename}";
            using var stream = _assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded resource not found: {resourceName}");
            using var reader = new StreamReader(stream);
            var content = reader.ReadToEnd();
            _cache[filename] = content;
            return content;
        }
    }
}

