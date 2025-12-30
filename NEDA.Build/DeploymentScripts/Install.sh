#!/bin/bash
#!/usr/bin/env bash

# =============================================================================
# NEDA Installation Script
# =============================================================================
# Industrial-grade installation and setup script for NEDA (Neural Engine for Digital Automation)
# Version: 2.1.0
# =============================================================================

set -euo pipefail
IFS=$'\n\t'

# =============================================================================
# Configuration
# =============================================================================

readonly SCRIPT_NAME="NEDA Installer"
readonly SCRIPT_VERSION="2.1.0"
readonly MIN_BASH_VERSION=4

# Installation directories
readonly INSTALL_DIR="/opt/neda"
readonly BIN_DIR="/usr/local/bin"
readonly CONFIG_DIR="/etc/neda"
readonly LOG_DIR="/var/log/neda"
readonly DATA_DIR="/var/lib/neda"
readonly CACHE_DIR="/var/cache/neda"

# User and group for service
readonly NEDA_USER="neda"
readonly NEDA_GROUP="neda"

# Colors for output
readonly RED='\033[0;31m'
readonly GREEN='\033[0;32m'
readonly YELLOW='\033[1;33m'
readonly BLUE='\033[0;34m'
readonly NC='\033[0m' # No Color

# Platform detection
readonly OS_TYPE=$(uname -s)
readonly ARCH_TYPE=$(uname -m)
readonly DISTRO=$(grep '^ID=' /etc/os-release 2>/dev/null | cut -d= -f2 | tr -d '"' || echo "unknown")

# =============================================================================
# Logging Functions
# =============================================================================

log_info() {
    echo -e "${BLUE}[INFO]${NC} $*"
}

log_success() {
    echo -e "${GREEN}[SUCCESS]${NC} $*"
}

log_warning() {
    echo -e "${YELLOW}[WARNING]${NC} $*"
}

log_error() {
    echo -e "${RED}[ERROR]${NC} $*" >&2
}

log_debug() {
    if [[ "${DEBUG:-false}" == "true" ]]; then
        echo -e "${BLUE}[DEBUG]${NC} $*"
    fi
}

# =============================================================================
# Utility Functions
# =============================================================================

print_header() {
    echo -e "${GREEN}"
    echo "╔══════════════════════════════════════════════════════════════════╗"
    echo "║                    NEDA Installation Script                      ║"
    echo "║                    Version: $SCRIPT_VERSION                          ║"
    echo "╚══════════════════════════════════════════════════════════════════╝"
    echo -e "${NC}"
    echo ""
}

print_footer() {
    echo ""
    echo -e "${GREEN}"
    echo "╔══════════════════════════════════════════════════════════════════╗"
    echo "║                 Installation Completed Successfully!             ║"
    echo "╚══════════════════════════════════════════════════════════════════╝"
    echo -e "${NC}"
    echo ""
}

check_root() {
    if [[ $EUID -ne 0 ]]; then
        log_error "This script must be run as root"
        echo "Please run: sudo $0"
        exit 1
    fi
}

check_bash_version() {
    local bash_version
    bash_version=$(bash --version | head -1 | grep -oE '[0-9]+\.[0-9]+\.[0-9]+' | head -1)
    local major_version=${bash_version%%.*}
    
    if [[ $major_version -lt $MIN_BASH_VERSION ]]; then
        log_error "Bash version $bash_version is too old. Minimum required: $MIN_BASH_VERSION"
        exit 1
    fi
}

check_dependencies() {
    log_info "Checking system dependencies..."
    
    local missing_deps=()
    local required_cmds=("curl" "wget" "tar" "gzip" "git" "python3" "dotnet" "docker" "openssl")
    
    for cmd in "${required_cmds[@]}"; do
        if ! command -v "$cmd" &> /dev/null; then
            missing_deps+=("$cmd")
        fi
    done
    
    if [[ ${#missing_deps[@]} -gt 0 ]]; then
        log_warning "Missing dependencies: ${missing_deps[*]}"
        return 1
    fi
    
    log_success "All dependencies are available"
    return 0
}

install_missing_dependencies() {
    log_info "Installing missing dependencies..."
    
    case "$DISTRO" in
        ubuntu|debian)
            apt-get update
            apt-get install -y curl wget tar gzip git python3 python3-pip \
                dotnet-sdk-6.0 docker.io openssl ca-certificates \
                build-essential libssl-dev libffi-dev python3-dev \
                sqlite3 libsqlite3-dev redis-server nginx
            ;;
        centos|rhel|fedora)
            dnf install -y curl wget tar gzip git python3 python3-pip \
                dotnet-sdk-6.0 docker openssl ca-certificates \
                gcc-c++ openssl-devel libffi-devel python3-devel \
                sqlite sqlite-devel redis nginx
            ;;
        arch)
            pacman -Syu --noconfirm curl wget tar gzip git python python-pip \
                dotnet-sdk docker openssl ca-certificates \
                base-devel openssl libffi sqlite redis nginx
            ;;
        *)
            log_error "Unsupported distribution: $DISTRO"
            log_info "Please install dependencies manually:"
            log_info "- curl, wget, tar, gzip, git"
            log_info "- Python 3.8+ with pip"
            log_info "- .NET 6.0 SDK"
            log_info "- Docker"
            log_info "- OpenSSL"
            log_info "- SQLite3"
            log_info "- Redis"
            log_info "- Nginx"
            return 1
            ;;
    esac
    
    # Enable and start Docker
    systemctl enable --now docker
    log_success "Dependencies installed"
}

# =============================================================================
# System Configuration
# =============================================================================

