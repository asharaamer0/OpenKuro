$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName WindowsBase, PresentationCore, PresentationFramework
$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $root 'src/Kuro/Brand.xaml'
$resources = [Windows.Markup.XamlReader]::Parse([IO.File]::ReadAllText($source))
$drawing = $resources['KuroMark'].Drawing
$images = foreach ($size in @(16, 24, 32, 48, 64, 256)) {
    $visual = New-Object Windows.Media.DrawingVisual
    $context = $visual.RenderOpen()
    $context.PushTransform([Windows.Media.ScaleTransform]::new($size / 48.0, $size / 48.0))
    $context.DrawDrawing($drawing)
    $context.Pop()
    $context.Close()
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $buffer = [IO.MemoryStream]::new()
    $encoder.Save($buffer)
    [pscustomobject]@{ Size = $size; Bytes = $buffer.ToArray() }
    $buffer.Dispose()
}
$target = Join-Path $root 'src/Kuro/Assets/Kuro.ico'
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
$writer = [IO.BinaryWriter]::new([IO.File]::Create($target))
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$images.Count)
    $offset = 6 + 16 * $images.Count
    foreach ($entry in $images) {
        $writer.Write([byte]($entry.Size % 256))
        $writer.Write([byte]($entry.Size % 256))
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$entry.Bytes.Length)
        $writer.Write([uint32]$offset)
        $offset += $entry.Bytes.Length
    }
    foreach ($entry in $images) { $writer.Write([byte[]]$entry.Bytes) }
}
finally { $writer.Dispose() }
