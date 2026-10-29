# Build bin\ExplorerSession.exe with the C# compiler that ships with Windows (.NET Framework 4.x). No SDK needed.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
$bin = Join-Path $root 'bin'
New-Item -ItemType Directory -Force $bin | Out-Null

$refs = 'System.dll', 'System.Core.dll', 'Microsoft.CSharp.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll',
        'System.Web.Extensions.dll', 'WindowsBase.dll', 'System.Management.dll', 'UIAutomationClient.dll', 'UIAutomationTypes.dll'
$cscArgs = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/codepage:65001',
          "/lib:$fw", "/lib:$(Join-Path $fw 'WPF')",
          "/out:$(Join-Path $bin 'ExplorerSession.exe')") +
        ($refs | ForEach-Object { "/r:$_" }) +
        (Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName })
& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "csc failed ($LASTEXITCODE)" }
Write-Host "built $(Join-Path $bin 'ExplorerSession.exe')"
