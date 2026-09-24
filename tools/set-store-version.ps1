param(
    [Parameter(Mandatory=$true)][string]$Version,
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+\.0$') { throw 'Expected a four-part Store version ending in .0' }
$parts = $Version.Split('.') | ForEach-Object { [int]$_ }
if ($parts[0] -lt 1 -or ($parts | Where-Object { $_ -gt 65535 }).Count -gt 0) {
    throw 'Version components must fit the MSIX version range.'
}
$manifestPath = Join-Path $ProjectRoot 'InterviewCopilotPackager\Package.appxmanifest'
$projectPath = Join-Path $ProjectRoot 'InterviewCopilot.csproj'
[xml]$manifest = [IO.File]::ReadAllText($manifestPath)
[xml]$project = [IO.File]::ReadAllText($projectPath)
$identity = $manifest.SelectSingleNode('/*[local-name()="Package"]/*[local-name()="Identity"]')
if ($null -eq $identity) { throw 'Package identity is missing.' }
$identity.SetAttribute('Version', $Version)
foreach ($name in @('Version', 'AssemblyVersion', 'FileVersion')) {
    $nodes = $project.SelectNodes("/Project/PropertyGroup/$name")
    if ($nodes.Count -eq 0) { throw "Project is missing $name" }
    foreach ($node in $nodes) { $node.InnerText = $Version }
}
# Only Identity.Version changes in the manifest; MinVersion and all other
# version-bearing attributes must remain unchanged.
$manifest.Save($manifestPath)
$project.Save($projectPath)
