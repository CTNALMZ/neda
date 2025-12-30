@echo off
REM ===========================================================================
REM NEDA Windows Setup Script
REM ===========================================================================
REM Industrial-grade Windows installation script for NEDA
REM Neural Engine for Digital Automation
REM Version: 2.1.0
REM ===========================================================================

REM ===========================================================================
REM Configuration
REM ===========================================================================
setlocal enabledelayedexpansion

set "SCRIPT_NAME=NEDA Windows Installer"
set "SCRIPT_VERSION=2.1.0"
set "MIN_WINDOWS_VERSION=10.0.17763"

set "INSTALL_DIR=%ProgramFiles%\NEDA"
set "BIN_DIR=%ProgramFiles%\NEDA\bin"
set "CONFIG_DIR=%ProgramData%\NEDA\config"
set "LOG_DIR=%ProgramData%\NEDA\logs"
set "DATA_DIR=%ProgramData%\NEDA\data"
set "CACHE_DIR=%ProgramData%\NEDA\cache"
set "SERVICE_NAME=NEDAService"
set "SERVICE_DISPLAY_NAME=NEDA AI Engine Service"
set "SERVICE_DESCRIPTION=Neural Engine for Digital Automation - Industrial AI Platform"

set "DOTNET_VERSION=8.0"
set "PYTHON_VERSION=3.10"
set "NODE_VERSION=18"
set "DOCKER_DESKTOP_VERSION=4.25"
set "GIT_VERSION=2.43"
set "CHOCOLATEY_VERSION=1.4.0"

set "REGISTRY_PATH=HKLM\SOFTWARE\NEDA"
set "ENVIRONMENT_REGISTRY=HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment"

set "RED=91"
set "GREEN=92"
set "YELLOW=93"
set "BLUE=94"
set "NC=0"

REM ===========================================================================
REM Color Functions
REM ===========================================================================
:Color
for /F "tokens=1,2 delims=#" %%a in ('"prompt #$H#$E# & echo on & for %%b in (1) do rem"') do (
  set "DEL=%%a"
)
<nul set /p ".=%DEL%" > "%~2"
findstr /v /a:%1 /R "^$" "%~2" nul
del "%~2" > nul 2>&1
exit /b

:PrintColor
echo %~2 | >nul findstr "^"
for %%a in (%~1) do set "COLOR=%%a"
call :Color !COLOR! "%~2"
echo.
exit /b

:LogInfo
call :PrintColor %BLUE% "[INFO] %~1"
exit /b

:LogSuccess
call :PrintColor %GREEN% "[SUCCESS] %~1"
exit /b

:LogWarning
call :PrintColor %YELLOW% "[WARNING] %~1"
exit /b

:LogError
call :PrintColor %RED% "[ERROR] %~1"
exit /b

REM ===========================================================================
REM Utility Functions
REM ===========================================================================
:PrintHeader
echo.
call :PrintColor %GREEN% "╔══════════════════════════════════════════════════════════════════╗"
call :PrintColor %GREEN% "║                    NEDA Windows Installer                         ║"
call :PrintColor %GREEN% "║                    Version: %SCRIPT_VERSION%                      ║"
call :PrintColor %GREEN% "╚══════════════════════════════════════════════════════════════════╝"
echo.
exit /b

:PrintFooter
echo.
call :PrintColor %GREEN% "╔══════════════════════════════════════════════════════════════════╗"
call :PrintColor %GREEN% "║                 Installation Completed Successfully!             ║"
call :PrintColor %GREEN% "╚══════════════════════════════════════════════════════════════════╝"
echo.
exit /b

:CheckAdmin
REM Check if running as administrator
net session >nul 2>&1
if %errorLevel% neq 0 (
    call :LogError "This script must be run as Administrator"
    echo Please right-click on Setup.bat and select "Run as administrator"
    pause
    exit /b 1
)
exit /b

:CheckWindowsVersion
REM Check Windows version
for /f "tokens=4-5 delims=. " %%i in ('ver') do set "VERSION=%%i.%%j"
for /f "tokens=4-5 delims=. " %%i in ('ver') do set "BUILD=%%j"

call :LogInfo "Windows Version: %VERSION% (Build %BUILD%)"

REM Compare versions
for /f "tokens=1-3 delims=." %%a in ("%MIN_WINDOWS_VERSION%") do (
    set "MIN_MAJOR=%%a"
    set "MIN_MINOR=%%b"
    set "MIN_BUILD=%%c"
)

for /f "tokens=1-3 delims=." %%a in ("%VERSION%") do (
    set "CUR_MAJOR=%%a"
    set "CUR_MINOR=%%b"
    set "CUR_BUILD=%%c"
)

if %CUR_MAJOR% LSS %MIN_MAJOR% goto :VersionError
if %CUR_MAJOR% EQU %MIN_MAJOR% (
    if %CUR_MINOR% LSS %MIN_MINOR% goto :VersionError
    if %CUR_MINOR% EQU %MIN_MINOR% (
        if %CUR_BUILD% LSS %MIN_BUILD% goto :VersionError
    )
)
exit /b