create_user_and_group() {
    log_info "Creating NEDA user and group..."
    
    if ! getent group "$NEDA_GROUP" > /dev/null; then
        groupadd --system "$NEDA_GROUP"
        log_debug "Created group: $NEDA_GROUP"
    fi
    
    if ! id "$NEDA_USER" > /dev/null 2>&1; then
        useradd --system --gid "$NEDA_GROUP" --shell /bin/false \
                --home-dir "$INSTALL_DIR" --comment "NEDA Service Account" "$NEDA_USER"
        log_debug "Created user: $NEDA_USER"
    fi
    
    log_success "User and group configured"
}

create_directories() {
    log_info "Creating installation directories..."
    
    local directories=(
        "$INSTALL_DIR"
        "$INSTALL_DIR/bin"
        "$INSTALL_DIR/lib"
        "$INSTALL_DIR/etc"
        "$INSTALL_DIR/plugins"
        "$INSTALL_DIR/temp"
        "$CONFIG_DIR"
        "$CONFIG_DIR/security"
        "$CONFIG_DIR/databases"
        "$CONFIG_DIR/services"
        "$LOG_DIR"
        "$DATA_DIR"
        "$DATA_DIR/databases"
        "$DATA_DIR/storage"
        "$DATA_DIR/models"
        "$CACHE_DIR"
        "$CACHE_DIR/packages"
        "$CACHE_DIR/temp"
    )
    
    for dir in "${directories[@]}"; do
        mkdir -p "$dir"
        chmod 755 "$dir"
        log_debug "Created directory: $dir"
    done
    
    # Set ownership
    chown -R "$NEDA_USER:$NEDA_GROUP" "$INSTALL_DIR" "$CONFIG_DIR" "$LOG_DIR" "$DATA_DIR" "$CACHE_DIR"
    
    log_success "Directories created and permissions set"
}

setup_environment() {
    log_info "Setting up environment..."
    
    # Create environment file
    cat > "$CONFIG_DIR/environment" << EOF
# NEDA Environment Configuration
# Generated on $(date)

# Paths
NEDA_HOME=$INSTALL_DIR
NEDA_CONFIG_DIR=$CONFIG_DIR
NEDA_DATA_DIR=$DATA_DIR
NEDA_LOG_DIR=$LOG_DIR
NEDA_CACHE_DIR=$CACHE_DIR

# Service Configuration
NEDA_USER=$NEDA_USER
NEDA_GROUP=$NEDA_GROUP

# Network
NEDA_HOST=0.0.0.0
NEDA_PORT=8080
NEDA_SSL_PORT=8443

# Database
NEDA_DB_TYPE=sqlite
NEDA_DB_PATH=$DATA_DIR/databases/neda.db
NEDA_REDIS_HOST=localhost
NEDA_REDIS_PORT=6379

# Security
NEDA_ENCRYPTION_KEY_PATH=$CONFIG_DIR/security/encryption.key
NEDA_JWT_SECRET_PATH=$CONFIG_DIR/security/jwt.secret
NEDA_SSL_CERT_PATH=$CONFIG_DIR/security/cert.pem
NEDA_SSL_KEY_PATH=$CONFIG_DIR/security/key.pem

# Features
NEDA_ENABLE_AI=true
NEDA_ENABLE_SECURITY=true
NEDA_ENABLE_MONITORING=true
NEDA_ENABLE_API=true
NEDA_ENABLE_UI=true

# Performance
NEDA_MAX_MEMORY=4096M
NEDA_THREAD_COUNT=$(nproc)
NEDA_WORKER_PROCESSES=4

# Logging
NEDA_LOG_LEVEL=INFO
NEDA_LOG_MAX_SIZE=100MB
NEDA_LOG_MAX_FILES=10
EOF
    
    # Source environment for current shell
    if [[ -f "$CONFIG_DIR/environment" ]]; then
        set -a
        source "$CONFIG_DIR/environment"
        set +a
    fi
    
    # Add to profile
    if ! grep -q "NEDA_HOME" /etc/environment; then
        echo "NEDA_HOME=$INSTALL_DIR" >> /etc/environment
        echo "PATH=\$PATH:$INSTALL_DIR/bin" >> /etc/environment
    fi
    
    log_success "Environment configured"
}

# =============================================================================
# Security Setup
# =============================================================================

