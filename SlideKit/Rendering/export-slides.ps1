param(
    [Parameter(Mandatory)][string]$PptxPath,
    [Parameter(Mandatory)][string]$OutputDir,
    [int]$Width = 1920,
    [int]$Height = 1080
)

$ErrorActionPreference = 'Stop'

# MsoTriState: msoTrue = -1, msoFalse = 0
$msoTrue = [int]-1
$msoFalse = [int]0

$app = New-Object -ComObject PowerPoint.Application
try {
    $pres = $app.Presentations.Open($PptxPath, $msoTrue, $msoFalse, $msoFalse)

    for ($i = 1; $i -le $pres.Slides.Count; $i++) {
        $out = Join-Path $OutputDir "slide$($i - 1).png"
        $pres.Slides[$i].Export($out, 'PNG', $Width, $Height)
    }
    $pres.Close()
} finally {
    $app.Quit()
    [System.Runtime.InteropServices.Marshal]::ReleaseComObject($app) | Out-Null
}