:VersionError
call :LogError "Windows version %VERSION% is not supported. Minimum required: %MIN_WINDOWS_VERSION%"
exit /b 1

:CheckDependencies
call :LogInfo "Checking system dependencies..."

set "MISSING_DEPS=0"
set "DEPENDENCIES=wmic reg query powershell curl wget tar git"

for %%D in (%DEPENDENCIES%) do (
    where /q %%D
    if errorlevel 1 (
        call :LogWarning "Missing: %%D"
        set /a MISSING_DEPS+=1
    )
)

if %MISSING_DEPS% EQU 0 (
    call :LogSuccess "All core dependencies are available"
) else (
    call :LogWarning "%MISSING_DEPS% dependencies missing, will attempt to install"
)
exit /b

:InstallChocolatey
call :LogInfo "Installing Chocolatey package manager..."

REM Check if Chocolatey is already installed
where /q choco
if not errorlevel 1 (
    call :LogInfo "Chocolatey is already installed"
    choco upgrade chocolatey -y
    exit /b
)

REM Install Chocolatey
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
    "[System.Net.ServicePointManager]::SecurityProtocol = 3072; ^
    iex ((New-Object System.Net.WebClient).DownloadString('https://chocolatey.org/install.ps1'))"

if errorlevel 1 (
    call :LogError "Failed to install Chocolatey"
    exit /b 1
)

call :LogSuccess "Chocolatey installed successfully"
exit /b

:InstallDependencies
call :LogInfo "Installing system dependencies via Chocolatey..."

REM Define packages to install
set "PACKAGES=git python310 dotnet-8.0-sdk nodejs-lts docker-desktop vscode curl wget 7zip ^
    sysinternals processhacker wireshark nginx redis postgresql14 sqlite"

for %%P in (%PACKAGES%) do (
    call :LogInfo "Installing: %%P"
    choco install %%P -y --no-progress
    if errorlevel 1 (
        call :LogWarning "Failed to install: %%P"
    )
)

REM Install Python packages
call :LogInfo "Installing Python packages..."
pip install --upgrade pip
pip install numpy pandas scipy scikit-learn matplotlib seaborn
pip install torch torchvision torchaudio --index-url https://download.pytorch.org/whl/cu118
pip install tensorflow transformers datasets

call :LogSuccess "Dependencies installed"
exit /b

REM ===========================================================================
REM Directory Functions
REM ===========================================================================
:CreateDirectories
call :LogInfo "Creating installation directories..."

set "DIRECTORIES=%INSTALL_DIR% %BIN_DIR% %CONFIG_DIR% %LOG_DIR% %DATA_DIR% %CACHE_DIR%"

for %%D in (%DIRECTORIES%) do (
    if not exist "%%D" (
        mkdir "%%D"
        call :LogInfo "Created: %%D"
    )
)

REM Set permissions
icacls "%INSTALL_DIR%" /grant "Everyone:(OI)(CI)F" /T
icacls "%PROGRAMDATA%\NEDA" /grant "Everyone:(OI)(CI)F" /T

call :LogSuccess "Directories created"
exit /b

REM ===========================================================================
REGISTRY Functions
REM ===========================================================================
:SetupRegistry
call :LogInfo "Setting up Windows Registry..."

REM Create NEDA registry key
reg add "%REGISTRY_PATH%" /f /v "Version" /t REG_SZ /d "%SCRIPT_VERSION%"
reg add "%REGISTRY_PATH%" /f /v "InstallPath" /t REG_SZ /d "%INSTALL_DIR%"
reg add "%REGISTRY_PATH%" /f /v "ConfigPath" /t REG_SZ /d "%CONFIG_DIR%"
reg add "%REGISTRY_PATH%" /f /v "DataPath" /t REG_SZ /d "%DATA_DIR%"
reg add "%REGISTRY_PATH%" /f /v "InstallDate" /t REG_SZ /d "%DATE% %TIME%"

REM Set environment variables
reg add "%ENVIRONMENT_REGISTRY%" /v "NEDA_HOME" /t REG_SZ /d "%INSTALL_DIR%" /f
reg add "%ENVIRONMENT_REGISTRY%" /v "NEDA_CONFIG_DIR" /t REG_SZ /d "%CONFIG_DIR%" /f
reg add "%ENVIRONMENT_REGISTRY%" /v "NEDA_DATA_DIR" /t REG_SZ /d "%DATA_DIR%" /f
reg add "%ENVIRONMENT_REGISTRY%" /v "NEDA_LOG_DIR" /t REG_SZ /d "%LOG_DIR%" /f

REM Add to PATH
for /f "tokens=2*" %%A in ('reg query "%ENVIRONMENT_REGISTRY%" /v Path 2^>nul') do set "CURRENT_PATH=%%B"
set "NEW_PATH=%CURRENT_PATH%;%BIN_DIR%"
reg add "%ENVIRONMENT_REGISTRY%" /v Path /t REG_EXPAND_SZ /d "%NEW_PATH%" /f

call :LogSuccess "Registry configured"
exit /b

