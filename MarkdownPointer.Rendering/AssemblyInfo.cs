using System.Runtime.CompilerServices;

// HtmlGenerator exposes internal helpers (InlineLocalImages, ReplaceYouTubeIframes,
// YouTubeIframePattern, LocalImagePattern) consumed by the app's services
// (SlideService, ExportService). The app assembly is "mdp".
[assembly: InternalsVisibleTo("mdp")]
