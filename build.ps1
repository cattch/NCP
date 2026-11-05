param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.8이 필요합니다.' }
$version = [IO.File]::ReadAllText((Join-Path $taskRoot 'VERSION')).Trim()
if ($version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') { throw 'VERSION은 1.0.0 형식이어야 합니다.' }
$parts = $version.Split('.') | ForEach-Object { [int]$_ }
if (@($parts | Where-Object { $_ -gt 65534 }).Count) { throw '버전의 각 숫자는 65534 이하여야 합니다.' }
$assemblyVersion = $version + '.0'
$outDir = if ($OutputDirectory) { [System.IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $taskRoot 'build' }
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $taskRoot 'src') -Filter '*.cs' | ForEach-Object FullName)
$versionSource = Join-Path $outDir 'Ncp.Version.g.cs'
[IO.File]::WriteAllLines($versionSource,@(
    'using System.Reflection;'
    '[assembly: AssemblyTitle("NVIDIA Color Preset")]'
    '[assembly: AssemblyProduct("NVIDIA Color Preset")]'
    ('[assembly: AssemblyVersion("' + $assemblyVersion + '")]')
    ('[assembly: AssemblyFileVersion("' + $assemblyVersion + '")]')
    ('[assembly: AssemblyInformationalVersion("' + $version + '")]')
),[Text.UTF8Encoding]::new($false))
$sourceFiles += $versionSource
[xml]$manifest = [IO.File]::ReadAllText((Join-Path $taskRoot 'app.manifest'))
$manifest.SelectSingleNode("/*[local-name()='assembly']/*[local-name()='assemblyIdentity']").SetAttribute('version',$assemblyVersion)
$versionManifest = Join-Path $outDir 'Ncp.Version.manifest'
$manifest.Save($versionManifest)
$appIcon = Join-Path $taskRoot 'assets\NCP.ico'
if (-not (Test-Path -LiteralPath $appIcon)) { throw 'NCP 아이콘 파일을 찾을 수 없습니다.' }
$defaultProfile = Join-Path $taskRoot 'assets\default-profile.ncpprofile'
if (-not (Test-Path -LiteralPath $defaultProfile)) { throw '기본 프로필 파일을 찾을 수 없습니다.' }
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /unsafe /utf8output /codepage:65001 /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Runtime.Serialization.dll /r:System.Xml.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll "/win32manifest:$versionManifest" "/win32icon:$appIcon" "/resource:$appIcon,Ncp.AppIcon" "/resource:$defaultProfile,Ncp.DefaultProfile" /out:"$outDir\NCP.exe" $sourceFiles
if ($LASTEXITCODE -ne 0) { throw 'NCP 빌드 실패' }
Copy-Item -LiteralPath (Join-Path $taskRoot 'NCP.exe.config') -Destination $outDir
Write-Output "Build complete: NCP $version - $outDir\NCP.exe"