setup_security() {
    log_info "Setting up security configuration..."
    
    # Create SSL certificates
    if [[ ! -f "$NEDA_SSL_CERT_PATH" ]] || [[ ! -f "$NEDA_SSL_KEY_PATH" ]]; then
        log_info "Generating SSL certificates..."
        openssl req -x509 -nodes -days 365 -newkey rsa:2048 \
            -keyout "$NEDA_SSL_KEY_PATH" \
            -out "$NEDA_SSL_CERT_PATH" \
            -subj "/C=US/ST=State/L=City/O=NEDA/CN=neda.local" \
            -addext "subjectAltName=DNS:localhost,IP:127.0.0.1" 2>/dev/null
        chmod 600 "$NEDA_SSL_KEY_PATH"
        chmod 644 "$NEDA_SSL_CERT_PATH"
        chown "$NEDA_USER:$NEDA_GROUP" "$NEDA_SSL_KEY_PATH" "$NEDA_SSL_CERT_PATH"
    fi
    
    # Generate encryption key
    if [[ ! -f "$NEDA_ENCRYPTION_KEY_PATH" ]]; then
        openssl rand -base64 32 > "$NEDA_ENCRYPTION_KEY_PATH"
        chmod 600 "$NEDA_ENCRYPTION_KEY_PATH"
        chown "$NEDA_USER:$NEDA_GROUP" "$NEDA_ENCRYPTION_KEY_PATH"
    fi
    
    # Generate JWT secret
    if [[ ! -f "$NEDA_JWT_SECRET_PATH" ]]; then
        openssl rand -base64 64 > "$NEDA_JWT_SECRET_PATH"
        chmod 600 "$NEDA_JWT_SECRET_PATH"
        chown "$NEDA_USER:$NEDA_GROUP" "$NEDA_JWT_SECRET_PATH"
    fi
    
    # Create security manifest
    cat > "$CONFIG_DIR/security/manifest.xml" << EOF
<?xml version="1.0" encoding="UTF-8"?>
<SecurityManifest version="2.1.0">
    <Encryption>
        <Algorithm>AES-256-GCM</Algorithm>
        <KeyPath>$NEDA_ENCRYPTION_KEY_PATH</KeyPath>
    </Encryption>
    <Authentication>
        <JWT>
            <SecretPath>$NEDA_JWT_SECRET_PATH</SecretPath>
            <ExpiryHours>24</ExpiryHours>
            <Issuer>NEDA System</Issuer>
        </JWT>
        <MultiFactor>true</MultiFactor>
        <SessionTimeout>3600</SessionTimeout>
    </Authentication>
    <SSL>
        <CertificatePath>$NEDA_SSL_CERT_PATH</CertificatePath>
        <KeyPath>$NEDA_SSL_KEY_PATH</KeyPath>
        <Protocols>TLSv1.2 TLSv1.3</Protocols>
    </SSL>
    <Firewall>
        <Enabled>true</Enabled>
        <DefaultPolicy>deny</DefaultPolicy>
        <AllowedPorts>22,80,443,8080,8443</AllowedPorts>
    </Firewall>
    <Audit>
        <Enabled>true</Enabled>
        <LogPath>$LOG_DIR/security</LogPath>
        <RetentionDays>90</RetentionDays>
    </Audit>
</SecurityManifest>
EOF
    
    # Configure SELinux/AppArmor if available
    if command -v sestatus &> /dev/null; then
        log_info "Configuring SELinux..."
        semanage fcontext -a -t bin_t "$INSTALL_DIR/bin(/.*)?"
        restorecon -Rv "$INSTALL_DIR"
    fi
    
    if command -v aa-status &> /dev/null; then
        log_info "Configuring AppArmor..."
        # Create AppArmor profile
        cat > /etc/apparmor.d/neda << EOF
#include <tunables/global>

$INSTALL_DIR/bin/** {
  #include <abstractions/base>
  
  $INSTALL_DIR/bin/* rmix,
  $INSTALL_DIR/lib/** r,
  $CONFIG_DIR/** rw,
  $DATA_DIR/** rw,
  $LOG_DIR/** rw,
  $CACHE_DIR/** rw,
  
  network inet tcp,
  network inet6 tcp,
  
  /proc/* r,
  /sys/** r,
}
EOF
        apparmor_parser -r /etc/apparmor.d/neda
    fi
    
    log_success "Security configuration completed"
}

# =============================================================================
# Package Installation
# =============================================================================

download_packages() {
    local package_url="https://packages.neda.io/releases/latest"
    local temp_dir=$(mktemp -d)
    
    log_info "Downloading NEDA packages..."
    
    # Download core package
    log_info "Downloading core package..."
    if ! curl -fsSL -o "$temp_dir/neda-core.tar.gz" "$package_url/neda-core-latest.tar.gz"; then
        log_error "Failed to download core package"
        return 1
    fi
    
    # Download AI models
    log_info "Downloading AI models..."
    if ! curl -fsSL -o "$temp_dir/neda-models.tar.gz" "$package_url/neda-models-latest.tar.gz"; then
        log_warning "Failed to download AI models"
    fi
    
    # Download plugins
    log_info "Downloading plugins..."
    if ! curl -fsSL -o "$temp_dir/neda-plugins.tar.gz" "$package_url/neda-plugins-latest.tar.gz"; then
        log_warning "Failed to download plugins"
    fi
    
    echo "$temp_dir"
}

extract_packages() {
    local temp_dir=$1
    
    log_info "Extracting packages..."
    
    # Extract core package
    tar -xzf "$temp_dir/neda-core.tar.gz" -C "$INSTALL_DIR" --strip-components=1
    if [[ $? -ne 0 ]]; then
        log_error "Failed to extract core package"
        return 1
    fi
    
    # Extract models if available
    if [[ -f "$temp_dir/neda-models.tar.gz" ]]; then
        mkdir -p "$DATA_DIR/models"
        tar -xzf "$temp_dir/neda-models.tar.gz" -C "$DATA_DIR/models" --strip-components=1
        log_debug "AI models extracted"
    fi
    
    # Extract plugins if available
    if [[ -f "$temp_dir/neda-plugins.tar.gz" ]]; then
        mkdir -p "$INSTALL_DIR/plugins"
        tar -xzf "$temp_dir/neda-plugins.tar.gz" -C "$INSTALL_DIR/plugins" --strip-components=1
        log_debug "Plugins extracted"
    fi
    
    # Cleanup
    rm -rf "$temp_dir"
    
    log_success "Packages extracted"
}

install_dotnet_app() {
    log_info "Building and installing .NET application..."
    
    local project_dir="$INSTALL_DIR/src/NEDA.Core"
    
    if [[ -d "$project_dir" ]]; then
        cd "$project_dir"
        
        # Restore dependencies
        log_info "Restoring NuGet packages..."
        dotnet restore
        
        # Build solution
        log_info "Building solution..."
        dotnet build -c Release
        
        # Publish application
        log_info "Publishing application..."
        dotnet publish -c Release -o "$INSTALL_DIR/bin" --self-contained true
        
        # Create symlinks
        ln -sf "$INSTALL_DIR/bin/NEDA" "$BIN_DIR/neda"
        ln -sf "$INSTALL_DIR/bin/NEDA.CLI" "$BIN_DIR/neda-cli"
        
        chmod +x "$INSTALL_DIR/bin/NEDA"
        chmod +x "$INSTALL_DIR/bin/NEDA.CLI"
        
        log_success ".NET application installed"
    else
        log_error "Source directory not found: $project_dir"
        return 1
    fi
}

install_python_packages() {
    log_info "Installing Python packages..."
    
    local requirements_file="$INSTALL_DIR/requirements.txt"
    
    if [[ -f "$requirements_file" ]]; then
        pip3 install --upgrade pip
        pip3 install -r "$requirements_file"
        
        # Install additional ML packages
        pip3 install torch torchvision torchaudio --index-url https://download.pytorch.org/whl/cpu
        pip3 install transformers datasets scikit-learn pandas numpy
        
        log_success "Python packages installed"
    else
        log_warning "Python requirements file not found"
    fi
}

# =============================================================================
# Database Setup
# =============================================================================

setup_databases() {
    log_info "Setting up databases..."
    
    # SQLite setup
    local db_path="$DATA_DIR/databases/neda.db"
    
    if [[ ! -f "$db_path" ]]; then
        log_info "Creating SQLite database..."
        sqlite3 "$db_path" << EOF
-- NEDA Core Database Schema
CREATE TABLE IF NOT EXISTS users (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    username TEXT UNIQUE NOT NULL,
    email TEXT UNIQUE NOT NULL,
    password_hash TEXT NOT NULL,
    is_active INTEGER DEFAULT 1,
    is_admin INTEGER DEFAULT 0,
    created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
    updated_at DATETIME DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS projects (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL,
    description TEXT,
    owner_id INTEGER NOT NULL,
    status TEXT DEFAULT 'active',
    created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
    updated_at DATETIME DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (owner_id) REFERENCES users(id)
);

CREATE TABLE IF NOT EXISTS tasks (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    project_id INTEGER NOT NULL,
    name TEXT NOT NULL,
    description TEXT,
    status TEXT DEFAULT 'pending',
    priority INTEGER DEFAULT 0,
    assigned_to INTEGER,
    created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
    updated_at DATETIME DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (project_id) REFERENCES projects(id),
    FOREIGN KEY (assigned_to) REFERENCES users(id)
);

CREATE TABLE IF NOT EXISTS logs (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    level TEXT NOT NULL,
    source TEXT NOT NULL,
    message TEXT NOT NULL,
    timestamp DATETIME DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS configurations (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL,
    description TEXT,
    updated_at DATETIME DEFAULT CURRENT_TIMESTAMP
);

-- Insert default admin user (password will be changed on first login)
INSERT OR IGNORE INTO users (username, email, password_hash, is_admin) 
VALUES ('admin', 'admin@neda.local', 'changeme', 1);

-- Insert default configurations
INSERT OR IGNORE INTO configurations (key, value, description) VALUES
('system.version', '2.1.0', 'NEDA System Version'),
('security.require_mfa', 'false', 'Require Multi-Factor Authentication'),
('logging.level', 'INFO', 'Default log level'),
('api.rate_limit', '100', 'API rate limit per minute');
EOF
        
        chown "$NEDA_USER:$NEDA_GROUP" "$db_path"
        log_success "SQLite database created"
    fi
    
    # Redis setup
    log_info "Configuring Redis..."
    
    # Update Redis configuration
    if [[ -f /etc/redis/redis.conf ]]; then
        sed -i 's/^bind 127.0.0.1/bind 0.0.0.0/' /etc/redis/redis.conf
        sed -i 's/^# requirepass.*/requirepass neda_redis_pass/' /etc/redis/redis.conf
        systemctl restart redis
        log_success "Redis configured"
    fi
    
    # Create database configuration
    cat > "$CONFIG_DIR/databases/dbconfig.json" << EOF
{
    "ConnectionStrings": {
        "DefaultConnection": "Data Source=$DATA_DIR/databases/neda.db",
        "Redis": "localhost:6379,password=neda_redis_pass,ssl=False,abortConnect=False"
    },
    "DatabaseType": "Sqlite",
    "AutoMigrate": true,
    "SeedData": true,
    "PoolSize": 10,
    "CommandTimeout": 30
}
EOF
    
    log_success "Database setup completed"
}