REM ===========================================================================
REM Security Functions
REM ===========================================================================
:SetupSecurity
call :LogInfo "Setting up security configuration..."

REM Create SSL certificates
if not exist "%CONFIG_DIR%\ssl" mkdir "%CONFIG_DIR%\ssl"
pushd "%CONFIG_DIR%\ssl"

REM Generate self-signed certificate
powershell -Command ^
    "$cert = New-SelfSignedCertificate -DnsName 'neda.local', 'localhost' -CertStoreLocation 'Cert:\LocalMachine\My' -KeyExportPolicy Exportable -KeySpec Signature -KeyLength 2048 -KeyAlgorithm RSA -HashAlgorithm SHA256; ^
    Export-Certificate -Cert $cert -FilePath 'neda.crt' -Type CERT; ^
    $pwd = ConvertTo-SecureString -String 'NEDA@2024' -Force -AsPlainText; ^
    Export-PfxCertificate -Cert $cert -FilePath 'neda.pfx' -Password $pwd"

if errorlevel 1 (
    call :LogWarning "Failed to generate SSL certificate"
) else (
    call :LogSuccess "SSL certificates generated"
)

popd

REM Generate encryption keys
echo Generating encryption keys...
openssl rand -base64 32 > "%CONFIG_DIR%\encryption.key" 2>nul
openssl rand -base64 64 > "%CONFIG_DIR%\jwt.secret" 2>nul

REM Create security manifest
echo ^<?xml version="1.0" encoding="UTF-8"?^> > "%CONFIG_DIR%\security.xml"
echo ^<SecurityManifest version="2.1.0"^> >> "%CONFIG_DIR%\security.xml"
echo   ^<Encryption^> >> "%CONFIG_DIR%\security.xml"
echo     ^<Algorithm^>AES-256-GCM^</Algorithm^> >> "%CONFIG_DIR%\security.xml"
echo     ^<KeyPath^>%CONFIG_DIR%\encryption.key^</KeyPath^> >> "%CONFIG_DIR%\security.xml"
echo   ^</Encryption^> >> "%CONFIG_DIR%\security.xml"
echo   ^<Authentication^> >> "%CONFIG_DIR%\security.xml"
echo     ^<JWT^> >> "%CONFIG_DIR%\security.xml"
echo       ^<SecretPath^>%CONFIG_DIR%\jwt.secret^</SecretPath^> >> "%CONFIG_DIR%\security.xml"
echo       ^<ExpiryHours^>24^</ExpiryHours^> >> "%CONFIG_DIR%\security.xml"
echo     ^</JWT^> >> "%CONFIG_DIR%\security.xml"
echo   ^</Authentication^> >> "%CONFIG_DIR%\security.xml"
echo   ^<SSL^> >> "%CONFIG_DIR%\security.xml"
echo     ^<CertificatePath^>%CONFIG_DIR%\ssl\neda.crt^</CertificatePath^> >> "%CONFIG_DIR%\security.xml"
echo     ^<KeyPath^>%CONFIG_DIR%\ssl\neda.pfx^</KeyPath^> >> "%CONFIG_DIR%\security.xml"
echo   ^</SSL^> >> "%CONFIG_DIR%\security.xml"
echo ^</SecurityManifest^> >> "%CONFIG_DIR%\security.xml"

call :LogSuccess "Security configuration completed"
exit /b

REM ===========================================================================
REM Package Installation
REM ===========================================================================
:DownloadPackages
call :LogInfo "Downloading NEDA packages..."

set "DOWNLOAD_URL=https://packages.neda.io/releases/latest"
set "TEMP_DIR=%TEMP%\neda-install"

if not exist "%TEMP_DIR%" mkdir "%TEMP_DIR%"

REM Download core package
call :LogInfo "Downloading core package..."
curl -L -o "%TEMP_DIR%\neda-core.zip" "%DOWNLOAD_URL%/neda-core-windows-latest.zip"
if errorlevel 1 (
    call :LogError "Failed to download core package"
    exit /b 1
)

REM Download AI models
call :LogInfo "Downloading AI models..."
curl -L -o "%TEMP_DIR%\neda-models.zip" "%DOWNLOAD_URL%/neda-models-latest.zip"
if errorlevel 1 (
    call :LogWarning "Failed to download AI models"
)

REM Download plugins
call :LogInfo "Downloading plugins..."
curl -L -o "%TEMP_DIR%\neda-plugins.zip" "%DOWNLOAD_URL%/neda-plugins-windows-latest.zip"
if errorlevel 1 (
    call :LogWarning "Failed to download plugins"
)

exit /b

:ExtractPackages
call :LogInfo "Extracting packages..."

REM Extract core package
if exist "%TEMP_DIR%\neda-core.zip" (
    powershell -Command "Expand-Archive -Path '%TEMP_DIR%\neda-core.zip' -DestinationPath '%INSTALL_DIR%' -Force"
    if errorlevel 1 (
        call :LogError "Failed to extract core package"
        exit /b 1
    )
)

