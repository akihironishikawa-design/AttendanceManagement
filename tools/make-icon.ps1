# =====================================================================
#  アプリのアイコン(src\TakaneAttendance.Wpf\app.ico)を作り直す
#
#  図案 : 青いタイル + 白い時計(勤怠=時刻) + 緑のチェック(突合=確認済み)
#  配色 : App.xaml のパレットに合わせる(Blue700 #1E5AA8 / Blue900 #0F2F5C)
#
#  小さいサイズでも潰れないよう、縮小ではなく各サイズを直接描いている。
#  16〜128px は BMP、256px は PNG として1つの .ico にまとめる
#  (256px を BMP にすると 256KB を超えて古いシェルで表示できないため)。
#
#  使い方 :  powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
# =====================================================================

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$OutPath = Join-Path $PSScriptRoot "..\src\TakaneAttendance.Wpf\app.ico"
$Sizes   = @(16, 24, 32, 48, 64, 128, 256)

# ---- 配色 ----
$TileTop    = [System.Drawing.ColorTranslator]::FromHtml("#2A6FC4")   # Blue700 の明るめ
$TileBottom = [System.Drawing.ColorTranslator]::FromHtml("#0F2F5C")   # Blue900
$FaceColor  = [System.Drawing.ColorTranslator]::FromHtml("#FFFFFF")
$HandColor  = [System.Drawing.ColorTranslator]::FromHtml("#0F2F5C")
$CheckBg    = [System.Drawing.ColorTranslator]::FromHtml("#2E9E5B")

function New-RoundedPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x,          $y,          $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y,          $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d,   0, 90)
    $path.AddArc($x,          $y + $h - $d, $d, $d,  90, 90)
    $path.CloseFigure()
    return $path
}