# =============================================================================
# Service Configuration
# =============================================================================

create_systemd_service() {
    log_info "Creating systemd service..."
    
    cat > /etc/systemd/system/neda.service << EOF
[Unit]
Description=NEDA (Neural Engine for Digital Automation)
Documentation=https://docs.neda.io
After=network.target redis.service docker.service
Requires=redis.service
Wants=docker.service

[Service]
Type=exec
User=$NEDA_USER
Group=$NEDA_GROUP
EnvironmentFile=$CONFIG_DIR/environment

# Runtime directory
RuntimeDirectory=neda
RuntimeDirectoryMode=0755

# Working directory
WorkingDirectory=$INSTALL_DIR
ExecStartPre=/bin/bash -c 'echo "Starting NEDA at \$(date)" >> $LOG_DIR/neda-startup.log'
ExecStart=$INSTALL_DIR/bin/NEDA --config $CONFIG_DIR/neda.config.json
ExecReload=/bin/kill -HUP \$MAINPID
ExecStop=/bin/kill -TERM \$MAINPID

# Security
NoNewPrivileges=true
PrivateTmp=true
PrivateDevices=true
ProtectSystem=strict
ProtectHome=true
ProtectControlGroups=true
ProtectKernelModules=true
ProtectKernelTunables=true
RestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX
RestrictNamespaces=true
RestrictRealtime=true
SystemCallArchitectures=native
MemoryDenyWriteExecute=true
LockPersonality=true

# Resource limits
LimitNOFILE=65536
LimitNPROC=4096
LimitCORE=0
LimitMEMLOCK=64M

# Restart policy
Restart=on-failure
RestartSec=10s
StartLimitInterval=60s
StartLimitBurst=3

# Standard output
StandardOutput=journal
StandardError=journal
SyslogIdentifier=neda

[Install]
WantedBy=multi-user.target
EOF
    
    # Create CLI service
    cat > /etc/systemd/system/neda-cli.service << EOF
[Unit]
Description=NEDA CLI Service
After=neda.service
Requires=neda.service

[Service]
Type=oneshot
RemainAfterExit=yes
User=$NEDA_USER
Group=$NEDA_GROUP
EnvironmentFile=$CONFIG_DIR/environment
WorkingDirectory=$INSTALL_DIR
ExecStart=$BIN_DIR/neda-cli initialize
SyslogIdentifier=neda-cli

[Install]
WantedBy=multi-user.target
EOF
    
    # Create timer for maintenance
    cat > /etc/systemd/system/neda-maintenance.timer << EOF
[Unit]
Description=NEDA Maintenance Timer
Requires=neda.service

[Timer]
OnCalendar=daily
Persistent=true

[Install]
WantedBy=timers.target
EOF
    
    cat > /etc/systemd/system/neda-maintenance.service << EOF
[Unit]
Description=NEDA Maintenance Service
After=neda.service

[Service]
Type=oneshot
User=$NEDA_USER
Group=$NEDA_GROUP
EnvironmentFile=$CONFIG_DIR/environment
WorkingDirectory=$INSTALL_DIR
ExecStart=$BIN_DIR/neda-cli maintenance
SyslogIdentifier=neda-maintenance
EOF
    
    systemctl daemon-reload
    log_success "Systemd services created"
}

