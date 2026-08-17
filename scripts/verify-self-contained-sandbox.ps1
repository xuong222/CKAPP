[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Executable,

    [ValidateRange(30, 900)]
    [int]$TimeoutSeconds = 240
)

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
使用 XML 安全转义后的绝对路径创建 Windows Sandbox 配置。
.PARAMETER PublishDirectory
只读映射到沙盒的单文件发布目录。
.PARAMETER RunnerDirectory
只读映射到沙盒的验证脚本目录。
.PARAMETER ValidationDirectory
允许沙盒写回证据文件的主机目录。
.PARAMETER ConfigurationPath
需要写入的 .wsb 配置文件路径。
#>
function Write-SandboxConfiguration
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$PublishDirectory,

        [Parameter(Mandatory = $true)]
        [string]$RunnerDirectory,

        [Parameter(Mandatory = $true)]
        [string]$ValidationDirectory,

        [Parameter(Mandatory = $true)]
        [string]$ConfigurationPath
    )

    $publishXml = [Security.SecurityElement]::Escape($PublishDirectory)
    $runnerXml = [Security.SecurityElement]::Escape($RunnerDirectory)
    $validationXml = [Security.SecurityElement]::Escape($ValidationDirectory)
    $configuration = @"
<Configuration>
  <VGpu>Disable</VGpu>
  <Networking>Disable</Networking>
  <ClipboardRedirection>Disable</ClipboardRedirection>
  <PrinterRedirection>Disable</PrinterRedirection>
  <MemoryInMB>4096</MemoryInMB>
  <MappedFolders>
    <MappedFolder>
      <HostFolder>$publishXml</HostFolder>
      <SandboxFolder>C:\CH32Publish</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
    <MappedFolder>
      <HostFolder>$runnerXml</HostFolder>
      <SandboxFolder>C:\CH32Runner</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
    <MappedFolder>
      <HostFolder>$validationXml</HostFolder>
      <SandboxFolder>C:\CH32Validation</SandboxFolder>
      <ReadOnly>false</ReadOnly>
    </MappedFolder>
  </MappedFolders>
  <LogonCommand>
    <Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\CH32Runner\run-validation.ps1</Command>
  </LogonCommand>
</Configuration>
"@
    [IO.File]::WriteAllText(
        $ConfigurationPath,
        $configuration,
        [Text.UTF8Encoding]::new($true))
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$executablePath = [IO.Path]::GetFullPath($Executable)
$publishDirectory = Split-Path -Parent $executablePath
$runnerDirectory = Join-Path $repositoryRoot "artifacts\temp\windows-sandbox"
$validationDirectory = Join-Path $repositoryRoot "artifacts\validation"
$configurationPath = Join-Path $runnerDirectory "self-contained-validation.wsb"
$runnerPath = Join-Path $runnerDirectory "run-validation.ps1"
$passMarker = Join-Path $validationDirectory "SELF_CONTAINED_PASS.txt"
$failureMarker = Join-Path $validationDirectory "SELF_CONTAINED_FAIL.txt"

if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf))
{
    throw "未找到待验证 EXE：$executablePath"
}

if ([IO.Path]::GetExtension($executablePath) -ne ".exe")
{
    throw "自包含验证只接受 Windows EXE：$executablePath"
}

$sandboxCommand = Get-Command "WindowsSandbox.exe" -ErrorAction SilentlyContinue

if ($null -eq $sandboxCommand)
{
    throw "当前 Windows 版本未提供 Windows Sandbox。请在 Windows Pro/Enterprise/Education 的 Sandbox，或没有 .NET 8 Desktop Runtime 的干净 Windows x64 虚拟机中执行等效验证。"
}

Assert-PathWithinRoot -RootPath $repositoryRoot -CandidatePath $runnerDirectory
Assert-PathWithinRoot -RootPath $repositoryRoot -CandidatePath $validationDirectory
New-Item -ItemType Directory -Path $runnerDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $validationDirectory -Force | Out-Null

foreach ($evidencePath in @(
    $passMarker,
    $failureMarker,
    (Join-Path $validationDirectory "sandbox-dashboard.png"),
    (Join-Path $validationDirectory "dotnet-runtimes.txt"),
    (Join-Path $validationDirectory "runtime-state.txt")
))
{
    if (Test-Path -LiteralPath $evidencePath)
    {
        Remove-Item -LiteralPath $evidencePath -Force
    }
}

$runnerScript = @'
$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Write-Failure
{
    <#
    .SYNOPSIS
    将沙盒内部异常写回主机验证目录。
    .PARAMETER Message
    需要完整保留的异常和诊断文本。
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    [IO.File]::WriteAllText(
        "C:\CH32Validation\SELF_CONTAINED_FAIL.txt",
        $Message,
        [Text.UTF8Encoding]::new($true))
}

