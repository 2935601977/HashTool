# ============================================================================
#  生成 HashTool 的图标 src\app.ico
#  设计：蓝色圆角方块 + 白色大写 H（哈希工具的首字母）
#  尺寸：16/24/32/48/64 用传统 DIB 帧（.NET 的 Icon 类只认这种），128/256 用 PNG 帧
#  只依赖 .NET 自带绘图，跑一次即可；图标已经生成好了，一般不用再跑。
#     powershell -ExecutionPolicy Bypass -File .\tools\make-icon.ps1
# ============================================================================

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$target = Join-Path $root "src\app.ico"

# ---------------------------------------------------------------------------
#  画一张 256x256 的母版
# ---------------------------------------------------------------------------
function New-MasterBitmap {
    $size = 256
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear([System.Drawing.Color]::Transparent)

    # ---- 圆角方块底：亮蓝 → 深蓝渐变 ----
    $radius = 58
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($size - $d, 0, $d, $d, 270, 90)
    $path.AddArc($size - $d, $size - $d, $d, $d, 0, 90)
    $path.AddArc(0, $size - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $rect = New-Object System.Drawing.Rectangle(0, 0, $size, $size)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect,
        [System.Drawing.Color]::FromArgb(255, 84, 146, 248),
        [System.Drawing.Color]::FromArgb(255, 18, 47, 122),
        60.0)
    $g.FillPath($brush, $path)

    # ---- 内描边，增加立体感 ----
    $inner = New-Object System.Drawing.Drawing2D.GraphicsPath
    $inset = 8
    $ir = $radius - 4
    $id = $ir * 2
    $inner.AddArc($inset, $inset, $id, $id, 180, 90)
    $inner.AddArc($size - $inset - $id, $inset, $id, $id, 270, 90)
    $inner.AddArc($size - $inset - $id, $size - $inset - $id, $id, $id, 0, 90)
    $inner.AddArc($inset, $size - $inset - $id, $id, $id, 90, 90)
    $inner.CloseFigure()
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(55, 255, 255, 255), 6)
    $g.DrawPath($pen, $inner)

    # ---- 白色大写 H：几何绘制（笔画粗细可控，缩到 16px 也看得清）----
    $hHeight = [int]($size * 0.58)      # H 的高
    $hWidth = [int]($size * 0.50)       # H 的宽
    $stroke = [int]($size * 0.155)      # 笔画粗
    $x0 = [int](($size - $hWidth) / 2)
    $y0 = [int](($size - $hHeight) / 2)
    $barTop = $y0 + [int](($hHeight - $stroke) / 2)

    # 先来一层淡阴影，白字在浅色背景下不发飘
    $shadow = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(38, 0, 0, 0))
    $sx = $x0 + [int]($size * 0.018)
    $sy = $y0 + [int]($size * 0.022)
    $sBar = $sy + [int](($hHeight - $stroke) / 2)
    $g.FillRectangle($shadow, $sx, $sy, $stroke, $hHeight)
    $g.FillRectangle($shadow, $sx + $hWidth - $stroke, $sy, $stroke, $hHeight)
    $g.FillRectangle($shadow, $sx, $sBar, $hWidth, $stroke)

    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $g.FillRectangle($white, $x0, $y0, $stroke, $hHeight)                        # 左竖
    $g.FillRectangle($white, $x0 + $hWidth - $stroke, $y0, $stroke, $hHeight)    # 右竖
    $g.FillRectangle($white, $x0, $barTop, $hWidth, $stroke)                     # 横杠

    $white.Dispose(); $shadow.Dispose()
    $pen.Dispose(); $inner.Dispose(); $brush.Dispose(); $path.Dispose(); $g.Dispose()
    return $bmp
}

function Resize-Bitmap {
    param([System.Drawing.Bitmap]$Source, [int]$Size)
    $bmp = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.DrawImage($Source, 0, 0, $Size, $Size)
    $g.Dispose()
    return $bmp
}

