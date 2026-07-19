# Rebuild the editor's graphify knowledge graph from src/. One command:
#   ./refresh-graph.ps1
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

# Resolve the graphify Python interpreter (cached, else via uv tool dir).
$pyFile = "graphify-out/.graphify_python"
$py = if (Test-Path $pyFile) { Get-Content $pyFile } else { $null }
if (-not $py -or -not (Test-Path $py)) {
    $uvDir = (uv tool dir 2>$null).Trim()
    $py = Join-Path $uvDir "graphifyy\Scripts\python.exe"
    if (-not (Test-Path $py)) { throw "graphify not found. Install: uv tool install graphifyy" }
    New-Item -ItemType Directory -Force graphify-out | Out-Null
    $py | Out-File -FilePath $pyFile -Encoding utf8 -NoNewline
}

& $py "$PSScriptRoot/tools/refresh_graph.py"
if ($LASTEXITCODE -ne 0) { throw "graph build failed" }

& graphify export html | Select-Object -Last 1
Write-Host "Graph refreshed -> graphify-out/graph.html (query: graphify explain `"<Class>`")"