setup_nginx() {
    log_info "Configuring Nginx reverse proxy..."
    
    if ! command -v nginx &> /dev/null; then
        log_warning "Nginx not installed, skipping reverse proxy setup"
        return 0
    fi
    
    cat > /etc/nginx/sites-available/neda << EOF
# NEDA Nginx Configuration
server {
    listen 80;
    listen [::]:80;
    server_name neda.local localhost;
    
    # Redirect HTTP to HTTPS
    return 301 https://\$server_name\$request_uri;
}

server {
    listen 443 ssl http2;
    listen [::]:443 ssl http2;
    server_name neda.local localhost;
    
    # SSL Configuration
    ssl_certificate $NEDA_SSL_CERT_PATH;
    ssl_certificate_key $NEDA_SSL_KEY_PATH;
    ssl_protocols TLSv1.2 TLSv1.3;
    ssl_ciphers ECDHE-RSA-AES256-GCM-SHA512:DHE-RSA-AES256-GCM-SHA512;
    ssl_prefer_server_ciphers off;
    ssl_session_cache shared:SSL:10m;
    ssl_session_timeout 10m;
    
    # Security headers
    add_header X-Frame-Options DENY always;
    add_header X-Content-Type-Options nosniff always;
    add_header X-XSS-Protection "1; mode=block" always;
    add_header Strict-Transport-Security "max-age=63072000; includeSubDomains" always;
    
    # Proxy settings
    location / {
        proxy_pass http://localhost:$NEDA_PORT;
        proxy_http_version 1.1;
        proxy_set_header Upgrade \$http_upgrade;
        proxy_set_header Connection 'upgrade';
        proxy_set_header Host \$host;
        proxy_set_header X-Real-IP \$remote_addr;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
        proxy_cache_bypass \$http_upgrade;
        proxy_read_timeout 300s;
        proxy_connect_timeout 75s;
        
        # WebSocket support
        proxy_set_header Upgrade \$http_upgrade;
        proxy_set_header Connection "Upgrade";
    }
    
    # Static files
    location /static/ {
        alias $INSTALL_DIR/www/static/;
        expires 1y;
        add_header Cache-Control "public, immutable";
    }
    
    # API
    location /api/ {
        proxy_pass http://localhost:$NEDA_PORT;
        proxy_set_header Host \$host;
        proxy_set_header X-Real-IP \$remote_addr;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
        
        # Rate limiting
        limit_req zone=api burst=20 nodelay;
        limit_req_status 429;
    }
    
    # Health check endpoint
    location /health {
        proxy_pass http://localhost:$NEDA_PORT/health;
        access_log off;
    }
    
    # Logging
    access_log $LOG_DIR/nginx-access.log;
    error_log $LOG_DIR/nginx-error.log;
}

# Rate limiting zone
limit_req_zone \$binary_remote_addr zone=api:10m rate=10r/s;
EOF
    
    # Enable site
    ln -sf /etc/nginx/sites-available/neda /etc/nginx/sites-enabled/
    nginx -t && systemctl reload nginx
    
    log_success "Nginx configured"
}

# =============================================================================
# Firewall Configuration
# =============================================================================