REM Extract models
if exist "%TEMP_DIR%\neda-models.zip" (
    mkdir "%DATA_DIR%\models" 2>nul
    powershell -Command "Expand-Archive -Path '%TEMP_DIR%\neda-models.zip' -DestinationPath '%DATA_DIR%\models' -Force"
    call :LogInfo "AI models extracted"
)

REM Extract plugins
if exist "%TEMP_DIR%\neda-plugins.zip" (
    mkdir "%INSTALL_DIR%\plugins" 2>nul
    powershell -Command "Expand-Archive -Path '%TEMP_DIR%\neda-plugins.zip' -DestinationPath '%INSTALL_DIR%\plugins' -Force"
    call :LogInfo "Plugins extracted"
)

REM Cleanup
rmdir /s /q "%TEMP_DIR%" 2>nul

call :LogSuccess "Packages extracted"
exit /b

REM ===========================================================================
REM .NET Application
REM ===========================================================================
:BuildDotNetApp
call :LogInfo "Building .NET application..."

set "PROJECT_DIR=%INSTALL_DIR%\src\NEDA.Core"

if exist "%PROJECT_DIR%" (
    pushd "%PROJECT_DIR%"
    
    REM Restore dependencies
    call :LogInfo "Restoring NuGet packages..."
    dotnet restore
    if errorlevel 1 (
        call :LogError "Failed to restore packages"
        popd
        exit /b 1
    )
    
    REM Build solution
    call :LogInfo "Building solution..."
    dotnet build -c Release
    if errorlevel 1 (
        call :LogError "Failed to build solution"
        popd
        exit /b 1
    )
    
    REM Publish application
    call :LogInfo "Publishing application..."
    dotnet publish -c Release -o "%BIN_DIR%" --self-contained true
    if errorlevel 1 (
        call :LogError "Failed to publish application"
        popd
        exit /b 1
    )
    
    popd
    
    REM Create shortcuts
    mklink "%BIN_DIR%\neda.exe" "%BIN_DIR%\NEDA.exe" >nul 2>&1
    mklink "%BIN_DIR%\neda-cli.exe" "%BIN_DIR%\NEDA.CLI.exe" >nul 2>&1
    
    call :LogSuccess ".NET application built"
) else (
    call :LogError "Source directory not found"
    exit /b 1
)
exit /b

REM ===========================================================================
REM Database Setup
REM ===========================================================================
:SetupDatabases
call :LogInfo "Setting up databases..."

REM SQLite setup
set "DB_PATH=%DATA_DIR%\databases\neda.db"
if not exist "%DATA_DIR%\databases" mkdir "%DATA_DIR%\databases"

if not exist "%DB_PATH%" (
    call :LogInfo "Creating SQLite database..."
    
    echo Creating database structure...
    sqlite3 "%DB_PATH%" ^
        "CREATE TABLE IF NOT EXISTS users (^
            id INTEGER PRIMARY KEY AUTOINCREMENT,^
            username TEXT UNIQUE NOT NULL,^
            email TEXT UNIQUE NOT NULL,^
            password_hash TEXT NOT NULL,^
            is_active INTEGER DEFAULT 1,^
            is_admin INTEGER DEFAULT 0,^
            created_at DATETIME DEFAULT CURRENT_TIMESTAMP,^
            updated_at DATETIME DEFAULT CURRENT_TIMESTAMP^
        );^
        CREATE TABLE IF NOT EXISTS projects (^
            id INTEGER PRIMARY KEY AUTOINCREMENT,^
            name TEXT NOT NULL,^
            description TEXT,^
            owner_id INTEGER NOT NULL,^
            status TEXT DEFAULT 'active',^
            created_at DATETIME DEFAULT CURRENT_TIMESTAMP,^
            updated_at DATETIME DEFAULT CURRENT_TIMESTAMP,^
            FOREIGN KEY (owner_id) REFERENCES users(id)^
        );^
        INSERT OR IGNORE INTO users (username, email, password_hash, is_admin) ^
        VALUES ('admin', 'admin@neda.local', 'changeme', 1);"
    
    if errorlevel 1 (
        call :LogError "Failed to create database"
        exit /b 1
    )
    
    call :LogSuccess "SQLite database created"
)

REM Redis setup
call :LogInfo "Configuring Redis..."
sc query Redis >nul 2>&1
if errorlevel 1 (
    call :LogWarning "Redis service not found"
) else (
    sc config Redis start= auto
    sc failure Redis reset= 86400 actions= restart/5000/restart/30000/restart/60000
    sc description Redis "NEDA Redis Cache Service"
    net start Redis
)

REM Create database configuration
echo { > "%CONFIG_DIR%\dbconfig.json"
echo   "ConnectionStrings": { >> "%CONFIG_DIR%\dbconfig.json"
echo     "DefaultConnection": "Data Source=%DB_PATH%", >> "%CONFIG_DIR%\dbconfig.json"
echo     "Redis": "localhost:6379,ssl=False,abortConnect=False" >> "%CONFIG_DIR%\dbconfig.json"
echo   }, >> "%CONFIG_DIR%\dbconfig.json"
echo   "DatabaseType": "Sqlite", >> "%CONFIG_DIR%\dbconfig.json"
echo   "AutoMigrate": true, >> "%CONFIG_DIR%\dbconfig.json"
echo   "SeedData": true >> "%CONFIG_DIR%\dbconfig.json"
echo } >> "%CONFIG_DIR%\dbconfig.json"

