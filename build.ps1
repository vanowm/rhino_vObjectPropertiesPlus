Set-Location d:\github\rhino\vObjectPropertiesPlus

# Bump CalVer in csproj and AssemblyInfo
$cur = (Select-String -Path vObjectPropertiesPlus.csproj -Pattern '<Version>([^<]+)').Matches[0].Groups[1].Value
$v   = (Get-Date).ToString('yy.M.d.HHmm')
(Get-Content vObjectPropertiesPlus.csproj)   -replace [regex]::Escape($cur), $v | Set-Content vObjectPropertiesPlus.csproj
(Get-Content Properties\AssemblyInfo.cs) -replace [regex]::Escape($cur), $v | Set-Content Properties\AssemblyInfo.cs

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

# Build
$dllPath = 'bin\Release\net7.0-windows\vObjectPropertiesPlus.dll'
$dllTimeBefore = if (Test-Path $dllPath) { (Get-Item $dllPath).LastWriteTime } else { $null }

$buildOutput = dotnet build vObjectPropertiesPlus.csproj -c Release --no-incremental 2>&1
$buildExitCode = $LASTEXITCODE
if ($buildExitCode -ne 0) {
    if ($buildOutput -match 'being used by another process' -or $buildOutput -match 'cannot access the file' -or $buildOutput -match 'Cannot write file') {
        Write-Host "WARNING: vObjectPropertiesPlus build reported a locked DLL; prebuild is considered successful and the pending commit message file has already been created." -ForegroundColor Yellow
    } else {
        Write-Host $buildOutput
        exit $buildExitCode
    }
}

# Commit only when build succeeded and DLL was actually updated
$dllTimeAfter = if (Test-Path $dllPath) { (Get-Item $dllPath).LastWriteTime } else { $null }
$dllUpdated = ($dllTimeAfter -ne $null) -and ($dllTimeAfter -ne $dllTimeBefore)

if ($dllUpdated) {
    $pendingMsg = (Get-Content $pendingFile -Raw -ErrorAction SilentlyContinue) -replace "`r`n|`r|`n", ' '
    if ($pendingMsg) {
        git add -A
        git add -f $dllPath
        git commit -m "${v}: $pendingMsg"
        Remove-Item $pendingFile -ErrorAction SilentlyContinue
        Write-Host "Committed: ${v}: $pendingMsg" -ForegroundColor Green
    }
} else {
    Write-Host "DLL not updated (build locked or unchanged) - commit deferred." -ForegroundColor Yellow
}
