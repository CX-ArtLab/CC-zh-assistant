$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourceRoot = Join-Path $projectRoot "src"
$outputRoot = Join-Path $projectRoot "dist"
$compiler = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$outputName = "CCZhAssistant.exe"
$outputPath = Join-Path $outputRoot $outputName
$zipName = "CCZhAssistant-windows.zip"
$zipPath = Join-Path $outputRoot $zipName

if (-not (Test-Path -LiteralPath $compiler)) {
    throw "The .NET Framework C# compiler was not found: $compiler"
}

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

Write-Host "Compiling Claude Code 桌面版中文助手 (CCZhAssistant)..." -ForegroundColor Cyan

& $compiler `
    /nologo `
    /target:winexe `
    /platform:anycpu `
    /optimize+ `
    "/win32icon:$sourceRoot\Assets\assistant-icon.ico" `
    "/win32manifest:$sourceRoot\app.manifest" `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    /reference:System.Net.Http.dll `
    /reference:System.Web.Extensions.dll `
    "/resource:$projectRoot\translation\translation-pack.json,BundledTranslationPack" `
    "/resource:$projectRoot\translation\manifest.json,BundledTranslationManifest" `
    "/resource:$sourceRoot\Assets\assistant-icon.ico,AssistantIcon" `
    "/resource:$sourceRoot\Assets\assistant-icon.png,AssistantIconPng" `
    "/resource:$sourceRoot\Assets\translator.js,TranslatorJs" `
    "/out:$outputPath" `
    "$sourceRoot\Program.cs"

if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed with exit code $LASTEXITCODE"
}

Write-Host "Build successfully created: $outputPath" -ForegroundColor Green
Get-Item -LiteralPath $outputPath | Select-Object Name, Length, LastWriteTime

# Package portable ZIP (matching Antigravity Zh Assistant release style)
Write-Host "Packaging portable release ZIP..." -ForegroundColor Cyan
$stage = Join-Path $outputRoot "package_stage"
Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $stage -Force | Out-Null
Copy-Item -LiteralPath $outputPath -Destination $stage
if (Test-Path -LiteralPath (Join-Path $projectRoot "README.md")) {
    Copy-Item -LiteralPath (Join-Path $projectRoot "README.md") -Destination $stage
}
if (Test-Path -LiteralPath (Join-Path $projectRoot "LICENSE")) {
    Copy-Item -LiteralPath (Join-Path $projectRoot "LICENSE") -Destination $stage
}
Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zipPath -Force
Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "Package successfully created: $zipPath" -ForegroundColor Green
Get-Item -LiteralPath $zipPath | Select-Object Name, Length, LastWriteTime