call :LogSuccess "Database setup completed"
exit /b

REM ===========================================================================
REM Service Configuration
REM ===========================================================================
:CreateWindowsService
call :LogInfo "Creating Windows service..."

REM Check if service already exists
sc query "%SERVICE_NAME%" >nul 2>&1
if not errorlevel 1 (
    call :LogInfo "Service already exists, stopping..."
    net stop "%SERVICE_NAME%" >nul 2>&1
    sc delete "%SERVICE_NAME%" >nul 2>&1
    timeout /t 2 >nul
)

REM Create service
sc create "%SERVICE_NAME%" ^
    binPath= "\"%BIN_DIR%\NEDA.exe\" --service" ^
    DisplayName= "%SERVICE_DISPLAY_NAME%" ^
    start= auto ^
    obj= "LocalSystem" ^
    password= ""

if errorlevel 1 (
    call :LogError "Failed to create service"
    exit /b 1
)

REM Configure service
sc description "%SERVICE_NAME%" "%SERVICE_DESCRIPTION%"
sc failure "%SERVICE_NAME%" reset= 86400 actions= restart/5000/restart/30000/restart/60000
sc config "%SERVICE_NAME%" start= delayed-auto

REM Create service dependencies
sc config "%SERVICE_NAME%" depend= "Redis/DockerDesktopService"

call :LogSuccess "Windows service created"
exit /b

:SetupNginx
call :LogInfo "Configuring Nginx..."

if not exist "C:\nginx" (
    call :LogWarning "Nginx not installed"
    exit /b
)

REM Create Nginx configuration
set "NGINX_CONF=C:\nginx\conf\neda.conf"
echo # NEDA Nginx Configuration > "%NGINX_CONF%"
echo server { >> "%NGINX_CONF%"
echo     listen 80; >> "%NGINX_CONF%"
echo     server_name neda.local localhost; >> "%NGINX_CONF%"
echo. >> "%NGINX_CONF%"
echo     location / { >> "%NGINX_CONF%"
echo         proxy_pass http://localhost:8080; >> "%NGINX_CONF%"
echo         proxy_http_version 1.1; >> "%NGINX_CONF%"
echo         proxy_set_header Upgrade $http_upgrade; >> "%NGINX_CONF%"
echo         proxy_set_header Connection 'upgrade'; >> "%NGINX_CONF%"
echo         proxy_set_header Host $host; >> "%NGINX_CONF%"
echo         proxy_set_header X-Real-IP $remote_addr; >> "%NGINX_CONF%"
echo         proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for; >> "%NGINX_CONF%"
echo         proxy_set_header X-Forwarded-Proto $scheme; >> "%NGINX_CONF%"
echo         proxy_cache_bypass $http_upgrade; >> "%NGINX_CONF%"
echo     } >> "%NGINX_CONF%"
echo } >> "%NGINX_CONF%"

REM Add to main nginx.conf
findstr /C:"include neda.conf;" "C:\nginx\conf\nginx.conf" >nul
if errorlevel 1 (
    echo include neda.conf; >> "C:\nginx\conf\nginx.conf"
)

REM Restart Nginx
taskkill /F /IM nginx.exe >nul 2>&1
start /B "Nginx" C:\nginx\nginx.exe

call :LogSuccess "Nginx configured"
exit /b

:ConfigureFirewall
call :LogInfo "Configuring Windows Firewall..."

REM Add firewall rules
netsh advfirewall firewall add rule ^
    name="NEDA HTTP" ^
    dir=in ^
    action=allow ^
    protocol=TCP ^
    localport=8080 ^
    remoteip=any

netsh advfirewall firewall add rule ^
    name="NEDA HTTPS" ^
    dir=in ^
    action=allow ^
    protocol=TCP ^
    localport=8443 ^
    remoteip=any

netsh advfirewall firewall add rule ^
    name="NEDA gRPC" ^
    dir=in ^
    action=allow ^
    protocol=TCP ^
    localport=50051 ^
    remoteip=any

netsh advfirewall firewall add rule ^
    name="NEDA Metrics" ^
    dir=in ^
    action=allow ^
    protocol=TCP ^
    localport=9090 ^
    remoteip=any

call :LogSuccess "Firewall configured"
exit /b

REM ===========================================================================
REM Configuration Files
REM ===========================================================================
:CreateConfigurationFiles
call :LogInfo "Creating configuration files..."

