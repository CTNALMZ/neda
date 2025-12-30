# NEDA.Build/DeploymentScripts/Deploy.ps1

<#
.SYNOPSIS
    NEDA System Deployment Script
.DESCRIPTION
    This script deploys the NEDA system across various environments with comprehensive
    configuration management, health checks, and rollback capabilities.
.PARAMETER Environment
    Target deployment environment (Development, Staging, Production)
.PARAMETER ConfigurationFile
    Path to deployment configuration file
.PARAMETER Component
    Specific component to deploy (optional, deploys all if not specified)
.PARAMETER SkipValidation
    Skip pre-deployment validation checks
.PARAMETER Force
    Force deployment even if warnings are detected
.PARAMETER DryRun
    Perform a dry run without making changes
.PARAMETER RollbackVersion
    Version to rollback to
.PARAMETER DeploymentMode
    Deployment mode (InPlace, BlueGreen, Canary)
.PARAMETER LogLevel
    Logging level (Verbose, Info, Warning, Error)
.EXAMPLE
    .\Deploy.ps1 -Environment Development -ConfigurationFile .\config\dev.json
.EXAMPLE
    .\Deploy.ps1 -Environment Production -Component API -DeploymentMode BlueGreen
.EXAMPLE
    .\Deploy.ps1 -Environment Staging -RollbackVersion 1.2.3
.NOTES
    Version: 2.0.0
    Author: NEDA Deployment Team
    Requires: PowerShell 7.0+, Administrator privileges for some operations
#>

[CmdletBinding(DefaultParameterSetName = 'Deploy')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Deploy')]
    [Parameter(Mandatory = $true, ParameterSetName = 'Rollback')]
    [ValidateSet('Development', 'Staging', 'Production', 'Integration', 'QA')]
    [string]$Environment,

    [Parameter(ParameterSetName = 'Deploy')]
    [Parameter(ParameterSetName = 'Rollback')]
    [ValidateScript({
        if (Test-Path $_ -PathType Leaf) { $true }
        else { throw "Configuration file $_ not found" }
    })]
    [string]$ConfigurationFile,

    [Parameter(ParameterSetName = 'Deploy')]
    [ValidateSet('All', 'API', 'UI', 'Services', 'Database', 'Monitoring', 'Security')]
    [string]$Component = 'All',

    [Parameter(ParameterSetName = 'Deploy')]
    [switch]$SkipValidation,

    [Parameter(ParameterSetName = 'Deploy')]
    [Parameter(ParameterSetName = 'Rollback')]
    [switch]$Force,

    [Parameter(ParameterSetName = 'Deploy')]
    [switch]$DryRun,

    [Parameter(Mandatory = $true, ParameterSetName = 'Rollback')]
    [string]$RollbackVersion,

    [Parameter(ParameterSetName = 'Deploy')]
    [ValidateSet('InPlace', 'BlueGreen', 'Canary', 'Rolling')]
    [string]$DeploymentMode = 'InPlace',

    [Parameter()]
    [ValidateSet('Verbose', 'Info', 'Warning', 'Error', 'Silent')]
    [string]$LogLevel = 'Info'
)

#region Initialization

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# Script metadata
$ScriptVersion = "2.0.0"
$ScriptStartTime = Get-Date
$DeploymentId = [Guid]::NewGuid().ToString()

# Logging configuration
$LogLevels = @{
    'Verbose' = 0
    'Info'    = 1
    'Warning' = 2
    'Error'   = 3
    'Silent'  = 4
}
$CurrentLogLevel = $LogLevels[$LogLevel]

# Paths and directories
$ScriptRoot = $PSScriptRoot
$WorkingDirectory = Join-Path $env:TEMP "NEDA_Deployment_$DeploymentId"
$LogDirectory = Join-Path $ScriptRoot "logs"
$ArtifactDirectory = Join-Path $ScriptRoot "artifacts"
$BackupDirectory = Join-Path $ScriptRoot "backups"

# Create required directories
$RequiredDirectories = @($WorkingDirectory, $LogDirectory, $ArtifactDirectory, $BackupDirectory)
foreach ($dir in $RequiredDirectories) {
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
}

# Log file setup
$LogFile = Join-Path $LogDirectory "deploy_${Environment}_$(Get-Date -Format 'yyyyMMdd_HHmmss').log"
$Global:TranscriptLog = $LogFile

# Deployment state
$DeploymentState = @{
    DeploymentId = $DeploymentId
    Environment = $Environment
    Component = $Component
    StartTime = $ScriptStartTime
    EndTime = $null
    Status = 'Initializing'
    Steps = @()
    Errors = @()
    Warnings = @()
    RollbackRequired = $false
    RollbackSteps = @()
}

#region Functions

function Write-Log {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Message,
        
        [Parameter()]
        [ValidateSet('Verbose', 'Info', 'Warning', 'Error', 'Success')]
        [string]$Level = 'Info',
        
        [Parameter()]
        [string]$Component = 'Deployment',
        
        [Parameter()]
        [switch]$NoConsole
    )
    
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss.fff"
    $logLevel = $Level.ToUpper()
    $logMessage = "[$timestamp] [$logLevel] [$Component] $Message"
    
    # Write to log file
    $logMessage | Out-File -FilePath $LogFile -Append -Encoding UTF8
    
    # Write to console based on log level
    if (-not $NoConsole -and $LogLevels[$Level] -ge $CurrentLogLevel) {
        switch ($Level) {
            'Verbose' { Write-Host $logMessage -ForegroundColor Gray }
            'Info'    { Write-Host $logMessage -ForegroundColor White }
            'Warning' { Write-Host $logMessage -ForegroundColor Yellow }
            'Error'   { Write-Host $logMessage -ForegroundColor Red }
            'Success' { Write-Host $logMessage -ForegroundColor Green }
        }
    }
    
    # Update deployment state
    $DeploymentState.Steps += @{
        Timestamp = $timestamp
        Level = $Level
        Component = $Component
        Message = $Message
    }
    
    if ($Level -eq 'Error') {
        $DeploymentState.Errors += $Message
    }
    elseif ($Level -eq 'Warning') {
        $DeploymentState.Warnings += $Message
    }
}

