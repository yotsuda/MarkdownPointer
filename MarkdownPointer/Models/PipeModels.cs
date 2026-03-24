namespace MarkdownPointer.Services;

public class PipeMessage
{
    public string Command { get; set; } = "";
    public string? Path { get; set; }
    public string[]? Paths { get; set; }
    public int? Line { get; set; }
    public string? Title { get; set; }
    public bool? SlideView { get; set; }
    public string? SlideAction { get; set; }
    public int? SlideIndex { get; set; }
    public string? OutputPath { get; set; }
    public string? TemplatePath { get; set; }
}

public class PipeResponse
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public OpenedTabInfo? OpenedTab { get; set; }
    public WindowInfo[]? Windows { get; set; }
    public SlideStateInfo? SlideState { get; set; }
    public string? ExportOutput { get; set; }
}

public class SlideStateInfo
{
    public int CurrentIndex { get; set; }
    public int TotalSlides { get; set; }
    public string CurrentContent { get; set; } = "";
    public string? NextContent { get; set; }
    public bool Overflowed { get; set; }
}

public class OpenedTabInfo
{
    public int WindowIndex { get; set; }
    public int TabIndex { get; set; }
    public string Title { get; set; } = "";
    public string Path { get; set; } = "";
}

public class WindowInfo
{
    public int Index { get; set; }
    public TabInfo[] Tabs { get; set; } = Array.Empty<TabInfo>();
}

public class TabInfo
{
    public int Index { get; set; }
    public string Title { get; set; } = "";
    public string Path { get; set; } = "";
    public bool IsSelected { get; set; }
    public bool IsSlideView { get; set; }
    public int? CurrentLine { get; set; }
    public string[]? Errors { get; set; }
}
