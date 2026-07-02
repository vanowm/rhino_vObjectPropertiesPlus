Set-Location d:\github\rhino\vObjectPropertiesPlus

$pendingFile = '.git\vObjectPropertiesPlus-pending-message.txt'

# Auto-generate pending message from diff when not provided
if (-not (Test-Path $pendingFile)) {
    $changes = @()
    git diff --name-status -- . | ForEach-Object {
        $parts = $_ -split '\t+', 2
        if ($parts.Count -ge 2) {
            $changes += [pscustomobject]@{ Status = $parts[0]; Path = $parts[1] }
        }
    }

    $hasViews    = $false
    $hasCommands = $false
    $hasReadme   = $false

    foreach ($c in $changes) {
        $p = ($c.Path -replace '\\','/')
        if ($p -eq 'README.md')           { $hasReadme   = $true }
        if ($p -like 'Views/*.cs')        { $hasViews    = $true }
        if ($p -like 'Commands/*.cs')     { $hasCommands = $true }
    }

    $parts = New-Object System.Collections.Generic.List[string]
    if ($hasViews)    { $parts.Add('panel: update') }
    if ($hasCommands) { $parts.Add('commands: update') }
    if ($hasReadme)   { $parts.Add('docs: refresh notes') }
    if ($parts.Count -eq 0) { $parts.Add('maintenance: apply project updates') }

    $summary = ($parts -join '; ')
    Set-Content -Path $pendingFile -Value $summary -NoNewline -Encoding utf8
    Write-Host "Created pending message file: $pendingFile -> $summary" -ForegroundColor Green
}

dotnet build vObjectPropertiesPlus.csproj -c Release --no-incremental
