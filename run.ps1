<#
.SYNOPSIS
  勤怠突合 PoC の起動用スクリプト。

.DESCRIPTION
  ソリューション直下で `dotnet run` を実行すると、実行できるプロジェクトが
  TakaneAttendance.Wpf と TakaneAttendance.Cli の2つあるため対象を決められず失敗する。
  通常は WPF 画面を起動したいので、この入口を用意している。

.EXAMPLE
  .\run.ps1
  WPF 画面を Debug で起動する。

.EXAMPLE
  .\run.ps1 -Release
  Release で起動する。

.EXAMPLE
  .\run.ps1 -Cli sheets .\sample\シフト表サンプル.xls
  確認用コンソール(画面なし)を実行する。-Cli の後ろの引数はそのまま渡される。
#>
param(
    # 確認用コンソール(TakaneAttendance.Cli)を実行する
    [switch]$Cli,
    # Release 構成で実行する(既定は Debug)
    [switch]$Release
)

$ErrorActionPreference = 'Stop'

$configuration = if ($Release) { 'Release' } else { 'Debug' }
$projectPath = if ($Cli) { 'src\TakaneAttendance.Cli' } else { 'src\TakaneAttendance.Wpf' }
$project = Join-Path $PSScriptRoot $projectPath

Write-Host "dotnet run --project $projectPath -c $configuration" -ForegroundColor DarkGray

# 残りの引数($args)はアプリ側の引数としてそのまま渡す
if ($args.Count -gt 0) {
    dotnet run --project $project -c $configuration -- @args
}
else {
    dotnet run --project $project -c $configuration
}

exit $LASTEXITCODE

