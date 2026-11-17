[CmdletBinding()]
param(
    [switch]$CoreOnly,
    [switch]$Tests,
    [switch]$Package
)

$ErrorActionPreference = 'Stop'
$projectDir = $PSScriptRoot
$buildDir = Join-Path $projectDir 'build'
$distDir = Join-Path $projectDir 'dist'
$coreDir = Join-Path $projectDir 'src\core'
$uiDir = Join-Path $projectDir 'src\ui'
$testDir = Join-Path $projectDir 'Tests'
New-Item -ItemType Directory -Force -Path $buildDir, $distDir | Out-Null

# --- Visual C++ 环境 -------------------------------------------------------
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw '未找到 Visual Studio Build Tools。' }
$vsRoot = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsRoot) { throw '未安装 Visual C++ x64 构建工具。' }
$vcvars = Join-Path $vsRoot 'VC/Auxiliary/Build/vcvars64.bat'
$envLines = & cmd.exe /d /s /c "call `"$vcvars`" >nul && set"
if ($LASTEXITCODE -ne 0) { throw '无法初始化编译环境。' }
foreach ($line in $envLines) {
    if ($line -match '^([^=]+)=(.*)$') { [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2], 'Process') }
}

$common = @('/nologo', '/utf-8', '/std:c11', '/MT', '/O2', '/W4', '/WX',
    '/DUNICODE', '/D_UNICODE', '/D_WIN32_WINNT=0x0A00', '/DWINVER=0x0A00', "/Fo$buildDir/")

# 上一次截图或联调留下的实例会锁住 dist 里的 exe/dll，先关掉再编译。
$stale = @(Get-Process -Name 'QJAR工具' -ErrorAction SilentlyContinue)
if ($stale.Count -gt 0) {
    Write-Warning ("检测到 QJAR工具 正在运行（PID " + (($stale | ForEach-Object { $_.Id }) -join ', ') + "），已结束它再构建。")
    $stale | Stop-Process -Force
    Start-Sleep -Milliseconds 400
}

Push-Location $projectDir
try {
    # --- C 核心 -> QJCore.dll ---------------------------------------------
    & cl.exe @common /LD "$coreDir\Controller.c" "$coreDir\Pendant.c" `
        "/Fe$distDir\QJCore.dll" `
        /link /DEF:"$coreDir\qjcore.def" /IMPLIB:"$buildDir\QJCore.lib" `
        ws2_32.lib bcrypt.lib user32.lib
    if ($LASTEXITCODE -ne 0) { throw 'QJCore.dll 编译失败。' }
    Write-Host "已生成：$distDir\QJCore.dll"

    if ($CoreOnly) { return }

    # --- C# 外壳 -> QJAR工具.exe ------------------------------------------
    $compiler = Join-Path $vsRoot 'MSBuild\Current\Bin\Roslyn\csc.exe'
    if (-not (Test-Path -LiteralPath $compiler)) { throw '未找到 Roslyn C# 编译器。' }
    $sources = @(Get-ChildItem -LiteralPath $uiDir -Filter *.cs | ForEach-Object { $_.FullName })
    & $compiler /nologo /codepage:65001 /langversion:latest /target:winexe /platform:x64 /optimize+ `
        "/out:$distDir\QJAR工具.exe" `
        /reference:System.Windows.Forms.dll /reference:System.Drawing.dll `
        /win32manifest:"$uiDir\app.manifest" `
        /win32icon:"$projectDir\Assets\registration-tool.ico" `
        /resource:"$projectDir\Assets\QJToolDisplay.otf,QJToolDisplay.otf" `
        /resource:"$projectDir\Assets\registration-tool.ico,registration-tool.ico" `
        $sources
    if ($LASTEXITCODE -ne 0) { throw 'QJAR工具.exe 编译失败。' }
    Copy-Item -LiteralPath (Join-Path $uiDir 'QJAR.exe.config') -Destination "$distDir\QJAR工具.exe.config" -Force
    Write-Host "已生成：$distDir\QJAR工具.exe"

    # --- 测试程序 ----------------------------------------------------------
    if ($Tests) {
        $appSources = @(Get-ChildItem -LiteralPath $uiDir -Filter *.cs |
            Where-Object { $_.Name -ne 'Program.cs' } | ForEach-Object { $_.FullName })
        & $compiler /nologo /codepage:65001 /langversion:latest /target:exe /platform:x64 `
            "/out:$buildDir\UiCheck.exe" `
            /reference:System.Windows.Forms.dll /reference:System.Drawing.dll `
            (Join-Path $testDir 'UiHarness.cs') (Join-Path $testDir 'MockController.cs') $appSources
        if ($LASTEXITCODE -ne 0) { throw '界面回归程序编译失败。' }

        & $compiler /nologo /codepage:65001 /langversion:latest /target:exe /platform:x64 `
            "/out:$buildDir\Smoke.exe" `
            (Join-Path $testDir 'Smoke.cs') (Join-Path $testDir 'MockController.cs')
        if ($LASTEXITCODE -ne 0) { throw '通信冒烟测试编译失败。' }

        & cl.exe @common (Join-Path $testDir 'FakeSsh.c') "/Fe$buildDir\FakeSsh.exe" /link /SUBSYSTEM:CONSOLE
        if ($LASTEXITCODE -ne 0) { throw 'SSH 测试替身编译失败。' }

        Copy-Item -LiteralPath "$distDir\QJCore.dll" -Destination $buildDir -Force
        & "$buildDir\Smoke.exe"
        if ($LASTEXITCODE -ne 0) { throw '通信冒烟测试失败。' }
        & "$buildDir\UiCheck.exe"
        if ($LASTEXITCODE -ne 0) { throw '界面回归失败。' }

        & $compiler /nologo /codepage:65001 /langversion:latest /target:exe /platform:x64 `
            "/out:$buildDir\RestoreCheck.exe" /reference:System.Drawing.dll /reference:System.Windows.Forms.dll `
            (Join-Path $testDir 'RestoreCheck.cs')
        if ($LASTEXITCODE -ne 0) { throw '恢复首帧检查编译失败。' }
        foreach ($page in @('register', 'command')) {
            & "$buildDir\RestoreCheck.exe" "$distDir\QJAR工具.exe" "$buildDir\restore-$page" 5 $page
            if ($LASTEXITCODE -ne 0) { throw "恢复首帧检查失败：$page" }
        }
    }

    # --- 打包 --------------------------------------------------------------
    if ($Package) {
        $version = '1.1.14'
        $name = "QJAR工具-v$version"
        $stage = Join-Path $distDir $name
        $zip = Join-Path $distDir "$name.zip"
        if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
        if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
        New-Item -ItemType Directory -Path $stage | Out-Null
        Copy-Item -LiteralPath "$distDir\QJAR工具.exe", "$distDir\QJCore.dll", "$distDir\QJAR工具.exe.config" -Destination $stage
        Copy-Item -LiteralPath (Join-Path $projectDir 'Docs\使用说明.txt') -Destination $stage
        $exeHash = (Get-FileHash -LiteralPath (Join-Path $stage 'QJAR工具.exe') -Algorithm SHA256).Hash
        $dllHash = (Get-FileHash -LiteralPath (Join-Path $stage 'QJCore.dll') -Algorithm SHA256).Hash
        @("SHA256 (QJAR工具.exe) = $exeHash", "SHA256 (QJCore.dll) = $dllHash") |
            Set-Content -LiteralPath (Join-Path $stage 'SHA256.txt') -Encoding utf8
        Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal
        Write-Host "已打包：$zip"
    }
}
finally {
    Pop-Location
}