try
{
    $runtimeState = "DOTNET_NOT_INSTALLED"
    $dotnetCommand = Get-Command "dotnet.exe" -ErrorAction SilentlyContinue

    if ($null -ne $dotnetCommand)
    {
        $runtimeList = (& $dotnetCommand.Source --list-runtimes 2>&1 | Out-String).Trim()
        [IO.File]::WriteAllText(
            "C:\CH32Validation\dotnet-runtimes.txt",
            $runtimeList,
            [Text.UTF8Encoding]::new($true))
        $runtimeState = if ($runtimeList -match "Microsoft\.WindowsDesktop\.App\s+8\.")
        {
            "DOTNET8_DESKTOP_PRESENT"
        }
        else
        {
            "DOTNET8_DESKTOP_NOT_PRESENT"
        }
    }

    [IO.File]::WriteAllText(
        "C:\CH32Validation\runtime-state.txt",
        $runtimeState,
        [Text.UTF8Encoding]::new($true))

    if ($runtimeState -eq "DOTNET8_DESKTOP_PRESENT")
    {
        throw "沙盒中检测到 .NET 8 Desktop Runtime，本次环境不能作为无运行时自包含证据。"
    }

    $env:DOTNET_ROOT = "C:\NoDotNetRuntime"
    $env:DOTNET_MULTILEVEL_LOOKUP = "0"
    $env:CH32_CAPTURE_PATH = "C:\CH32Validation\sandbox-dashboard.png"
    $env:CH32_CAPTURE_AND_EXIT = "1"
    $startParameters = @{
        FilePath = "C:\CH32Publish\CKAPP.exe"
        PassThru = $true
    }
    $process = Start-Process @startParameters

    if (-not $process.WaitForExit(60000))
    {
        $process.Kill()
        throw "自包含 EXE 在 60 秒内没有完成启动、渲染和安全退出。"
    }

    $screenshotPath = "C:\CH32Validation\sandbox-dashboard.png"

    if (-not (Test-Path -LiteralPath $screenshotPath -PathType Leaf))
    {
        throw "应用未生成沙盒主窗口截图。"
    }

    $screenshot = Get-Item -LiteralPath $screenshotPath

    if ($screenshot.Length -le 0)
    {
        throw "沙盒主窗口截图为空。"
    }

    $hash = Get-FileHash -LiteralPath "C:\CH32Publish\CKAPP.exe" -Algorithm SHA256
    $evidence = @(
        "SELF_CONTAINED_PASS",
        "TimestampUtc=$([DateTimeOffset]::UtcNow.ToString('O'))",
        "RuntimeState=$runtimeState",
        "ExitCode=$($process.ExitCode)",
        "ScreenshotBytes=$($screenshot.Length)",
        "SHA256=$($hash.Hash)"
    ) -join [Environment]::NewLine
    [IO.File]::WriteAllText(
        "C:\CH32Validation\SELF_CONTAINED_PASS.txt",
        $evidence,
        [Text.UTF8Encoding]::new($true))
}
catch
{
    Write-Failure -Message $_.Exception.ToString()
}
finally
{
    Start-Process -FilePath "shutdown.exe" -ArgumentList "/s", "/t", "0" -WindowStyle Hidden
}
'@
[IO.File]::WriteAllText(
    $runnerPath,
    $runnerScript,
    [Text.UTF8Encoding]::new($true))
$sandboxConfigurationParameters = @{
    PublishDirectory = $publishDirectory
    RunnerDirectory = $runnerDirectory
    ValidationDirectory = $validationDirectory
    ConfigurationPath = $configurationPath
}
Write-SandboxConfiguration @sandboxConfigurationParameters

Write-Host "正在启动 Windows Sandbox 自包含验证。" -ForegroundColor Cyan
$quotedConfigurationPath = '"' + $configurationPath + '"'
Start-Process -FilePath $sandboxCommand.Source -ArgumentList $quotedConfigurationPath | Out-Null
$deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)

while ([DateTimeOffset]::UtcNow -lt $deadline)
{
    if (Test-Path -LiteralPath $passMarker -PathType Leaf)
    {
        Write-Host (Get-Content -LiteralPath $passMarker -Raw) -ForegroundColor Green
        return
    }

    if (Test-Path -LiteralPath $failureMarker -PathType Leaf)
    {
        $failure = Get-Content -LiteralPath $failureMarker -Raw
        throw "Windows Sandbox 自包含验证失败：`n$failure"
    }

    Start-Sleep -Milliseconds 500
}

throw "Windows Sandbox 在 $TimeoutSeconds 秒内没有生成验证结果。请检查 Sandbox 是否启动、映射目录是否可用以及主窗口是否成功渲染。"