# 传统 DIB 帧（BITMAPINFOHEADER + BGRA 像素 + AND 掩码），.NET 的 Icon 类只认这种
function Get-DibBytes {
    param([System.Drawing.Bitmap]$Source, [int]$Size)
    $bmp = Resize-Bitmap -Source $Source -Size $Size
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)

    $bw.Write([UInt32]40)                  # biSize
    $bw.Write([Int32]$Size)                # biWidth
    $bw.Write([Int32]($Size * 2))          # biHeight = 2 倍（XOR + AND）
    $bw.Write([UInt16]1)                   # biPlanes
    $bw.Write([UInt16]32)                  # biBitCount
    $bw.Write([UInt32]0)                   # BI_RGB
    $bw.Write([UInt32]($Size * $Size * 4)) # biSizeImage
    $bw.Write([Int32]0); $bw.Write([Int32]0)
    $bw.Write([UInt32]0); $bw.Write([UInt32]0)

    for ($y = $Size - 1; $y -ge 0; $y--) {           # 像素自下而上
        for ($x = 0; $x -lt $Size; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $bw.Write([byte]$c.B); $bw.Write([byte]$c.G)
            $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
        }
    }

    $andRow = [int]([math]::Ceiling($Size / 32.0) * 4)   # 1bpp，每行 4 字节对齐
    $bw.Write((New-Object byte[] ($andRow * $Size)))     # 全 0：透明由 alpha 决定

    $bw.Flush()
    $bmp.Dispose()
    $bytes = $ms.ToArray()
    $bw.Dispose(); $ms.Dispose()
    return , $bytes
}

# PNG 帧（大尺寸用，Windows Vista 以上都支持）
function Get-PngBytes {
    param([System.Drawing.Bitmap]$Source, [int]$Size)
    $bmp = Resize-Bitmap -Source $Source -Size $Size
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $bytes = $ms.ToArray()
    $ms.Dispose()
    return , $bytes
}

# ---------------------------------------------------------------------------
#  组装 ICO
# ---------------------------------------------------------------------------
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$master = New-MasterBitmap
$frames = @()
foreach ($s in $sizes) {
    if ($s -le 64) { $frames += , (Get-DibBytes -Source $master -Size $s) }
    else { $frames += , (Get-PngBytes -Source $master -Size $s) }
}
$master.Dispose()

foreach ($frame in $frames) {
    if ($null -eq $frame -or $frame.Length -lt 100) { throw "生成的图标帧为空，制作失败。" }
}

$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
$bw.Write([UInt16]0)                 # reserved
$bw.Write([UInt16]1)                 # type = icon
$bw.Write([UInt16]$sizes.Count)

$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $data = $frames[$i]
    if ($s -ge 256) { $dim = 0 } else { $dim = $s }     # 0 表示 256
    $bw.Write([byte]$dim)
    $bw.Write([byte]$dim)
    $bw.Write([byte]0)                                  # 调色板
    $bw.Write([byte]0)                                  # 保留
    $bw.Write([UInt16]1)                                # 色彩平面
    $bw.Write([UInt16]32)                               # 位深
    $bw.Write([UInt32]$data.Length)
    $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($data in $frames) { $bw.Write($data) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($target, $ms.ToArray())
$bw.Dispose(); $ms.Dispose()

# ---------------------------------------------------------------------------
#  顺便导出一张 PNG 预览图给 README 用（ICO 在网页里显示不出来）
# ---------------------------------------------------------------------------
$docsDir = Join-Path $root "docs"
New-Item -ItemType Directory -Force -Path $docsDir | Out-Null
$preview = Join-Path $docsDir "icon.png"
[System.IO.File]::WriteAllBytes($preview, $frames[$sizes.Count - 1])   # 最后一帧就是 256 PNG
Write-Host ("预览图已导出：{0}" -f $preview)

# ---------------------------------------------------------------------------
#  自检：把 32x32 帧读回来，确认中间确实是白色 H、底是蓝的
# ---------------------------------------------------------------------------
$check = New-Object System.Drawing.Icon($target, 32, 32)
$checkBmp = $check.ToBitmap()
$whiteCount = 0
$blueCount = 0
for ($y = 0; $y -lt $checkBmp.Height; $y++) {
    for ($x = 0; $x -lt $checkBmp.Width; $x++) {
        $c = $checkBmp.GetPixel($x, $y)
        if ($c.R -gt 200 -and $c.G -gt 200 -and $c.B -gt 200) { $whiteCount++ }
        elseif ($c.B -gt $c.R + 20) { $blueCount++ }
    }
}
$total = $checkBmp.Width * $checkBmp.Height
$checkBmp.Dispose(); $check.Dispose()

$whitePct = $whiteCount / $total
Write-Host ("图标已生成：{0}" -f $target)
Write-Host ("  大小：{0} 字节，尺寸：{1}" -f (Get-Item $target).Length, ($sizes -join "/"))
Write-Host ("  32x32 帧自检：白色 H 像素 {0}（{1:P1}），蓝色底像素 {2}（{3:P1}）" -f $whiteCount, $whitePct, $blueCount, ($blueCount / $total))
if ($whitePct -lt 0.10) { throw "自检失败：图标里的白色 H 太小或没画出来。" }
