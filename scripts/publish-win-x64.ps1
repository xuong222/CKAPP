[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

<#
.SYNOPSIS
验证目标目录位于仓库根目录内部。
.PARAMETER RootPath
仓库根目录的规范化绝对路径。
.PARAMETER CandidatePath
准备清理或写入的候选绝对路径。
#>
function Assert-PathWithinRoot
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$RootPath,

        [Parameter(Mandatory = $true)]
        [string]$CandidatePath
    )

    $normalizedRoot = [IO.Path]::GetFullPath($RootPath).TrimEnd('\') + '\'
    $normalizedCandidate = [IO.Path]::GetFullPath($CandidatePath)

    if (-not $normalizedCandidate.StartsWith(
        $normalizedRoot,
        [StringComparison]::OrdinalIgnoreCase))
    {
        throw "拒绝访问仓库外路径：$normalizedCandidate"
    }
}

<#
.SYNOPSIS
运行一条 dotnet 命令并在非零退出码时立即终止发布。
.PARAMETER Arguments
按独立参数传递给 dotnet CLI 的字符串数组。
.PARAMETER Description
显示在控制台中的当前发布阶段说明。
#>
function Invoke-DotNetCommand
{
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    Write-Host "`n[$Description] dotnet $($Arguments -join ' ')" -ForegroundColor Cyan
    & dotnet @Arguments

    if ($LASTEXITCODE -ne 0)
    {
        throw "$Description 失败，dotnet 退出码为 $LASTEXITCODE。"
    }
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$solutionPath = Join-Path $repositoryRoot "CH32UpperComputer.sln"
$projectPath = Join-Path $repositoryRoot "src\CH32UpperComputer.App\CH32UpperComputer.App.csproj"
$publishDirectory = Join-Path $repositoryRoot "artifacts\publish\win-x64"
$expectedExecutable = Join-Path $publishDirectory "CH32UpperComputer.App.exe"

if (-not (Test-Path -LiteralPath $solutionPath -PathType Leaf))
{
    throw "未找到解决方案：$solutionPath"
}

if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf))
{
    throw "未找到 WPF 发布项目：$projectPath"
}

Assert-PathWithinRoot -RootPath $repositoryRoot -CandidatePath $publishDirectory

Push-Location $repositoryRoot

try
{
    if (Test-Path -LiteralPath $publishDirectory)
    {
        Remove-Item -LiteralPath $publishDirectory -Recurse -Force
    }

    New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null

    Invoke-DotNetCommand -Description "锁定还原" -Arguments @(
        "restore",
        $solutionPath,
        "--locked-mode"
    )
    Invoke-DotNetCommand -Description "Release 全量测试" -Arguments @(
        "test",
        $solutionPath,
        "-c",
        "Release",
        "--no-restore"
    )
    Invoke-DotNetCommand -Description "Release 全量构建" -Arguments @(
        "build",
        $solutionPath,
        "-c",
        "Release",
        "--no-restore"
    )
    Invoke-DotNetCommand -Description "win-x64 自包含单文件发布" -Arguments @(
        "publish",
        $projectPath,
        "-c",
        "Release",
        "-r",
        "win-x64",
        "--self-contained",
        "true",
        "--no-restore",
        "-o",
        $publishDirectory,
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:EnableCompressionInSingleFile=true",
        "-p:PublishTrimmed=false",
        "-p:PublishDocumentationFiles=false",
        "-p:DebugType=embedded",
        "-p:InvariantGlobalization=false"
    )

    if (-not (Test-Path -LiteralPath $expectedExecutable -PathType Leaf))
    {
        throw "发布未生成预期 EXE：$expectedExecutable"
    }

    $publishedFiles = @(Get-ChildItem -LiteralPath $publishDirectory -File)
    $publishedExecutables = @($publishedFiles | Where-Object Extension -EQ ".exe")
    $unexpectedFiles = @($publishedFiles | Where-Object FullName -NE $expectedExecutable)

    if ($publishedExecutables.Count -ne 1)
    {
        throw "发布目录必须且只能包含一个 EXE；实际数量为 $($publishedExecutables.Count)。"
    }

    if ($unexpectedFiles.Count -gt 0)
    {
        $unexpectedNames = $unexpectedFiles.Name -join ", "
        throw "发布目录包含单文件 EXE 以外的文件：$unexpectedNames"
    }

    $hash = Get-FileHash -LiteralPath $expectedExecutable -Algorithm SHA256
    $fileInfo = Get-Item -LiteralPath $expectedExecutable
    Write-Host "`n发布成功" -ForegroundColor Green
    Write-Host "文件：$($fileInfo.FullName)"
    Write-Host "大小：$($fileInfo.Length) bytes"
    Write-Host "SHA256：$($hash.Hash)"
}
finally
{
    Pop-Location
}
