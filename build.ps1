# Builds K631Live.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
$ErrorActionPreference = 'Stop'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:winexe /optimize+ `
    /out:"$PSScriptRoot\K631Live.exe" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll `
    "$PSScriptRoot\src\*.cs"
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Write-Output "Built $PSScriptRoot\K631Live.exe"