configure_firewall() {
    log_info "Configuring firewall..."
    
    # Check for ufw (Ubuntu)
    if command -v ufw &> /dev/null; then
        ufw allow 22/tcp comment 'SSH'
        ufw allow 80/tcp comment 'HTTP'
        ufw allow 443/tcp comment 'HTTPS'
        ufw allow 8080/tcp comment 'NEDA HTTP'
        ufw allow 8443/tcp comment 'NEDA HTTPS'
        ufw --force enable
        log_success "UFW configured"
    
    # Check for firewalld (RHEL/CentOS)
    elif command -v firewall-cmd &> /dev/null; then
        firewall-cmd --permanent --add-service=ssh
        firewall-cmd --permanent --add-service=http
        firewall-cmd --permanent --add-service=https
        firewall-cmd --permanent --add-port=8080/tcp
        firewall-cmd --permanent --add-port=8443/tcp
        firewall-cmd --reload
        log_success "Firewalld configured"
    
    # Check for iptables
    elif command -v iptables &> /dev/null; then
        iptables -A INPUT -p tcp --dport 22 -j ACCEPT
        iptables -A INPUT -p tcp --dport 80 -j ACCEPT
        iptables -A INPUT -p tcp --dport 443 -j ACCEPT
        iptables -A INPUT -p tcp --dport 8080 -j ACCEPT
        iptables -A INPUT -p tcp --dport 8443 -j ACCEPT
        iptables -A INPUT -m state --state RELATED,ESTABLISHED -j ACCEPT
        iptables -A INPUT -i lo -j ACCEPT
        iptables -P INPUT DROP
        log_success "IPTables configured"
    else
        log_warning "No supported firewall found, skipping firewall configuration"
    fi
}

# =============================================================================
# Final Setup
# =============================================================================