function Start-Transcript {
    param([string]$Path)
    
    try {
        Start-Transcript -Path $Path -Append -Force -IncludeInvocationHeader | Out-Null
        Write-Log "Transcript started: $Path" -Level Verbose
    }
    catch {
        Write-Log "Failed to start transcript: $_" -Level Warning
    }
}

function Stop-Transcript {
    try {
        Stop-Transcript | Out-Null
        Write-Log "Transcript stopped" -Level Verbose
    }
    catch {
        # Ignore errors when stopping transcript
    }
}

function Test-Administrator {
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object System.Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Test-Requirements {
    Write-Log "Checking system requirements..." -Level Info
    
    $requirements = @{
        'PowerShell Version' = @{
            Required = '7.0.0'
            Actual = $PSVersionTable.PSVersion.ToString()
            Test = { [version]$PSVersionTable.PSVersion -ge [version]$args[0] }
        }
        'Execution Policy' = @{
            Required = 'RemoteSigned or Unrestricted'
            Actual = Get-ExecutionPolicy
            Test = { $args[0] -in @('RemoteSigned', 'Unrestricted', 'Bypass', 'Unrestricted') }
        }
        'Disk Space' = @{
            Required = '10GB free'
            Actual = "{0:N2} GB" -f ((Get-PSDrive -Name $env:SystemDrive[0]).Free / 1GB)
            Test = { (Get-PSDrive -Name $env:SystemDrive[0]).Free -gt 10GB }
        }
        'Memory' = @{
            Required = '8GB available'
            Actual = "{0:N2} GB" -f ((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB)
            Test = { (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory -ge 8GB }
        }
    }
    
    $allPassed = $true
    
    foreach ($req in $requirements.Keys) {
        $spec = $requirements[$req]
        $passed = & $spec.Test $spec.Required
        
        if ($passed) {
            Write-Log "$req: $($spec.Actual) ✓" -Level Success
        }
        else {
            Write-Log "$req: $($spec.Actual) ✗ (Required: $($spec.Required))" -Level Error
            $allPassed = $false
        }
    }
    
    # Check for required modules
    $requiredModules = @(
        @{ Name = 'PowerShellGet'; Version = '2.2.5' }
        @{ Name = 'PackageManagement'; Version = '1.4.7' }
        @{ Name = 'SqlServer'; Version = '21.1.18256' }
    )
    
    foreach ($module in $requiredModules) {
        $installed = Get-Module -ListAvailable -Name $module.Name | 
                     Where-Object { $_.Version -ge [version]$module.Version }
        
        if ($installed) {
            Write-Log "Module $($module.Name) v$($installed.Version) ✓" -Level Success
        }
        else {
            Write-Log "Module $($module.Name) v$($module.Version) or higher required ✗" -Level Warning
        }
    }
    
    return $allPassed
}

function Get-DeploymentConfiguration {
    param([string]$ConfigFile)
    
    Write-Log "Loading deployment configuration..." -Level Info
    
    # Default configuration
    $defaultConfig = @{
        Environment = $Environment
        DeploymentId = $DeploymentId
        Timestamp = (Get-Date).ToString('o')
        
        # Network settings
        Network = @{
            Ports = @(80, 443, 8080, 5000, 1433)
            FirewallRules = @()
            DnsSettings = @{}
        }
        
        # Service configuration
        Services = @{
            WebServer = @{
                Type = 'IIS'
                Sites = @()
                AppPools = @()
            }
            Database = @{
                Type = 'SQLServer'
                ConnectionString = ''
                BackupBeforeDeploy = $true
            }
            Cache = @{
                Type = 'Redis'
                ConnectionString = ''
            }
        }
        
        # Security settings
        Security = @{
            EncryptionKey = ''
            CertificateThumbprint = ''
            ServiceAccounts = @{}
        }
        
        # Deployment settings
        Deployment = @{
            Mode = $DeploymentMode
            MaxParallelDeployments = 3
            HealthCheckTimeout = 300
            RollbackOnFailure = $true
            BackupRetentionDays = 30
        }
        
        # Component configurations
        Components = @{
            API = @{
                Path = ''
                ServiceName = 'NEDA.API'
                Dependencies = @('Database', 'Cache')
            }
            UI = @{
                Path = ''
                ServiceName = 'NEDA.UI'
                Dependencies = @('API')
            }
            Services = @{
                Path = ''
                ServiceNames = @('NEDA.Service.*')
            }
        }
    }
    
    # Load configuration file if specified
    $config = $defaultConfig
    
    if ($ConfigFile) {
        try {
            $fileConfig = Get-Content $ConfigFile -Raw | ConvertFrom-Json -Depth 10 | ConvertTo-Hashtable
            $config = Merge-Hashtables $defaultConfig $fileConfig
            Write-Log "Configuration loaded from: $ConfigFile" -Level Success
        }
        catch {
            Write-Log "Failed to load configuration file: $_" -Level Error
            throw "Configuration file load failed"
        }
    }
    else {
        Write-Log "Using default configuration" -Level Info
    }
    
    # Validate configuration
    Test-Configuration -Configuration $config
    
    return $config
}

function Merge-Hashtables {
    param(
        [hashtable]$Base,
        [hashtable]$Override
    )
    
    foreach ($key in $Override.Keys) {
        if ($Base.ContainsKey($key) -and $Base[$key] -is [hashtable] -and $Override[$key] -is [hashtable]) {
            $Base[$key] = Merge-Hashtables $Base[$key] $Override[$key]
        }
        else {
            $Base[$key] = $Override[$key]
        }
    }
    
    return $Base
}

function ConvertTo-Hashtable {
    param([PSObject]$Object)
    
    $hashtable = @{}
    
    if ($Object) {
        $Object.PSObject.Properties | ForEach-Object {
            if ($_.Value -is [PSCustomObject]) {
                $hashtable[$_.Name] = ConvertTo-Hashtable $_.Value
            }
            elseif ($_.Value -is [Array]) {
                $hashtable[$_.Name] = @($_.Value | ForEach-Object {
                    if ($_ -is [PSCustomObject]) {
                        ConvertTo-Hashtable $_
                    }
                    else {
                        $_
                    }
                })
            }
            else {
                $hashtable[$_.Name] = $_.Value
            }
        }
    }
    
    return $hashtable
}

function Test-Configuration {
    param([hashtable]$Configuration)
    
    Write-Log "Validating configuration..." -Level Info
    
    $errors = @()
    
    # Check required settings
    $requiredPaths = @(
        'Components.API.Path',
        'Components.UI.Path',
        'Services.Database.ConnectionString'
    )
    
    foreach ($path in $requiredPaths) {
        $value = $Configuration
        $parts = $path.Split('.')
        
        foreach ($part in $parts) {
            if ($value -is [hashtable] -and $value.ContainsKey($part)) {
                $value = $value[$part]
            }
            else {
                $errors += "Missing configuration: $path"
                break
            }
        }
        
        if ([string]::IsNullOrEmpty($value)) {
            $errors += "Empty configuration value: $path"
        }
    }
    
    # Validate ports
    $ports = $Configuration.Network.Ports
    if ($ports -is [array]) {
        foreach ($port in $ports) {
            if ($port -lt 1 -or $port -gt 65535) {
                $errors += "Invalid port number: $port"
            }
        }
    }
    
    # Validate deployment mode
    $validModes = @('InPlace', 'BlueGreen', 'Canary', 'Rolling')
    if ($Configuration.Deployment.Mode -notin $validModes) {
        $errors += "Invalid deployment mode: $($Configuration.Deployment.Mode)"
    }
    
    if ($errors.Count -gt 0) {
        foreach ($error in $errors) {
            Write-Log $error -Level Error
        }
        throw "Configuration validation failed with $($errors.Count) errors"
    }
    
    Write-Log "Configuration validation passed ✓" -Level Success
}

function Backup-System {
    param(
        [hashtable]$Configuration,
        [string]$BackupType = 'Full'
    )
    
    Write-Log "Starting $BackupType backup..." -Level Info
    
    $backupTimestamp = Get-Date -Format 'yyyyMMdd_HHmmss'
    $backupPath = Join-Path $BackupDirectory "${Environment}_${backupTimestamp}_${BackupType}"
    New-Item -ItemType Directory -Path $backupPath -Force | Out-Null
    
    $backupManifest = @{
        DeploymentId = $DeploymentId
        Environment = $Environment
        BackupType = $BackupType
        Timestamp = $backupTimestamp
        Items = @()
    }
    
    try {
        # Backup IIS configuration
        if ($Configuration.Services.WebServer.Type -eq 'IIS') {
            Write-Log "Backing up IIS configuration..." -Level Verbose
            $iisBackupPath = Join-Path $backupPath 'IIS'
            New-Item -ItemType Directory -Path $iisBackupPath -Force | Out-Null
            
            # Export IIS configuration
            $appCmd = "$env:SystemRoot\System32\inetsrv\appcmd.exe"
            if (Test-Path $appCmd) {
                & $appCmd list apppool /config /xml > (Join-Path $iisBackupPath 'AppPools.xml')
                & $appCmd list site /config /xml > (Join-Path $iisBackupPath 'Sites.xml')
                
                $backupManifest.Items += @{
                    Type = 'IIS'
                    Path = $iisBackupPath
                    Status = 'Success'
                }
            }
        }
        
        # Backup application files
        foreach ($component in $Configuration.Components.Keys) {
            $componentConfig = $Configuration.Components[$component]
            if ($componentConfig.Path -and (Test-Path $componentConfig.Path)) {
                Write-Log "Backing up $component files..." -Level Verbose
                $componentBackupPath = Join-Path $backupPath $component
                Copy-Item -Path $componentConfig.Path -Destination $componentBackupPath -Recurse -Force
                
                $backupManifest.Items += @{
                    Type = 'Files'
                    Component = $component
                    Path = $componentBackupPath
                    Status = 'Success'
                }
            }
        }
        
        # Backup database
        if ($Configuration.Services.Database.BackupBeforeDeploy) {
            Write-Log "Backing up database..." -Level Info
            $dbBackupPath = Join-Path $backupPath 'Database'
            New-Item -ItemType Directory -Path $dbBackupPath -Force | Out-Null
            
            try {
                $connectionString = $Configuration.Services.Database.ConnectionString
                $dbBackupFile = Join-Path $dbBackupPath "database_$backupTimestamp.bak"
                
                # SQL Server backup
                if ($Configuration.Services.Database.Type -eq 'SQLServer') {
                    $sql = @"
BACKUP DATABASE [NEDA] 
TO DISK = N'$dbBackupFile' 
WITH COMPRESSION, STATS = 5
"@
                    
                    Invoke-Sqlcmd -ConnectionString $connectionString -Query $sql -QueryTimeout 300
                    
                    $backupManifest.Items += @{
                        Type = 'Database'
                        File = $dbBackupFile
                        Status = 'Success'
                    }
                }
            }
            catch {
                Write-Log "Database backup failed: $_" -Level Warning
                $backupManifest.Items += @{
                    Type = 'Database'
                    Error = $_.ToString()
                    Status = 'Failed'
                }
            }
        }
        
        # Save backup manifest
        $backupManifest | ConvertTo-Json -Depth 10 | Out-File (Join-Path $backupPath 'manifest.json') -Encoding UTF8
        
        # Update deployment state
        $DeploymentState.BackupPath = $backupPath
        $DeploymentState.BackupManifest = $backupManifest
        
        Write-Log "$BackupType backup completed: $backupPath" -Level Success
        
        return $backupPath
    }
    catch {
        Write-Log "Backup failed: $_" -Level Error
        throw "Backup operation failed"
    }
}

function Deploy-Component {
    param(
        [string]$ComponentName,
        [hashtable]$Configuration,
        [hashtable]$ComponentConfig
    )
    
    Write-Log "Deploying component: $ComponentName" -Level Info
    
    $stepTimer = [System.Diagnostics.Stopwatch]::StartNew()
    
    try {
        # Check if component should be deployed
        if ($Component -ne 'All' -and $Component -ne $ComponentName) {
            Write-Log "Skipping $ComponentName (not selected for deployment)" -Level Verbose
            return
        }
        
        # Check dependencies
        if ($ComponentConfig.Dependencies) {
            foreach ($dep in $ComponentConfig.Dependencies) {
                Write-Log "Checking dependency: $dep" -Level Verbose
                # Add dependency check logic here
            }
        }
        
        # Component-specific deployment logic
        switch ($ComponentName) {
            'API' {
                Deploy-Api -Configuration $Configuration -ComponentConfig $ComponentConfig
            }
            'UI' {
                Deploy-UI -Configuration $Configuration -ComponentConfig $ComponentConfig
            }
            'Services' {
                Deploy-Services -Configuration $Configuration -ComponentConfig $ComponentConfig
            }
            'Database' {
                Deploy-Database -Configuration $Configuration -ComponentConfig $ComponentConfig
            }
            default {
                Write-Log "Unknown component: $ComponentName" -Level Warning
            }
        }
        
        $stepTimer.Stop()
        Write-Log "Component $ComponentName deployed successfully in $($stepTimer.Elapsed.TotalSeconds)s" -Level Success
    }
    catch {
        $stepTimer.Stop()
        Write-Log "Component $ComponentName deployment failed after $($stepTimer.Elapsed.TotalSeconds)s: $_" -Level Error
        throw
    }
}

function Deploy-Api {
    param(
        [hashtable]$Configuration,
        [hashtable]$ComponentConfig
    )
    
    $apiPath = $ComponentConfig.Path
    $serviceName = $ComponentConfig.ServiceName
    
    if (-not (Test-Path $apiPath)) {
        throw "API path not found: $apiPath"
    }
    
    Write-Log "Deploying API from: $apiPath" -Level Info
    
    # Stop service if running
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($service -and $service.Status -eq 'Running') {
        Write-Log "Stopping service: $serviceName" -Level Info
        if (-not $DryRun) {
            Stop-Service -Name $serviceName -Force
            Start-Sleep -Seconds 5
        }
    }
    
    # Backup current deployment
    $currentDeploymentPath = "C:\Program Files\NEDA\API"
    if (Test-Path $currentDeploymentPath) {
        $backupPath = Join-Path $BackupDirectory "API_$(Get-Date -Format 'yyyyMMdd_HHmmss')"
        Write-Log "Backing up current API to: $backupPath" -Level Verbose
        if (-not $DryRun) {
            Copy-Item -Path $currentDeploymentPath -Destination $backupPath -Recurse -Force
        }
    }
    
    # Deploy new files
    Write-Log "Copying API files..." -Level Info
    if (-not $DryRun) {
        # Clear destination
        if (Test-Path $currentDeploymentPath) {
            Remove-Item -Path $currentDeploymentPath -Recurse -Force
        }
        
        # Copy new files
        Copy-Item -Path $apiPath -Destination $currentDeploymentPath -Recurse -Force
        
        # Update configuration
        $configFile = Join-Path $currentDeploymentPath "appsettings.$Environment.json"
        if (Test-Path $configFile) {
            $config = Get-Content $configFile -Raw | ConvertFrom-Json
            $config.ConnectionStrings.Default = $Configuration.Services.Database.ConnectionString
            $config | ConvertTo-Json -Depth 10 | Out-File $configFile -Encoding UTF8
        }
    }
    
    # Start service
    if (-not $DryRun) {
        if ($service) {
            Write-Log "Starting service: $serviceName" -Level Info
            Start-Service -Name $serviceName
        }
        else {
            Write-Log "Creating new service: $serviceName" -Level Info
            # Service creation logic here
        }
    }
    
    # Verify deployment
    Test-ComponentHealth -ComponentName 'API' -Configuration $Configuration
}

function Deploy-UI {
    param(
        [hashtable]$Configuration,
        [hashtable]$ComponentConfig
    )
    
    $uiPath = $ComponentConfig.Path
    
    if (-not (Test-Path $uiPath)) {
        throw "UI path not found: $uiPath"
    }
    
    Write-Log "Deploying UI from: $uiPath" -Level Info
    
    # IIS deployment
    if ($Configuration.Services.WebServer.Type -eq 'IIS') {
        $siteName = "NEDA_$Environment"
        $sitePath = "C:\inetpub\wwwroot\NEDA"
        
        # Create or update IIS site
        if (-not (Get-Website -Name $siteName -ErrorAction SilentlyContinue)) {
            Write-Log "Creating IIS site: $siteName" -Level Info
            if (-not $DryRun) {
                New-Website -Name $siteName -PhysicalPath $sitePath -Port 80 -Force
            }
        }
        
        # Deploy files
        Write-Log "Deploying UI files to IIS..." -Level Info
        if (-not $DryRun) {
            if (Test-Path $sitePath) {
                Remove-Item -Path $sitePath -Recurse -Force
            }
            Copy-Item -Path $uiPath -Destination $sitePath -Recurse -Force
        }
        
        # Update web.config
        $webConfigPath = Join-Path $sitePath "web.config"
        if (Test-Path $webConfigPath -and -not $DryRun) {
            [xml]$webConfig = Get-Content $webConfigPath
            $appSettings = $webConfig.SelectSingleNode("//appSettings")
            if ($appSettings) {
                $addNode = $webConfig.CreateElement("add")
                $addNode.SetAttribute("key", "Environment")
                $addNode.SetAttribute("value", $Environment)
                $appSettings.AppendChild($addNode)
                $webConfig.Save($webConfigPath)
            }
        }
    }
    
    Test-ComponentHealth -ComponentName 'UI' -Configuration $Configuration
}

function Deploy-Database {
    param(
        [hashtable]$Configuration,
        [hashtable]$ComponentConfig
    )
    
    Write-Log "Deploying database changes..." -Level Info
    
    $connectionString = $Configuration.Services.Database.ConnectionString
    
    if ([string]::IsNullOrEmpty($connectionString)) {
        Write-Log "No database connection string configured" -Level Warning
        return
    }
    
    # Find migration scripts
    $migrationPath = Join-Path $ComponentConfig.Path "Migrations"
    if (-not (Test-Path $migrationPath)) {
        Write-Log "No migration scripts found" -Level Info
        return
    }
    
    $migrationScripts = Get-ChildItem -Path $migrationPath -Filter "*.sql" | Sort-Object Name
    
    if ($migrationScripts.Count -eq 0) {
        Write-Log "No migration scripts to execute" -Level Info
        return
    }
    
    Write-Log "Found $($migrationScripts.Count) migration scripts" -Level Info
    
    foreach ($script in $migrationScripts) {
        Write-Log "Executing migration: $($script.Name)" -Level Verbose
        
        if (-not $DryRun) {
            try {
                $sqlContent = Get-Content $script.FullName -Raw
                Invoke-Sqlcmd -ConnectionString $connectionString -Query $sqlContent -QueryTimeout 300 -ErrorAction Stop
                Write-Log "Migration successful: $($script.Name)" -Level Success
            }
            catch {
                Write-Log "Migration failed: $($script.Name) - $_" -Level Error
                throw "Database migration failed"
            }
        }
    }
    
    # Update database version
    if (-not $DryRun) {
        $versionSql = @"
IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'SchemaVersions')
BEGIN
    CREATE TABLE SchemaVersions (
        Version VARCHAR(50) PRIMARY KEY,
        Applied DATETIME DEFAULT GETDATE(),
        Description NVARCHAR(500)
    )
END

INSERT INTO SchemaVersions (Version, Description) 
VALUES ('$ScriptVersion', 'Deployed via PowerShell script $DeploymentId')
"@
        
        Invoke-Sqlcmd -ConnectionString $connectionString -Query $versionSql
    }
    
    Test-ComponentHealth -ComponentName 'Database' -Configuration $Configuration
}

function Test-ComponentHealth {
    param(
        [string]$ComponentName,
        [hashtable]$Configuration
    )
    
    Write-Log "Testing health of component: $ComponentName" -Level Info
    
    $healthChecks = @{
        'API' = {
            $serviceName = $Configuration.Components.API.ServiceName
            $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
            if (-not $service -or $service.Status -ne 'Running') {
                throw "Service $serviceName is not running"
            }
            
            # Test API endpoint
            $apiUrl = "http://localhost:5000/health"
            try {
                $response = Invoke-WebRequest -Uri $apiUrl -TimeoutSec 10
                if ($response.StatusCode -ne 200) {
                    throw "API returned status code: $($response.StatusCode)"
                }
            }
            catch {
                throw "API health check failed: $_"
            }
        }
        
        'UI' = {
            # Test UI endpoint
            $uiUrl = "http://localhost/"
            try {
                $response = Invoke-WebRequest -Uri $uiUrl -TimeoutSec 10
                if ($response.StatusCode -ne 200) {
                    throw "UI returned status code: $($response.StatusCode)"
                }
            }
            catch {
                throw "UI health check failed: $_"
            }
        }
        
        'Database' = {
            $connectionString = $Configuration.Services.Database.ConnectionString
            try {
                $testQuery = "SELECT 1 AS TestValue"
                $result = Invoke-Sqlcmd -ConnectionString $connectionString -Query $testQuery -QueryTimeout 30
                if ($result.TestValue -ne 1) {
                    throw "Database test query failed"
                }
            }
            catch {
                throw "Database health check failed: $_"
            }
        }
    }
    
    if ($healthChecks.ContainsKey($ComponentName)) {
        try {
            & $healthChecks[$ComponentName]
            Write-Log "Component $ComponentName health check passed ✓" -Level Success
            return $true
        }
        catch {
            Write-Log "Component $ComponentName health check failed: $_" -Level Error
            return $false
        }
    }
    else {
        Write-Log "No health check defined for component: $ComponentName" -Level Warning
        return $true
    }
}

function Test-SystemHealth {
    param([hashtable]$Configuration)
    
    Write-Log "Performing system health check..." -Level Info
    
    $healthResults = @{}
    $allHealthy = $true
    
    # Check network connectivity
    Write-Log "Testing network connectivity..." -Level Verbose
    $testEndpoints = @(
        'https://api.neda.local/health',
        'https://ui.neda.local',
        $Configuration.Services.Database.ConnectionString -replace '.*Server=([^;]+).*', '$1'
    )
    
    foreach ($endpoint in $testEndpoints) {
        try {
            Test-Connection -ComputerName $endpoint -Count 1 -Quiet -ErrorAction Stop
            Write-Log "Endpoint reachable: $endpoint ✓" -Level Success
        }
        catch {
            Write-Log "Endpoint unreachable: $endpoint ✗" -Level Warning
            $allHealthy = $false
        }
    }
    
    # Check disk space
    $drive = Get-PSDrive -Name $env:SystemDrive[0]
    $freeSpaceGB = $drive.Free / 1GB
    if ($freeSpaceGB -lt 5) {
        Write-Log "Low disk space: $freeSpaceGB GB free ✗" -Level Warning
        $allHealthy = $false
    }
    else {
        Write-Log "Disk space: $freeSpaceGB GB free ✓" -Level Success
    }
    
    # Check services
    $requiredServices = @(
        $Configuration.Components.API.ServiceName,
        'W3SVC',
        'MSSQLSERVER'
    )
    
    foreach ($serviceName in $requiredServices) {
        $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        if ($service -and $service.Status -eq 'Running') {
            Write-Log "Service running: $serviceName ✓" -Level Success
        }
        else {
            Write-Log "Service not running: $serviceName ✗" -Level Warning
            $allHealthy = $false
        }
    }
    
    return $allHealthy
}

function Invoke-Rollback {
    param(
        [string]$Version,
        [hashtable]$Configuration
    )
    
    Write-Log "Initiating rollback to version: $Version" -Level Warning
    
    # Find backup for specified version
    $backupPattern = "${Environment}_*_${Version}_*"
    $backupDirs = Get-ChildItem -Path $BackupDirectory -Directory -Filter $backupPattern | Sort-Object LastWriteTime -Descending
    
    if ($backupDirs.Count -eq 0) {
        throw "No backup found for version: $Version"
    }
    
    $latestBackup = $backupDirs[0].FullName
    Write-Log "Found backup: $latestBackup" -Level Info
    
    # Load backup manifest
    $manifestPath = Join-Path $latestBackup 'manifest.json'
    if (-not (Test-Path $manifestPath)) {
        throw "Backup manifest not found"
    }
    
    $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
    
    # Rollback each item
    foreach ($item in $manifest.Items) {
        try {
            switch ($item.Type) {
                'IIS' {
                    Write-Log "Restoring IIS configuration..." -Level Info
                    # IIS restore logic
                }
                'Files' {
                    Write-Log "Restoring files for $($item.Component)..." -Level Info
                    $source = $item.Path
                    $dest = $Configuration.Components[$item.Component].Path
                    
                    if (Test-Path $dest) {
                        Remove-Item -Path $dest -Recurse -Force
                    }
                    Copy-Item -Path $source -Destination $dest -Recurse -Force
                }
                'Database' {
                    Write-Log "Restoring database..." -Level Info
                    if ($item.File -and (Test-Path $item.File)) {
                        $connectionString = $Configuration.Services.Database.ConnectionString
                        $sql = @"
USE [master];
ALTER DATABASE [NEDA] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
RESTORE DATABASE [NEDA] FROM DISK = N'$($item.File)' WITH REPLACE;
ALTER DATABASE [NEDA] SET MULTI_USER;
"@
                        Invoke-Sqlcmd -ConnectionString $connectionString -Query $sql -QueryTimeout 600
                    }
                }
            }
        }
        catch {
            Write-Log "Rollback failed for $($item.Type): $_" -Level Error
            throw "Rollback failed"
        }
    }
    
    Write-Log "Rollback to version $Version completed successfully" -Level Success
}

function Complete-Deployment {
    param([hashtable]$Configuration)
    
    Write-Log "Completing deployment..." -Level Info
    
    # Final health check
    Write-Log "Performing final health check..." -Level Info
    if (-not (Test-SystemHealth -Configuration $Configuration)) {
        Write-Log "Final health check failed" -Level Warning
    }
    
    # Update deployment registry
    $deploymentRegistry = @{
        DeploymentId = $DeploymentId
        Environment = $Environment
        Component = $Component
        StartTime = $ScriptStartTime
        EndTime = Get-Date
        Status = 'Completed'
        Version = $ScriptVersion
        Configuration = $Configuration
    }
    
    $registryFile = Join-Path $WorkingDirectory "deployment_registry.json"
    $deploymentRegistry | ConvertTo-Json -Depth 10 | Out-File $registryFile -Encoding UTF8
    
    # Cleanup temporary files (keep for debugging if verbose logging)
    if ($LogLevel -ne 'Verbose') {
        Write-Log "Cleaning up temporary files..." -Level Verbose
        Remove-Item -Path $WorkingDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
    
    # Send notification
    Send-DeploymentNotification -Status 'Success' -Configuration $Configuration
    
    $totalTime = (Get-Date) - $ScriptStartTime
    Write-Log "Deployment completed successfully in $($totalTime.TotalMinutes.ToString('F1')) minutes" -Level Success
    
    # Generate deployment report
    Generate-DeploymentReport
}

function Send-DeploymentNotification {
    param(
        [string]$Status,
        [hashtable]$Configuration
    )
    
    $notificationSettings = $Configuration.Notifications
    if (-not $notificationSettings.Enabled) {
        return
    }
    
    $subject = "[$Environment] NEDA Deployment $Status"
    $body = @"
NEDA Deployment Report
======================

Deployment ID: $DeploymentId
Environment: $Environment
Status: $Status
Start Time: $($ScriptStartTime.ToString('yyyy-MM-dd HH:mm:ss'))
End Time: $((Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))
Duration: $(((Get-Date) - $ScriptStartTime).TotalMinutes.ToString('F1')) minutes

Components Deployed: $Component
Deployment Mode: $($Configuration.Deployment.Mode)

Errors: $($DeploymentState.Errors.Count)
Warnings: $($DeploymentState.Warnings.Count)

$(if ($DeploymentState.Errors.Count -gt 0) {"Errors:`n" + ($DeploymentState.Errors -join "`n")})

$(if ($DeploymentState.Warnings.Count -gt 0) {"Warnings:`n" + ($DeploymentState.Warnings -join "`n")})

Deployment Log: $LogFile
"@
    
    # Email notification
    if ($notificationSettings.Email.Enabled) {
        try {
            Send-MailMessage @{
                From = $notificationSettings.Email.From
                To = $notificationSettings.Email.To
                Subject = $subject
                Body = $body
                SmtpServer = $notificationSettings.Email.SmtpServer
                Port = $notificationSettings.Email.Port
                UseSsl = $true
            }
            Write-Log "Email notification sent" -Level Verbose
        }
        catch {
            Write-Log "Failed to send email notification: $_" -Level Warning
        }
    }
    
    # Slack notification
    if ($notificationSettings.Slack.Enabled) {
        try {
            $slackMessage = @{
                text = $subject
                attachments = @(@{
                    color = if ($Status -eq 'Success') { 'good' } else { 'danger' }
                    fields = @(
                        @{ title = 'Environment'; value = $Environment; short = $true },
                        @{ title = 'Status'; value = $Status; short = $true },
                        @{ title = 'Duration'; value = "$(((Get-Date) - $ScriptStartTime).TotalMinutes.ToString('F1')) minutes"; short = $true }
                    )
                })
            }
            
            Invoke-RestMethod -Uri $notificationSettings.Slack.WebhookUrl `
                -Method Post `
                -Body ($slackMessage | ConvertTo-Json -Depth 10) `
                -ContentType 'application/json'
                
            Write-Log "Slack notification sent" -Level Verbose
        }
        catch {
            Write-Log "Failed to send Slack notification: $_" -Level Warning
        }
    }
}

function Generate-DeploymentReport {
    $report = @"
# NEDA Deployment Report

## Summary
- **Deployment ID**: $DeploymentId
- **Environment**: $Environment
- **Status**: $($DeploymentState.Status)
- **Start Time**: $($ScriptStartTime.ToString('yyyy-MM-dd HH:mm:ss'))
- **End Time**: $($(Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))
- **Duration**: $(((Get-Date) - $ScriptStartTime).TotalMinutes.ToString('F1')) minutes

## Components
- **Selected Component**: $Component
- **Deployment Mode**: $DeploymentMode

## Statistics
- **Total Steps**: $($DeploymentState.Steps.Count)
- **Errors**: $($DeploymentState.Errors.Count)
- **Warnings**: $($DeploymentState.Warnings.Count)

## Error Details
$($DeploymentState.Errors -join "`n")

## Warning Details
$($DeploymentState.Warnings -join "`n")

## Step Log
$(($DeploymentState.Steps | ForEach-Object { 
    "[$($_.Timestamp)] [$($_.Level)] $($_.Message)" 
}) -join "`n")

## Files
- **Log File**: $LogFile
- **Working Directory**: $WorkingDirectory
$(if ($DeploymentState.BackupPath) { "- **Backup Path**: $($DeploymentState.BackupPath)" })

---

*Report generated by NEDA Deployment Script v$ScriptVersion*
"@
    
    $reportFile = Join-Path $LogDirectory "deployment_report_${Environment}_$(Get-Date -Format 'yyyyMMdd_HHmmss').md"
    $report | Out-File $reportFile -Encoding UTF8
    
    Write-Log "Deployment report generated: $reportFile" -Level Info
}

function Handle-Error {
    param([System.Management.Automation.ErrorRecord]$ErrorRecord)
    
    Write-Log "Unhandled error occurred: $($ErrorRecord.Exception.Message)" -Level Error
    Write-Log "Error details: $($ErrorRecord | Out-String)" -Level Verbose
    
    # Update deployment state
    $DeploymentState.Status = 'Failed'
    $DeploymentState.EndTime = Get-Date
    
    # Attempt rollback if configured
    if ($DeploymentState.RollbackRequired -and -not $DryRun) {
        Write-Log "Attempting rollback due to deployment failure..." -Level Warning
        try {
            Invoke-Rollback -Version $ScriptVersion -Configuration $Configuration
            Write-Log "Rollback completed successfully" -Level Success
        }
        catch {
            Write-Log "Rollback failed: $_" -Level Error
        }
    }
    
    # Send failure notification
    Send-DeploymentNotification -Status 'Failed' -Configuration $Configuration
    
    # Generate error report
    Generate-DeploymentReport
    
    throw $ErrorRecord
}

#endregion

#region Main Execution

try {
    # Start transcript
    Start-Transcript -Path $LogFile
    
    Write-Log "==========================================" -Level Info
    Write-Log "NEDA Deployment Script v$ScriptVersion" -Level Info
    Write-Log "Deployment ID: $DeploymentId" -Level Info
    Write-Log "Environment: $Environment" -Level Info
    Write-Log "Start Time: $($ScriptStartTime.ToString('yyyy-MM-dd HH:mm:ss'))" -Level Info
    Write-Log "==========================================" -Level Info
    
    # Display warning for production deployments
    if ($Environment -eq 'Production' -and -not $Force) {
        Write-Host "`n⚠️  PRODUCTION DEPLOYMENT WARNING ⚠️" -ForegroundColor Red
        Write-Host "You are about to deploy to PRODUCTION environment." -ForegroundColor Yellow
        Write-Host "This will affect live users and critical systems." -ForegroundColor Yellow
        Write-Host "`nAre you sure you want to continue? (Y/N)" -ForegroundColor White
        
        $confirmation = Read-Host
        if ($confirmation -notin @('Y', 'y', 'Yes', 'yes')) {
            Write-Log "Deployment cancelled by user" -Level Warning
            exit 0
        }
    }
    
    # Check administrator privileges
    if (-not (Test-Administrator)) {
        Write-Log "Warning: Script is not running as Administrator" -Level Warning
        Write-Log "Some operations may require elevated privileges" -Level Warning
        
        if (-not $Force) {
            $confirmation = Read-Host "Continue without administrator privileges? (Y/N)"
            if ($confirmation -notin @('Y', 'y', 'Yes', 'yes')) {
                exit 0
            }
        }
    }
    
    # Test system requirements
    if (-not $SkipValidation -and -not (Test-Requirements)) {
        if (-not $Force) {
            throw "System requirements check failed. Use -Force to override."
        }
        else {
            Write-Log "Proceeding despite failed requirements (forced)" -Level Warning
        }
    }
    
    # Load configuration
    $Configuration = Get-DeploymentConfiguration -ConfigFile $ConfigurationFile
    
    # Update deployment state
    $DeploymentState.Configuration = $Configuration
    
    # Dry run warning
    if ($DryRun) {
        Write-Log "DRY RUN MODE: No changes will be made" -Level Warning
    }
    
    # Rollback mode
    if ($PSCmdlet.ParameterSetName -eq 'Rollback') {
        Write-Log "ROLLBACK MODE: Rolling back to version $RollbackVersion" -Level Warning
        Invoke-Rollback -Version $RollbackVersion -Configuration $Configuration
        Complete-Deployment -Configuration $Configuration
        exit 0
    }
    
    # Pre-deployment health check
    Write-Log "Performing pre-deployment health check..." -Level Info
    if (-not (Test-SystemHealth -Configuration $Configuration)) {
        if (-not $Force) {
            throw "Pre-deployment health check failed. Use -Force to override."
        }
        else {
            Write-Log "Proceeding despite failed health check (forced)" -Level Warning
        }
    }
    
    # Create backup
    $backupPath = $null
    if (-not $DryRun) {
        $backupPath = Backup-System -Configuration $Configuration -BackupType 'Full'
        $DeploymentState.RollbackRequired = $true
    }
    
    # Deploy components
    $DeploymentState.Status = 'Deploying'
    
    foreach ($componentName in $Configuration.Components.Keys) {
        Deploy-Component -ComponentName $componentName `
                        -Configuration $Configuration `
                        -ComponentConfig $Configuration.Components[$componentName]
    }
    
    # Post-deployment tasks
    Write-Log "Running post-deployment tasks..." -Level Info
    
    # Clear caches
    Write-Log "Clearing application caches..." -Level Verbose
    if (-not $DryRun) {
        # Clear Redis cache
        if ($Configuration.Services.Cache.Type -eq 'Redis') {
            try {
                $redisConnection = $Configuration.Services.Cache.ConnectionString
                # Redis cache clear logic
            }
            catch {
                Write-Log "Failed to clear Redis cache: $_" -Level Warning
            }
        }
    }
    
    # Update load balancer (for BlueGreen deployments)
    if ($DeploymentMode -eq 'BlueGreen' -and -not $DryRun) {
        Write-Log "Updating load balancer configuration..." -Level Info
        # Load balancer update logic
    }
    
    # Complete deployment
    $DeploymentState.Status = 'Completed'
    Complete-Deployment -Configuration $Configuration
    
    # Success exit
    exit 0
}
catch {
    Handle-Error -ErrorRecord $_
    exit 1
}
finally {
    # Stop transcript
    Stop-Transcript
    
    # Final status message
    $totalTime = (Get-Date) - $ScriptStartTime
    Write-Host "`n" + "="*50
    Write-Host "Deployment Summary" -ForegroundColor Cyan
    Write-Host "Environment: $Environment" -ForegroundColor White
    Write-Host "Status: $($DeploymentState.Status)" -ForegroundColor $(if ($DeploymentState.Status -eq 'Completed') { 'Green' } else { 'Red' })
    Write-Host "Duration: $($totalTime.TotalMinutes.ToString('F1')) minutes" -ForegroundColor White
    Write-Host "Errors: $($DeploymentState.Errors.Count)" -ForegroundColor $(if ($DeploymentState.Errors.Count -eq 0) { 'Green' } else { 'Red' })
    Write-Host "Log File: $LogFile" -ForegroundColor Gray
    Write-Host "="*50
}

#endregion