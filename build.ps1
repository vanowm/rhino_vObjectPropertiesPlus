Set-Location d:\github\rhino\vObjectPropertiesPlus

# Bump CalVer in csproj and AssemblyInfo
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

# Build a non-canned message: use pending message file if present, else list changed source file basenames
$pendingFile = '.git\vObjectPropertiesPlus-pending-message.txt'
$desc = if (Test-Path $pendingFile) {
    $m = (Get-Content $pendingFile -Raw).Trim()
    Remove-Item $pendingFile
    $m
} else {
    $src = git diff --cached --name-only |
        Where-Object { $_ -notmatch '^bin/' -and
                       $_ -ne 'vObjectPropertiesPlus.csproj' -and
                       $_ -ne 'Properties/AssemblyInfo.cs' }
    if ($src) {
        ($src | ForEach-Object { Split-Path $_ -Leaf }) -join ', '
    } else {
        'version bump'
    }
}

git commit -m "$v $desc"