create_configuration_files() {
    log_info "Creating configuration files..."
    
    # Main configuration
    cat > "$CONFIG_DIR/neda.config.json" << EOF
{
    "System": {
        "Name": "NEDA",
        "Version": "2.1.0",
        "Environment": "Production",
        "InstallationId": "$(uuidgen || echo \$(hostname)-\$(date +%s))"
    },
    "Network": {
        "Host": "0.0.0.0",
        "Port": 8080,
        "SslPort": 8443,
        "EnableSsl": true,
        "SslCertificate": "$NEDA_SSL_CERT_PATH",
        "SslKey": "$NEDA_SSL_KEY_PATH",
        "CorsOrigins": ["http://localhost:3000", "https://neda.local"],
        "RateLimiting": {
            "Enabled": true,
            "PermitLimit": 100,
            "Window": 60
        }
    },
    "Database": {
        "Provider": "Sqlite",
        "ConnectionString": "Data Source=$DATA_DIR/databases/neda.db",
        "AutoMigrate": true,
        "SeedData": true
    },
    "Security": {
        "Jwt": {
            "SecretPath": "$NEDA_JWT_SECRET_PATH",
            "ExpiryHours": 24,
            "Issuer": "NEDA System"
        },
        "Encryption": {
            "KeyPath": "$NEDA_ENCRYPTION_KEY_PATH",
            "Algorithm": "AES-256-GCM"
        },
        "Authentication": {
            "RequireEmailVerification": false,
            "LockoutAfterFailedAttempts": 5,
            "LockoutDurationMinutes": 15
        }
    },
    "Logging": {
        "Level": "Information",
        "File": {
            "Path": "$LOG_DIR/neda.log",
            "MaxSize": 104857600,
            "MaxFiles": 10
        },
        "Console": {
            "Enabled": true
        },
        "Seq": {
            "Enabled": false,
            "Url": "http://localhost:5341"
        }
    },
    "AI": {
        "EnableMachineLearning": true,
        "EnableNaturalLanguage": true,
        "EnableComputerVision": false,
        "ModelsPath": "$DATA_DIR/models",
        "TensorFlow": {
            "Enabled": true,
            "Threads": 2
        },
        "PyTorch": {
            "Enabled": true,
            "Device": "cpu"
        }
    },
    "Services": {
        "Redis": {
            "Enabled": true,
            "ConnectionString": "localhost:6379"
        },
        "BackgroundJobs": {
            "Enabled": true,
            "WorkerCount": 4
        },
        "Email": {
            "Enabled": false,
            "SmtpServer": "smtp.gmail.com",
            "SmtpPort": 587,
            "UseSsl": true
        }
    },
    "Monitoring": {
        "EnableHealthChecks": true,
        "EnableMetrics": true,
        "Prometheus": {
            "Enabled": true,
            "Endpoint": "/metrics"
        },
        "HealthCheckInterval": 30
    },
    "Plugins": {
        "Enabled": true,
        "Directory": "$INSTALL_DIR/plugins",
        "AutoLoad": true
    }
}
EOF
    
    # Create CLI configuration
    cat > "$CONFIG_DIR/cli.config.json" << EOF
{
    "DefaultProfile": "default",
    "Profiles": {
        "default": {
            "ApiUrl": "https://localhost:8443",
            "ApiKey": "",
            "Timeout": 30,
            "RetryCount": 3
        }
    },
    "Features": {
        "AutoComplete": true,
        "SyntaxHighlighting": true,
        "History": true,
        "LogCommands": true
    }
}
EOF
    
    # Create first-run script
    cat > "$INSTALL_DIR/bin/first-run.sh" << 'EOF'
#!/bin/bash
# NEDA First Run Script

echo "NEDA First Run Configuration"
echo "============================="

# Check if admin password needs to be changed
if sqlite3 /var/lib/neda/databases/neda.db "SELECT password_hash FROM users WHERE username='admin';" | grep -q "changeme"; then
    echo "Admin password is still default. Please change it now."
    read -sp "Enter new admin password: " password
    echo
    read -sp "Confirm password: " password2
    echo
    
    if [ "$password" == "$password2" ] && [ ${#password} -ge 8 ]; then
        # Hash password (in production, use proper hashing)
        hash=$(echo -n "$password" | sha256sum | awk '{print $1}')
        sqlite3 /var/lib/neda/databases/neda.db "UPDATE users SET password_hash='$hash' WHERE username='admin';"
        echo "Admin password updated successfully."
    else
        echo "Passwords do not match or are too short (min 8 characters)."
    fi
fi

# Generate API key
echo "Generating API key..."
api_key=$(openssl rand -hex 32)
sqlite3 /var/lib/neda/databases/neda.db "INSERT OR REPLACE INTO configurations (key, value) VALUES ('api.default_key', '$api_key');"
echo "API Key generated: $api_key"
echo "Save this key for API access."

# Initialize AI models
echo "Initializing AI models..."
if [ -d "/var/lib/neda/models" ]; then
    echo "Models found. Running initialization..."
    # Add model initialization commands here
fi

echo "First run configuration completed."
EOF
    
    chmod +x "$INSTALL_DIR/bin/first-run.sh"
    chown "$NEDA_USER:$NEDA_GROUP" "$CONFIG_DIR"/*.json
    
    log_success "Configuration files created"
}

set_permissions() {
    log_info "Setting final permissions..."
    
    # Set directory permissions
    find "$INSTALL_DIR" -type d -exec chmod 755 {} \;
    find "$CONFIG_DIR" -type d -exec chmod 755 {} \;
    find "$LOG_DIR" -type d -exec chmod 755 {} \;
    find "$DATA_DIR" -type d -exec chmod 755 {} \;
    find "$CACHE_DIR" -type d -exec chmod 755 {} \;
    
    # Set file permissions
    find "$INSTALL_DIR" -type f -exec chmod 644 {} \;
    find "$CONFIG_DIR" -type f -exec chmod 600 {} \;
    find "$LOG_DIR" -type f -exec chmod 640 {} \;
    
    # Executable files
    chmod 755 "$INSTALL_DIR/bin/"* 2>/dev/null || true
    chmod 755 "$BIN_DIR/neda" "$BIN_DIR/neda-cli"
    
    # Set ownership
    chown -R "$NEDA_USER:$NEDA_GROUP" \
        "$INSTALL_DIR" \
        "$CONFIG_DIR" \
        "$LOG_DIR" \
        "$DATA_DIR" \
        "$CACHE_DIR"
    
    # Special permissions for runtime directories
    chmod 1777 "$INSTALL_DIR/temp" 2>/dev/null || true
    chmod 1777 "$CACHE_DIR/temp" 2>/dev/null || true
    
    log_success "Permissions set"
}

verify_installation() {
    log_info "Verifying installation..."
    
    local errors=0
    
    # Check if binaries exist
    for binary in "$BIN_DIR/neda" "$BIN_DIR/neda-cli"; do
        if [[ ! -f "$binary" ]]; then
            log_error "Binary not found: $binary"
            ((errors++))
        fi
    done
    
    # Check if services are installed
    if [[ ! -f "/etc/systemd/system/neda.service" ]]; then
        log_error "Systemd service not installed"
        ((errors++))
    fi
    
    # Check if directories exist
    for dir in "$INSTALL_DIR" "$CONFIG_DIR" "$LOG_DIR" "$DATA_DIR"; do
        if [[ ! -d "$dir" ]]; then
            log_error "Directory not found: $dir"
            ((errors++))
        fi
    done
    
    # Test database connection
    if [[ -f "$DATA_DIR/databases/neda.db" ]]; then
        if ! sqlite3 "$DATA_DIR/databases/neda.db" "SELECT 1;" &> /dev/null; then
            log_error "Database connection test failed"
            ((errors++))
        fi
    fi
    
    # Test SSL certificates
    if [[ ! -f "$NEDA_SSL_CERT_PATH" ]] || [[ ! -f "$NEDA_SSL_KEY_PATH" ]]; then
        log_error "SSL certificates not found"
        ((errors++))
    fi
    
    if [[ $errors -eq 0 ]]; then
        log_success "Installation verification passed"
        return 0
    else
        log_error "Installation verification failed with $errors error(s)"
        return 1
    fi
}

start_services() {
    log_info "Starting NEDA services..."
    
    # Enable and start services
    systemctl enable neda.service neda-cli.service neda-maintenance.timer
    systemctl start neda.service
    
    # Wait for service to start
    local max_attempts=30
    local attempt=1
    
    while [[ $attempt -le $max_attempts ]]; do
        if systemctl is-active --quiet neda.service; then
            log_success "NEDA service started successfully"
            
            # Run first-time setup
            log_info "Running first-time setup..."
            sudo -u "$NEDA_USER" "$INSTALL_DIR/bin/first-run.sh"
            
            return 0
        fi
        
        log_debug "Waiting for service to start (attempt $attempt/$max_attempts)..."
        sleep 2
        ((attempt++))
    done
    
    log_error "Service failed to start within 60 seconds"
    journalctl -u neda.service --no-pager -n 50
    return 1
}

# =============================================================================
# Main Installation Function
# =============================================================================

main() {
    print_header
    
    # Pre-flight checks
    check_root
    check_bash_version
    
    log_info "Starting NEDA installation on $OS_TYPE $ARCH_TYPE ($DISTRO)"
    
    # Check and install dependencies
    if ! check_dependencies; then
        log_info "Installing missing dependencies..."
        install_missing_dependencies
    fi
    
    # Installation steps
    create_user_and_group
    create_directories
    setup_environment
    setup_security
    
    # Download and extract packages
    local temp_dir
    temp_dir=$(download_packages)
    if [[ $? -eq 0 ]] && [[ -n "$temp_dir" ]]; then
        extract_packages "$temp_dir"
    else
        log_error "Package download failed, using local files if available"
    fi
    
    # Install components
    install_dotnet_app
    install_python_packages
    setup_databases
    create_configuration_files
    
    # System integration
    create_systemd_service
    setup_nginx
    configure_firewall
    set_permissions
    
    # Final steps
    if verify_installation; then
        if start_services; then
            print_footer
            
            # Display installation summary
            cat << EOF

╔══════════════════════════════════════════════════════════════════╗
║                     Installation Summary                          ║
╠══════════════════════════════════════════════════════════════════╣
║                                                                  ║
║  NEDA has been successfully installed!                           ║
║                                                                  ║
║  Installation Directory: $INSTALL_DIR                            ║
║  Configuration Directory: $CONFIG_DIR                            ║
║  Data Directory: $DATA_DIR                                       ║
║  Log Directory: $LOG_DIR                                         ║
║                                                                  ║
║  Services:                                                       ║
║    • neda.service          - Main NEDA service                   ║
║    • neda-cli.service      - CLI service                         ║
║    • neda-maintenance.timer - Maintenance tasks                  ║
║                                                                  ║
║  Web Interface:                                                  ║
║    • HTTPS: https://localhost:8443                               ║
║    • HTTP:  http://localhost:8080                                ║
║                                                                  ║
║  Next Steps:                                                     ║
║    1. Access the web interface at https://localhost:8443         ║
║    2. Login with username: admin                                 ║
║    3. Change the default password immediately                    ║
║    4. Configure your environment in $CONFIG_DIR                  ║
║    5. Check service status: systemctl status neda.service        ║
║                                                                  ║
║  Useful Commands:                                                ║
║    • Start/Stop:   systemctl start/stop neda.service             ║
║    • Status:       systemctl status neda.service                 ║
║    • Logs:         journalctl -u neda.service -f                 ║
║    • CLI:          neda-cli --help                               ║
║                                                                  ║
╚══════════════════════════════════════════════════════════════════╝

To uninstall NEDA, run: $0 --uninstall

EOF
            
            log_success "Installation completed at $(date)"
            return 0
        fi
    fi
    
    log_error "Installation failed"
    return 1
}

# =============================================================================
# Uninstall Function
# =============================================================================

uninstall() {
    echo -e "${RED}"
    echo "╔══════════════════════════════════════════════════════════════════╗"
    echo "║                    NEDA Uninstallation                           ║"
    echo "╚══════════════════════════════════════════════════════════════════╝"
    echo -e "${NC}"
    echo ""
    
    read -p "Are you sure you want to uninstall NEDA? [y/N]: " confirm
    if [[ ! "$confirm" =~ ^[Yy]$ ]]; then
        echo "Uninstallation cancelled."
        exit 0
    fi
    
    log_info "Stopping services..."
    systemctl stop neda.service neda-cli.service neda-maintenance.timer
    systemctl disable neda.service neda-cli.service neda-maintenance.timer
    
    log_info "Removing services..."
    rm -f /etc/systemd/system/neda.service
    rm -f /etc/systemd/system/neda-cli.service
    rm -f /etc/systemd/system/neda-maintenance.timer
    rm -f /etc/systemd/system/neda-maintenance.service
    systemctl daemon-reload
    
    log_info "Removing Nginx configuration..."
    rm -f /etc/nginx/sites-enabled/neda
    rm -f /etc/nginx/sites-available/neda
    nginx -t && systemctl reload nginx
    
    log_info "Removing binaries..."
    rm -f "$BIN_DIR/neda"
    rm -f "$BIN_DIR/neda-cli"
    
    log_info "Removing data..."
    
    read -p "Remove all NEDA data and configurations? [y/N]: " remove_data
    if [[ "$remove_data" =~ ^[Yy]$ ]]; then
        rm -rf "$INSTALL_DIR"
        rm -rf "$CONFIG_DIR"
        rm -rf "$LOG_DIR"
        rm -rf "$DATA_DIR"
        rm -rf "$CACHE_DIR"
        log_info "All data removed."
    else
        log_info "Data preserved in:"
        log_info "  $INSTALL_DIR"
        log_info "  $CONFIG_DIR"
        log_info "  $LOG_DIR"
        log_info "  $DATA_DIR"
        log_info "  $CACHE_DIR"
    fi
    
    read -p "Remove NEDA user and group? [y/N]: " remove_user
    if [[ "$remove_user" =~ ^[Yy]$ ]]; then
        userdel "$NEDA_USER" 2>/dev/null || true
        groupdel "$NEDA_GROUP" 2>/dev/null || true
        log_info "User and group removed."
    fi
    
    log_success "NEDA has been uninstalled."
}

# =============================================================================
# Argument Parsing
# =============================================================================

parse_arguments() {
    while [[ $# -gt 0 ]]; do
        case $1 in
            -h|--help)
                cat << EOF
NEDA Installation Script

Usage: $0 [OPTIONS]

Options:
  -h, --help      Show this help message
  -v, --version   Show version information
  -u, --uninstall Uninstall NEDA
  -d, --debug     Enable debug output
  -q, --quiet     Quiet mode (minimal output)

Examples:
  $0               # Install NEDA
  $0 --uninstall   # Uninstall NEDA
  $0 --debug       # Install with debug output

EOF
                exit 0
                ;;
            -v|--version)
                echo "$SCRIPT_NAME version $SCRIPT_VERSION"
                exit 0
                ;;
            -u|--uninstall)
                uninstall
                exit 0
                ;;
            -d|--debug)
                DEBUG=true
                shift
                ;;
            -q|--quiet)
                exec >/dev/null 2>&1
                shift
                ;;
            *)
                log_error "Unknown option: $1"
                exit 1
                ;;
        esac
    done
}

# =============================================================================
# Main Execution
# =============================================================================

# Cleanup on exit
cleanup() {
    local exit_code=$?
    
    # Remove temporary files
    rm -rf "${TMPDIR:-/tmp}/neda-install-*" 2>/dev/null || true
    
    if [[ $exit_code -eq 0 ]]; then
        log_success "Script completed successfully"
    else
        log_error "Script failed with exit code $exit_code"
    fi
    
    exit $exit_code
}

# Set trap for cleanup
trap cleanup EXIT INT TERM

# Parse arguments
parse_arguments "$@"

# Run main installation
if main; then
    exit 0
else
    exit 1
fi