REM Main configuration
echo { > "%CONFIG_DIR%\neda.config.json"
echo   "System": { >> "%CONFIG_DIR%\neda.config.json"
echo     "Name": "NEDA", >> "%CONFIG_DIR%\neda.config.json"
echo     "Version": "2.1.0", >> "%CONFIG_DIR%\neda.config.json"
echo     "Environment": "Production", >> "%CONFIG_DIR%\neda.config.json"
echo     "InstallationId": "%COMPUTERNAME%-%DATE:~6,4%%DATE:~3,2%%DATE:~0,2%" >> "%CONFIG_DIR%\neda.config.json"
echo   }, >> "%CONFIG_DIR%\neda.config.json"
echo   "Network": { >> "%CONFIG_DIR%\neda.config.json"
echo     "Host": "0.0.0.0", >> "%CONFIG_DIR%\neda.config.json"
echo     "Port": 8080, >> "%CONFIG_DIR%\neda.config.json"
echo     "SslPort": 8443, >> "%CONFIG_DIR%\neda.config.json"
echo     "EnableSsl": true, >> "%CONFIG_DIR%\neda.config.json"
echo     "SslCertificate": "%CONFIG_DIR%\ssl\neda.pfx", >> "%CONFIG_DIR%\neda.config.json"
echo     "SslKey": "NEDA@2024" >> "%CONFIG_DIR%\neda.config.json"
echo   }, >> "%CONFIG_DIR%\neda.config.json"
echo   "Database": { >> "%CONFIG_DIR%\neda.config.json"
echo     "Provider": "Sqlite", >> "%CONFIG_DIR%\neda.config.json"
echo     "ConnectionString": "Data Source=%DATA_DIR%\databases\neda.db", >> "%CONFIG_DIR%\neda.config.json"
echo     "AutoMigrate": true, >> "%CONFIG_DIR%\neda.config.json"
echo     "SeedData": true >> "%CONFIG_DIR%\neda.config.json"
echo   }, >> "%CONFIG_DIR%\neda.config.json"
echo   "Security": { >> "%CONFIG_DIR%\neda.config.json"
echo     "Jwt": { >> "%CONFIG_DIR%\neda.config.json"
echo       "SecretPath": "%CONFIG_DIR%\jwt.secret", >> "%CONFIG_DIR%\neda.config.json"
echo       "ExpiryHours": 24, >> "%CONFIG_DIR%\neda.config.json"
echo       "Issuer": "NEDA System" >> "%CONFIG_DIR%\neda.config.json"
echo     } >> "%CONFIG_DIR%\neda.config.json"
echo   }, >> "%CONFIG_DIR%\neda.config.json"
echo   "Logging": { >> "%CONFIG_DIR%\neda.config.json"
echo     "Level": "Information", >> "%CONFIG_DIR%\neda.config.json"
echo     "File": { >> "%CONFIG_DIR%\neda.config.json"
echo       "Path": "%LOG_DIR%\neda.log", >> "%CONFIG_DIR%\neda.config.json"
echo       "MaxSize": 104857600, >> "%CONFIG_DIR%\neda.config.json"
echo       "MaxFiles": 10 >> "%CONFIG_DIR%\neda.config.json"
echo     } >> "%CONFIG_DIR%\neda.config.json"
echo   } >> "%CONFIG_DIR%\neda.config.json"
echo } >> "%CONFIG_DIR%\neda.config.json"

REM Create first-run script
echo @echo off > "%BIN_DIR%\first-run.bat"
echo echo NEDA First Run Configuration >> "%BIN_DIR%\first-run.bat"
echo echo ============================= >> "%BIN_DIR%\first-run.bat"
echo. >> "%BIN_DIR%\first-run.bat"
echo REM Check if admin password needs to be changed >> "%BIN_DIR%\first-run.bat"
echo sqlite3 "%DATA_DIR%\databases\neda.db" "SELECT password_hash FROM users WHERE username='admin';" ^| findstr "changeme" ^>nul >> "%BIN_DIR%\first-run.bat"
echo if not errorlevel 1 ( >> "%BIN_DIR%\first-run.bat"
echo   echo Admin password is still default. Please change it now. >> "%BIN_DIR%\first-run.bat"
echo   set /p password="Enter new admin password: " >> "%BIN_DIR%\first-run.bat"
echo   set /p password2="Confirm password: " >> "%BIN_DIR%\first-run.bat"
echo   if "!password!"=="!password2!" ( >> "%BIN_DIR%\first-run.bat"
echo     if not "!password!"=="" ( >> "%BIN_DIR%\first-run.bat"
echo       REM Hash password (simplified) >> "%BIN_DIR%\first-run.bat"
echo       echo !password! ^| openssl sha256 ^| awk "{print \$\$2}" ^> "%TEMP%\hash.txt" >> "%BIN_DIR%\first-run.bat"
echo       set /p hash=^<"%TEMP%\hash.txt" >> "%BIN_DIR%\first-run.bat"
echo       sqlite3 "%DATA_DIR%\databases\neda.db" "UPDATE users SET password_hash='!hash!' WHERE username='admin';" >> "%BIN_DIR%\first-run.bat"
echo       echo Admin password updated successfully. >> "%BIN_DIR%\first-run.bat"
echo     ) >> "%BIN_DIR%\first-run.bat"
echo   ) else ( >> "%BIN_DIR%\first-run.bat"
echo     echo Passwords do not match. >> "%BIN_DIR%\first-run.bat"
echo   ) >> "%BIN_DIR%\first-run.bat"
echo ) >> "%BIN_DIR%\first-run.bat"
echo. >> "%BIN_DIR%\first-run.bat"
echo echo Generating API key... >> "%BIN_DIR%\first-run.bat"
echo openssl rand -hex 32 ^> "%TEMP%\api_key.txt" >> "%BIN_DIR%\first-run.bat"
echo set /p api_key=^<"%TEMP%\api_key.txt" >> "%BIN_DIR%\first-run.bat"
echo sqlite3 "%DATA_DIR%\databases\neda.db" "INSERT OR REPLACE INTO configurations (key, value) VALUES ('api.default_key', '!api_key!');" >> "%BIN_DIR%\first-run.bat"
echo echo API Key generated: !api_key! >> "%BIN_DIR%\first-run.bat"
echo echo Save this key for API access. >> "%BIN_DIR%\first-run.bat"
echo. >> "%BIN_DIR%\first-run.bat"
echo echo First run configuration completed. >> "%BIN_DIR%\first-run.bat"

