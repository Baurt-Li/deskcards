$ErrorActionPreference = 'Stop'

$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (!(Test-Path -LiteralPath (Join-Path $framework 'csc.exe'))) {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$compiler = Join-Path $framework 'csc.exe'
if (!(Test-Path -LiteralPath $compiler)) {
    throw 'The .NET Framework C# compiler was not found.'
}

$wpf = Join-Path $framework 'WPF'
$source = Join-Path $PSScriptRoot 'src'
$dist = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Path $dist -Force | Out-Null
$binary = Join-Path $dist 'DeskCards.exe'
$arguments = @(
    '/nologo'
    '/codepage:65001'
    '/target:winexe'
    '/optimize+'
    "/out:$binary"
    "/win32icon:$source\DeskCards.ico"
    "/resource:$source\DeskCards.xaml,DeskCards.xaml"
    "/reference:$wpf\WindowsBase.dll"
    "/reference:$wpf\PresentationCore.dll"
    "/reference:$wpf\PresentationFramework.dll"
    "/reference:$wpf\UIAutomationTypes.dll"
    "/reference:$framework\System.Xaml.dll"
    "/reference:$framework\System.Windows.Forms.dll"
    "/reference:$framework\System.Drawing.dll"
    "$source\DeskCards.cs"
)
& $compiler @arguments
if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE."
}

Write-Output $binary
