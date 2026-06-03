Set-Location d:\github\rhino\vObjectPropertiesPlus

# Bump CalVer in csproj and AssemblyInfo
# Micro version (no leading zeros on M/D): YY.M.D.Hmm - used for ALL fields per memory rules
$cur = (Select-String -Path vObjectPropertiesPlus.csproj -Pattern '<Version>([^<]+)').Matches[0].Groups[1].Value
$v   = Get-Date -Format 'yy.M.d.Hmm'
(Get-Content vObjectPropertiesPlus.csproj)   -replace [regex]::Escape($cur), $v | Set-Content vObjectPropertiesPlus.csproj
(Get-Content Properties\AssemblyInfo.cs) -replace [regex]::Escape($cur), $v | Set-Content Properties\AssemblyInfo.cs

# Build
dotnet build vObjectPropertiesPlus.csproj -c Release --no-incremental
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Stage everything including the DLL
git add -A
git add -f bin\Release\net7.0-windows\vObjectPropertiesPlus.dll

# Build commit message: use pending message file (REQUIRED), else ABORT
$pendingFile = '.git\vObjectPropertiesPlus-pending-message.txt'
if (Test-Path $pendingFile) {
    $desc = (Get-Content $pendingFile -Raw).Trim()
    Remove-Item $pendingFile
    git commit -m "$v $desc"
} else {
    Write-Host "ERROR: Missing $pendingFile - commit message required. See memory rules." -ForegroundColor Red
    exit 1
}