call :LogSuccess "Configuration files created"
exit /b

REM ===========================================================================
REM Final Setup
REM ===========================================================================
:SetPermissions
call :LogInfo "Setting final permissions..."

REM Set directory permissions
icacls "%INSTALL_DIR%" /grant "Everyone:(OI)(CI)F" /T
icacls "%CONFIG_DIR%" /grant "Everyone:(OI)(CI)R" /T
icacls "%LOG_DIR%" /grant "Everyone:(OI)(CI)W" /T
icacls "%DATA_DIR%" /grant "Everyone:(OI)(CI)F" /T

REM Set file permissions
attrib +R "%CONFIG_DIR%\*.json" /S
attrib +R "%CONFIG_DIR%\security.xml"

call :LogSuccess "Permissions set"
exit /b

:VerifyInstallation
call :LogInfo "Verifying installation..."

set "ERRORS=0"

REM Check if binaries exist
if not exist "%BIN_DIR%\NEDA.exe" (
    call :LogError "Binary not found: NEDA.exe"
    set /a ERRORS+=1
)

if not exist "%BIN_DIR%\neda.exe" (
    call :LogError "Binary not found: neda.exe"
    set /a ERRORS+=1
)

REM Check if service exists
sc query "%SERVICE_NAME%" >nul 2>&1
if errorlevel 1 (
    call :LogError "Service not installed"
    set /a ERRORS+=1
)

REM Check if directories exist
for %%D in ("%INSTALL_DIR%" "%CONFIG_DIR%" "%LOG_DIR%" "%DATA_DIR%") do (
    if not exist %%D (
        call :LogError "Directory not found: %%D"
        set /a ERRORS+=1
    )
)

REM Test database
if exist "%DATA_DIR%\databases\neda.db" (
    sqlite3 "%DATA_DIR%\databases\neda.db" "SELECT 1;" >nul 2>&1
    if errorlevel 1 (
        call :LogError "Database connection test failed"
        set /a ERRORS+=1
    )
)

REM Test SSL certificates
if not exist "%CONFIG_DIR%\ssl\neda.pfx" (
    call :LogError "SSL certificate not found"
    set /a ERRORS+=1
)

if %ERRORS% EQU 0 (
    call :LogSuccess "Installation verification passed"
) else (
    call :LogError "Installation verification failed with %ERRORS% error(s)"
    exit /b 1
)
exit /b

:StartServices
call :LogInfo "Starting NEDA services..."

REM Start Docker Desktop
sc query DockerDesktopService >nul 2>&1
if not errorlevel 1 (
    net start DockerDesktopService >nul 2>&1
    call :LogInfo "Docker Desktop service started"
)

REM Start Redis
sc query Redis >nul 2>&1
if not errorlevel 1 (
    net start Redis >nul 2>&1
    call :LogInfo "Redis service started"
)

REM Start NEDA service
net start "%SERVICE_NAME%" >nul 2>&1
if errorlevel 1 (
    call :LogError "Failed to start NEDA service"
    exit /b 1
)

REM Wait for service to start
set "MAX_ATTEMPTS=30"
set "ATTEMPT=1"

:WaitForService
sc query "%SERVICE_NAME%" | findstr "RUNNING" >nul
if not errorlevel 1 goto :ServiceStarted

call :LogInfo "Waiting for service to start (attempt %ATTEMPT%/%MAX_ATTEMPTS%)..."
timeout /t 2 >nul
set /a ATTEMPT+=1
if %ATTEMPT% LEQ %MAX_ATTEMPTS% goto :WaitForService

call :LogError "Service failed to start within 60 seconds"
exit /b 1

:ServiceStarted
call :LogSuccess "NEDA service started successfully"

REM Run first-time setup
call :LogInfo "Running first-time setup..."
call "%BIN_DIR%\first-run.bat"

exit /b

REM ===========================================================================
REM Main Installation Function
REM ===========================================================================
:Main
call :PrintHeader

call :LogInfo "Starting NEDA installation on Windows"

REM Pre-flight checks
call :CheckAdmin
call :CheckWindowsVersion
call :CheckDependencies

