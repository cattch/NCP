param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$version = [IO.File]::ReadAllText((Join-Path $taskRoot 'VERSION')).Trim()
$output = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $taskRoot 'artifacts' }
$staging = Join-Path $taskRoot ('build\release-' + [Guid]::NewGuid().ToString('N'))
$runtime = Join-Path $staging 'runtime'
$source = Join-Path $staging 'source'
New-Item -ItemType Directory -Force -Path $output,$runtime,$source | Out-Null
& (Join-Path $taskRoot 'build.ps1') -OutputDirectory $runtime
$name = 'NVIDIA Color Preset-v' + $version
Copy-Item -LiteralPath (Join-Path $taskRoot 'LICENSE.txt') -Destination $runtime
$files = @('.gitignore','README.md','VERSION','build.ps1','release.ps1','app.manifest','NCP.exe.config','LICENSE.txt',
    'assets\NCP.ico','assets\NCP.svg','assets\default-profile.ncpprofile')
$files += Get-ChildItem -LiteralPath (Join-Path $taskRoot 'src') -Filter '*.cs' | ForEach-Object { 'src\' + $_.Name }
foreach ($file in $files) {
    $destination = Join-Path $source $file
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
    Copy-Item -LiteralPath (Join-Path $taskRoot $file) -Destination $destination
}
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Write-Archive($path,$directory,$entries) {
    $temporary = Join-Path $staging ([Guid]::NewGuid().ToString('N') + '.zip')
    $zip = [IO.Compression.ZipFile]::Open($temporary,[IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($entry in $entries) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,(Join-Path $directory $entry),
                $entry.Replace('\','/'),[IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $zip.Dispose() }
    Move-Item -LiteralPath $temporary -Destination $path -Force
}
$binaryArchive = Join-Path $output ($name + '.zip')
$sourceArchive = Join-Path $output ($name + '-source.zip')
Write-Archive $binaryArchive $runtime @('NCP.exe','NCP.exe.config','LICENSE.txt')
Write-Archive $sourceArchive $source $files
Write-Output "Executable: $binaryArchive"
Write-Output "GitHub source: $sourceArchive"