# 1サイズ分を描く。座標はすべて $s(辺の長さ)に対する比で置き、拡大縮小しても崩れないようにする。
function New-IconBitmap([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # ---- 背景のタイル ----
    $margin = [single]($s * 0.03)
    $side   = [single]($s - $margin * 2)
    $tile   = New-RoundedPath $margin $margin $side $side ([single]($s * 0.18))
    # ※ New-Object Type(a, b + c) は「+」より「,」が優先され引数が増えてしまうため、
    #    足し算はいったん変数に入れてから渡す
    $tileBottomY = [single]($margin + $side)
    $topPoint    = New-Object System.Drawing.PointF -ArgumentList ([single]0), $margin
    $bottomPoint = New-Object System.Drawing.PointF -ArgumentList ([single]0), $tileBottomY
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush -ArgumentList `
        $topPoint, $bottomPoint, $TileTop, $TileBottom
    $g.FillPath($brush, $tile)
    $brush.Dispose(); $tile.Dispose()

    # ---- 時計の文字盤 ----
    $cx = [single]($s * 0.425); $cy = [single]($s * 0.415); $r = [single]($s * 0.250)
    $face = New-Object System.Drawing.SolidBrush($FaceColor)
    $g.FillEllipse($face, $cx - $r, $cy - $r, $r * 2, $r * 2)
    $face.Dispose()

    # 目盛り(12/3/6/9)。小さいサイズでは点になって潰れるため描かない
    if ($s -ge 48) {
        $tick = New-Object System.Drawing.Pen($HandColor, [single]($s * 0.030))
        foreach ($deg in 0, 90, 180, 270) {
            $rad = [Math]::PI * $deg / 180.0
            $dx = [Math]::Sin($rad); $dy = (0 - [Math]::Cos($rad))
            $g.DrawLine($tick,
                [single]($cx + $dx * $r * 0.80), [single]($cy + $dy * $r * 0.80),
                [single]($cx + $dx * $r * 0.94), [single]($cy + $dy * $r * 0.94))
        }
        $tick.Dispose()
    }

    # ---- 針(8:00 = 始業時刻) ----
    $hand = New-Object System.Drawing.Pen($HandColor, [single]($s * 0.062))
    $hand.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $hand.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round

    $g.DrawLine($hand, $cx, $cy, $cx, [single]($cy - $r * 0.72))          # 長針 → 12
    $rad = [Math]::PI * 240 / 180.0                                        # 短針 → 8
    $g.DrawLine($hand, $cx, $cy,
        [single]($cx + [Math]::Sin($rad) * $r * 0.48),
        [single]($cy - [Math]::Cos($rad) * $r * 0.48))
    $hand.Dispose()

    # ---- 突合済みを示すチェック ----
    $bx = [single]($s * 0.720); $by = [single]($s * 0.715); $br = [single]($s * 0.215)

    # タイルとの境目が分かるよう、白のふちを付ける
    $ring = New-Object System.Drawing.Pen($FaceColor, [single]($s * 0.055))
    $bg   = New-Object System.Drawing.SolidBrush($CheckBg)
    $g.FillEllipse($bg, $bx - $br, $by - $br, $br * 2, $br * 2)
    $g.DrawEllipse($ring, $bx - $br, $by - $br, $br * 2, $br * 2)
    $bg.Dispose(); $ring.Dispose()

    $check = New-Object System.Drawing.Pen($FaceColor, [single]($s * 0.062))
    $check.StartCap  = [System.Drawing.Drawing2D.LineCap]::Round
    $check.EndCap    = [System.Drawing.Drawing2D.LineCap]::Round
    $check.LineJoin  = [System.Drawing.Drawing2D.LineJoin]::Round
    $points = @(
        (New-Object System.Drawing.PointF([single]($bx - $br * 0.46), [single]($by + $br * 0.02))),
        (New-Object System.Drawing.PointF([single]($bx - $br * 0.12), [single]($by + $br * 0.36))),
        (New-Object System.Drawing.PointF([single]($bx + $br * 0.48), [single]($by - $br * 0.34)))
    )
    $g.DrawLines($check, [System.Drawing.PointF[]]$points)
    $check.Dispose()

    $g.Dispose()
    return $bmp
}

# ICO に埋め込む BMP(DIB)。上下反転で並べ、末尾に AND マスク(未使用)を付ける決まり。
function ConvertTo-IconDib([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
                          [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $rowBytes = $w * 4
        $pixels = New-Object byte[] ($rowBytes * $h)
        for ($y = 0; $y -lt $h; $y++) {
            # DIB は下から上に並ぶため、最終行から詰める
            $src = [IntPtr]::Add($data.Scan0, $data.Stride * ($h - 1 - $y))
            [System.Runtime.InteropServices.Marshal]::Copy($src, $pixels, $rowBytes * $y, $rowBytes)
        }
    }
    finally { $bmp.UnlockBits($data) }

    $maskRow  = [int][Math]::Ceiling($w / 32.0) * 4     # 1bpp・4バイト境界
    $maskSize = $maskRow * $h

    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter($stream)
    $writer.Write([uint32]40)                 # biSize
    $writer.Write([int32]$w)                  # biWidth
    $writer.Write([int32]($h * 2))            # biHeight (XOR + AND)
    $writer.Write([uint16]1)                  # biPlanes
    $writer.Write([uint16]32)                 # biBitCount
    $writer.Write([uint32]0)                  # biCompression = BI_RGB
    $writer.Write([uint32]($pixels.Length + $maskSize))
    $writer.Write([int32]0); $writer.Write([int32]0)    # 解像度(未使用)
    $writer.Write([uint32]0); $writer.Write([uint32]0)  # 使用色数(未使用)
    $writer.Write($pixels)
    $writer.Write((New-Object byte[] $maskSize))        # アルファを使うため全て 0
    $writer.Flush()
    $bytes = $stream.ToArray()
    $writer.Dispose(); $stream.Dispose()
    return ,[byte[]]$bytes      # 先頭の「,」が無いと配列が1要素ずつばらけて返る
}

function ConvertTo-PngBytes([System.Drawing.Bitmap]$bmp) {
    $stream = New-Object System.IO.MemoryStream
    $bmp.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $stream.ToArray()
    $stream.Dispose()
    return ,[byte[]]$bytes
}

# ---- 各サイズを描いて .ico にまとめる ----
$entries = @()
foreach ($size in $Sizes) {
    $bmp = New-IconBitmap $size
    if ($size -ge 256) { $bytes = ConvertTo-PngBytes $bmp } else { $bytes = ConvertTo-IconDib $bmp }
    $bmp.Dispose()
    $entries += [pscustomobject]@{ Size = $size; Bytes = $bytes }
    Write-Host ("  {0,3}px  {1,7:N0} bytes" -f $size, $bytes.Length)
}

$stream = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($stream)
$writer.Write([uint16]0)                    # 予約
$writer.Write([uint16]1)                    # 種別 = アイコン
$writer.Write([uint16]$entries.Count)

$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
    if ($e.Size -ge 256) { $dim = [byte]0 } else { $dim = [byte]$e.Size }   # 256 は 0 で表す
    $writer.Write($dim); $writer.Write($dim)
    $writer.Write([byte]0)                  # パレット数
    $writer.Write([byte]0)                  # 予約
    $writer.Write([uint16]1)                # プレーン数
    $writer.Write([uint16]32)               # ビット数
    $writer.Write([uint32]$e.Bytes.Length)
    $writer.Write([uint32]$offset)
    $offset += $e.Bytes.Length
}
foreach ($e in $entries) { $writer.Write([byte[]]$e.Bytes) }
$writer.Flush()

$full = [System.IO.Path]::GetFullPath($OutPath)
[System.IO.File]::WriteAllBytes($full, $stream.ToArray())
$writer.Dispose(); $stream.Dispose()

Write-Host ""
Write-Host ("作成しました: {0} ({1:N0} bytes / {2} サイズ)" -f $full, (Get-Item $full).Length, $entries.Count)