REM Installation steps
call :InstallChocolatey
call :InstallDependencies
call :CreateDirectories
call :SetupRegistry
call :SetupSecurity

REM Download and extract packages
call :DownloadPackages
call :ExtractPackages

REM Install components
call :BuildDotNetApp
call :SetupDatabases
call :CreateConfigurationFiles

REM System integration
call :CreateWindowsService
call :SetupNginx
call :ConfigureFirewall
call :SetPermissions

REM Final steps
call :VerifyInstallation
call :StartServices

call :PrintFooter

REM Display installation summary
echo.
call :PrintColor %GREEN% "╔══════════════════════════════════════════════════════════════════╗"
call :PrintColor %GREEN% "║                     Installation Summary                          ║"
call :PrintColor %GREEN% "╠══════════════════════════════════════════════════════════════════╣"
call :PrintColor %GREEN% "║                                                                  ║"
call :PrintColor %GREEN% "║  NEDA has been successfully installed!                           ║"
call :PrintColor %GREEN% "║                                                                  ║"
call :PrintColor %GREEN% "║  Installation Directory: %INSTALL_DIR%                           ║"
call :PrintColor %GREEN% "║  Configuration Directory: %CONFIG_DIR%                           ║"
call :PrintColor %GREEN% "║  Data Directory: %DATA_DIR%                                      ║"
call :PrintColor %GREEN% "║  Log Directory: %LOG_DIR%                                        ║"
call :PrintColor %GREEN% "║                                                                  ║"
call :PrintColor %GREEN% "║  Services:                                                       ║"
call :PrintColor %GREEN% "║    • %SERVICE_NAME% - Main NEDA service                         ║"
call :PrintColor %GREEN% "║    • Redis - Cache service                                      ║"
call :PrintColor %GREEN% "║    • DockerDesktopService - Container runtime                   ║"
call :PrintColor %GREEN% "║                                                                  ║"
call :PrintColor %GREEN% "║  Web Interface:                                                  ║"
call :PrintColor %GREEN% "║    • HTTPS: https://localhost:8443                               ║"
call :PrintColor %GREEN% "║    • HTTP:  http://localhost:8080                                ║"
call :PrintColor %GREEN% "║    • Nginx: http://localhost:80                                  ║"
call :PrintColor %GREEN% "║                                                                  ║"
call :PrintColor %GREEN% "║  Next Steps:                                                     ║"
call :PrintColor %GREEN% "║    1. Access the web interface at https://localhost:8443         ║"
call :PrintColor %GREEN% "║    2. Login with username: admin                                 ║"
call :PrintColor %GREEN% "║    3. Change the default password immediately                    ║"
call :PrintColor %GREEN% "║    4. Configure your environment in %CONFIG_DIR%                 ║"
call :PrintColor %GREEN% "║    5. Check service status: sc query %SERVICE_NAME%             ║"
call :PrintColor %GREEN% "║                                                                  ║"
call :PrintColor %GREEN% "║  Useful Commands:                                                ║"
call :PrintColor %GREEN% "║    • Start/Stop: net start/stop %SERVICE_NAME%                  ║"
call :PrintColor %GREEN% "║    • Status: sc query %SERVICE_NAME%                            ║"
call :PrintColor %GREEN% "║    • Logs: Get-EventLog -LogName Application -Source NEDA       ║"
call :PrintColor %GREEN% "║    • CLI: neda-cli --help                                       ║"
call :PrintColor %GREEN% "║                                                                  ║"
call :PrintColor %GREEN% "╚══════════════════════════════════════════════════════════════════╝"
echo.
call :LogSuccess "Installation completed at %DATE% %TIME%"

REM Create uninstall script
echo @echo off > "%INSTALL_DIR%\uninstall.bat"
echo REM NEDA Uninstall Script >> "%INSTALL_DIR%\uninstall.bat"
echo net stop "%SERVICE_NAME%" ^>nul 2^>^&1 >> "%INSTALL_DIR%\uninstall.bat"
echo sc delete "%SERVICE_NAME%" ^>nul 2^>^&1 >> "%INSTALL_DIR%\uninstall.bat"
echo rmdir /s /q "%INSTALL_DIR%" ^>nul 2^>^&1 >> "%INSTALL_DIR%\uninstall.bat"
echo rmdir /s /q "%CONFIG_DIR%" ^>nul 2^>^&1 >> "%INSTALL_DIR%\uninstall.bat"
echo reg delete "%REGISTRY_PATH%" /f ^>nul 2^>^&1 >> "%INSTALL_DIR%\uninstall.bat"
echo echo NEDA has been uninstalled. >> "%INSTALL_DIR%\uninstall.bat"
echo pause >> "%INSTALL_DIR%\uninstall.bat"

echo.
echo To uninstall NEDA, run: "%INSTALL_DIR%\uninstall.bat"
echo.

pause
exit /b 0

REM ===========================================================================
REM Entry Point
REM ===========================================================================
call :Main
if errorlevel 1 (
    call :LogError "Installation failed with error code %errorlevel%"
    pause
    exit /b %errorlevel%
)