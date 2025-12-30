# =============================================================================
# NEDA Multi-Stage Dockerfile
# =============================================================================
# Industrial-grade Dockerfile for NEDA (Neural Engine for Digital Automation)
# Multi-architecture, multi-stage build with production optimization
# =============================================================================

# =============================================================================
# Stage 1: Base Image
# =============================================================================
FROM ubuntu:22.04 AS base

# Set environment variables
ENV DEBIAN_FRONTEND=noninteractive \
    LANG=C.UTF-8 \
    LC_ALL=C.UTF-8 \
    TZ=UTC \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:8080;https://+:8443 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    NODE_ENV=production \
    PYTHONUNBUFFERED=1 \
    PYTHONDONTWRITEBYTECODE=1 \
    PIP_NO_CACHE_DIR=1 \
    PATH="/usr/local/nvidia/bin:/usr/local/cuda/bin:${PATH}" \
    LD_LIBRARY_PATH="/usr/local/nvidia/lib:/usr/local/nvidia/lib64:${LD_LIBRARY_PATH}" \
    NVIDIA_VISIBLE_DEVICES=all \
    NVIDIA_DRIVER_CAPABILITIES=compute,utility \
    NVIDIA_REQUIRE_CUDA="cuda>=11.8"

# Install system dependencies
RUN apt-get update && apt-get install -y --no-install-recommends \
    # Core utilities
    ca-certificates \
    curl \
    wget \
    gnupg \
    lsb-release \
    software-properties-common \
    apt-transport-https \
    # Security
    openssl \
    gpg-agent \
    dirmngr \
    # Development tools
    build-essential \
    git \
    pkg-config \
    cmake \
    ninja-build \
    # System libraries
    libssl-dev \
    libffi-dev \
    libgomp1 \
    libatomic1 \
    libc6-dev \
    libgdiplus \
    libicu-dev \
    libsqlite3-dev \
    zlib1g-dev \
    libbz2-dev \
    libreadline-dev \
    libncursesw5-dev \
    libxml2-dev \
    libxmlsec1-dev \
    liblzma-dev \
    libgdbm-dev \
    libnss3-dev \
    # Multimedia
    libgl1-mesa-glx \
    libglib2.0-0 \
    libsm6 \
    libxext6 \
    libxrender-dev \
    libxft-dev \
    # Fonts
    fonts-dejavu-core \
    ttf-bitstream-vera \
    # Process management
    supervisor \
    cron \
    logrotate \
    # Networking
    net-tools \
    iproute2 \
    dnsutils \
    netcat \
    # Debugging
    strace \
    lsof \
    htop \
    vim \
    less \
    && rm -rf /var/lib/apt/lists/*

# =============================================================================
# Stage 2: .NET SDK Builder
# =============================================================================
FROM base AS dotnet-builder

# Install .NET 8 SDK
RUN wget -q https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb -O packages-microsoft-prod.deb \
    && dpkg -i packages-microsoft-prod.deb \
    && rm packages-microsoft-prod.deb \
    && apt-get update \
    && apt-get install -y dotnet-sdk-8.0

# Copy .NET projects
COPY NEDA.Core/ /src/NEDA.Core/
COPY NEDA.API/ /src/NEDA.API/
COPY NEDA.Services/ /src/NEDA.Services/
COPY NEDA.Common/ /src/NEDA.Common/

# Build .NET solution
WORKDIR /src/NEDA.Core
RUN dotnet restore NEDA.Core.sln \
    && dotnet build NEDA.Core.sln -c Release -o /app/build \
    && dotnet publish NEDA.Core.sln -c Release -o /app/publish --no-restore --no-build \
        -p:PublishReadyToRun=true \
        -p:PublishSingleFile=false \
        -p:PublishTrimmed=true \
        -p:TrimMode=partial \
        -p:EnableCompressionInSingleFile=true

# =============================================================================
# Stage 3: Python Builder
# =============================================================================
FROM base AS python-builder

# Install Python 3.10
RUN add-apt-repository ppa:deadsnakes/ppa \
    && apt-get update \
    && apt-get install -y python3.10 python3.10-dev python3.10-distutils \
    && curl -sS https://bootstrap.pypa.io/get-pip.py | python3.10

# Install CUDA if needed (conditional build)
ARG CUDA_VERSION=11.8
ARG USE_CUDA=false
RUN if [ "$USE_CUDA" = "true" ]; then \
        wget https://developer.download.nvidia.com/compute/cuda/repos/ubuntu2204/x86_64/cuda-keyring_1.0-1_all.deb \
        && dpkg -i cuda-keyring_1.0-1_all.deb \
        && apt-get update \
        && apt-get install -y cuda-toolkit-${CUDA_VERSION//./-} \
        && rm -rf /var/lib/apt/lists/*; \
    fi

# Copy Python requirements
COPY NEDA.AI/requirements.txt /src/requirements.txt
COPY NEDA.AI/requirements-ai.txt /src/requirements-ai.txt

# Install Python dependencies
WORKDIR /src
RUN pip3.10 install --upgrade pip setuptools wheel \
    && pip3.10 install -r requirements.txt \
    && if [ -f requirements-ai.txt ]; then pip3.10 install -r requirements-ai.txt; fi \
    && pip3.10 cache purge

# Copy Python source code
COPY NEDA.AI/ /src/NEDA.AI/

# =============================================================================
# Stage 4: Node.js Builder
# =============================================================================
FROM base AS node-builder

# Install Node.js 18
RUN curl -fsSL https://deb.nodesource.com/setup_18.x | bash - \
    && apt-get install -y nodejs

# Copy package files
COPY NEDA.UI/package.json /src/package.json
COPY NEDA.UI/package-lock.json /src/package-lock.json

# Install Node.js dependencies
WORKDIR /src
RUN npm ci --only=production \
    && npm cache clean --force

# Copy UI source code
COPY NEDA.UI/ /src/

# Build UI
RUN npm run build

# =============================================================================
# Stage 5: Runtime Image
# =============================================================================
FROM ubuntu:22.04 AS runtime

# Security hardening
ARG USER_ID=1000
ARG GROUP_ID=1000
ARG USER_NAME=neda
ARG APP_HOME=/app

# Set environment variables
ENV DEBIAN_FRONTEND=noninteractive \
    LANG=C.UTF-8 \
    LC_ALL=C.UTF-8 \
    TZ=UTC \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:8080;https://+:8443 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    NODE_ENV=production \
    PYTHONUNBUFFERED=1 \
    PATH="${APP_HOME}/.local/bin:${PATH}" \
    PYTHONPATH="${APP_HOME}/python:${PYTHONPATH}"

# Install minimal runtime dependencies
RUN apt-get update && apt-get install -y --no-install-recommends \
    # Core
    ca-certificates \
    curl \
    wget \
    # .NET Runtime
    libc6 \
    libgcc-s1 \
    libgssapi-krb5-2 \
    libicu70 \
    libssl3 \
    libstdc++6 \
    zlib1g \
    # Python Runtime
    python3.10 \
    python3.10-distutils \
    # System
    libgl1-mesa-glx \
    libglib2.0-0 \
    libsm6 \
    libxext6 \
    libxrender-dev \
    fonts-dejavu-core \
    ttf-bitstream-vera \
    # Security
    openssl \
    # Process monitoring
    procps \
    # Debugging
    lsof \
    less \
    && rm -rf /var/lib/apt/lists/* \
    && ln -sf python3.10 /usr/bin/python3 \
    && ln -sf python3.10 /usr/bin/python

# Create non-root user
RUN groupadd -g ${GROUP_ID} ${USER_NAME} \
    && useradd -l -u ${USER_ID} -g ${USER_NAME} -m -d ${APP_HOME} -s /bin/bash ${USER_NAME} \
    && chown -R ${USER_NAME}:${USER_NAME} ${APP_HOME}

# Switch to non-root user
USER ${USER_NAME}
WORKDIR ${APP_HOME}

# Copy .NET runtime from builder
COPY --from=dotnet-builder --chown=${USER_NAME}:${USER_NAME} /app/publish/ ${APP_HOME}/dotnet/

# Copy Python runtime from builder
COPY --from=python-builder --chown=${USER_NAME}:${USER_NAME} /usr/local/lib/python3.10/dist-packages/ ${APP_HOME}/.local/lib/python3.10/site-packages/
COPY --from=python-builder --chown=${USER_NAME}:${USER_NAME} /src/NEDA.AI/ ${APP_HOME}/python/

# Copy UI build from builder
COPY --from=node-builder --chown=${USER_NAME}:${USER_NAME} /src/build/ ${APP_HOME}/ui/

# Copy application configuration
COPY --chown=${USER_NAME}:${USER_NAME} NEDA.Core/Configuration/ ${APP_HOME}/config/
COPY --chown=${USER_NAME}:${USER_NAME} NEDA.Core/Assets/ ${APP_HOME}/assets/
COPY --chown=${USER_NAME}:${USER_NAME} scripts/ ${APP_HOME}/scripts/

# Create necessary directories
RUN mkdir -p \
    ${APP_HOME}/data \
    ${APP_HOME}/logs \
    ${APP_HOME}/cache \
    ${APP_HOME}/tmp \
    ${APP_HOME}/models \
    ${APP_HOME}/plugins \
    && chmod -R 755 ${APP_HOME}

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=60s --retries=3 \
    CMD curl -f http://localhost:8080/health || exit 1

# Expose ports
EXPOSE 8080  # HTTP
EXPOSE 8443  # HTTPS
EXPOSE 50051 # gRPC
EXPOSE 9090  # Metrics

# =============================================================================
# Stage 6: Production Image (Final)
# =============================================================================
FROM runtime AS production

# Labels (Open Container Initiative)
LABEL org.opencontainers.image.title="NEDA AI Engine" \
    org.opencontainers.image.description="Neural Engine for Digital Automation - Industrial AI Platform" \
    org.opencontainers.image.version="2.1.0" \
    org.opencontainers.image.created="2024-01-15T10:00:00Z" \
    org.opencontainers.image.vendor="NEDA Corporation" \
    org.opencontainers.image.authors="engineering@neda.io" \
    org.opencontainers.image.url="https://neda.io" \
    org.opencontainers.image.documentation="https://docs.neda.io" \
    org.opencontainers.image.source="https://github.com/neda-ai/neda-core" \
    org.opencontainers.image.licenses="Commercial" \
    org.opencontainers.image.ref.name="neda-ai-engine" \
    org.opencontainers.image.base.digest="" \
    org.opencontainers.image.base.name="ubuntu:22.04"

# Security labels
LABEL security.txt="https://neda.io/.well-known/security.txt" \
    security.scan="true" \
    security.hardening="enabled" \
    security.compliance="HIPAA,GDRP,SOC2"

# Multi-architecture support
ARG TARGETARCH
ARG TARGETOS
ARG TARGETVARIANT
LABEL org.opencontainers.image.architecture="${TARGETARCH}" \
    org.opencontainers.image.os="${TARGETOS}" \
    org.opencontainers.image.variant="${TARGETVARIANT}"

# Set working directory
WORKDIR ${APP_HOME}

# Copy entrypoint script
COPY --chown=${USER_NAME}:${USER_NAME} docker-entrypoint.sh /usr/local/bin/
RUN chmod +x /usr/local/bin/docker-entrypoint.sh

# Copy startup scripts
COPY --chown=${USER_NAME}:${USER_NAME} scripts/startup.sh ${APP_HOME}/scripts/
COPY --chown=${USER_NAME}:${USER_NAME} scripts/health-check.sh ${APP_HOME}/scripts/
RUN chmod +x ${APP_HOME}/scripts/*.sh

# Create volume mount points
VOLUME ["${APP_HOME}/data", "${APP_HOME}/logs", "${APP_HOME}/config", "${APP_HOME}/models"]

# Default command
ENTRYPOINT ["docker-entrypoint.sh"]

# Default arguments
CMD ["dotnet", "NEDA.Core.dll", "--config", "/app/config/appsettings.json"]

# =============================================================================
# Stage 7: Development Image
# =============================================================================
FROM runtime AS development

# Switch back to root for development tools
USER root

# Install development tools
RUN apt-get update && apt-get install -y --no-install-recommends \
    git \
    vim \
    nano \
    curl \
    wget \
    netcat \
    iputils-ping \
    dnsutils \
    telnet \
    jq \
    yq \
    && rm -rf /var/lib/apt/lists/*

# Install .NET SDK for development
RUN wget -q https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb -O packages-microsoft-prod.deb \
    && dpkg -i packages-microsoft-prod.deb \
    && rm packages-microsoft-prod.deb \
    && apt-get update \
    && apt-get install -y dotnet-sdk-8.0

# Install Python development tools
RUN apt-get update && apt-get install -y --no-install-recommends \
    python3.10-dev \
    python3.10-venv \
    python3-pip \
    && pip3 install --upgrade pip setuptools wheel \
    && pip3 install ipython pytest pylint black flake8 mypy \
    && rm -rf /var/lib/apt/lists/*

# Install Node.js for development
RUN curl -fsSL https://deb.nodesource.com/setup_18.x | bash - \
    && apt-get install -y nodejs \
    && npm install -g nodemon ts-node typescript @angular/cli

# Switch back to non-root user
USER ${USER_NAME}

# Copy development configuration
COPY --chown=${USER_NAME}:${USER_NAME} NEDA.Core/Configuration/development/ ${APP_HOME}/config/development/

# Set development environment
ENV ASPNETCORE_ENVIRONMENT=Development \
    NODE_ENV=development \
    PYTHONUNBUFFERED=1

# Development entrypoint
ENTRYPOINT ["docker-entrypoint.sh", "development"]

# =============================================================================
# Stage 8: GPU Image
# =============================================================================
FROM nvidia/cuda:11.8.0-runtime-ubuntu22.04 AS gpu

# Inherit from production image
COPY --from=production ${APP_HOME} ${APP_HOME}
COPY --from=production /usr/local/bin/docker-entrypoint.sh /usr/local/bin/

# Set CUDA environment
ENV NVIDIA_VISIBLE_DEVICES=all \
    NVIDIA_DRIVER_CAPABILITIES=compute,utility \
    LD_LIBRARY_PATH=/usr/local/cuda/lib64:$LD_LIBRARY_PATH \
    TF_FORCE_GPU_ALLOW_GROWTH=true \
    CUDA_VISIBLE_DEVICES=0

# Install CUDA dependencies
RUN apt-get update && apt-get install -y --no-install-recommends \
    cuda-libraries-11-8 \
    libcublas-11-8 \
    libcufft-11-8 \
    libcurand-11-8 \
    libcusolver-11-8 \
    libcusparse-11-8 \
    libnpp-11-8 \
    cuda-nvrtc-11-8 \
    && rm -rf /var/lib/apt/lists/*

# Copy GPU-specific Python packages
COPY --from=python-builder --chown=${USER_NAME}:${USER_NAME} /usr/local/lib/python3.10/dist-packages/ ${APP_HOME}/.local/lib/python3.10/site-packages/

# Set GPU-specific environment
ENV NEDA_GPU_ENABLED=true \
    OMP_NUM_THREADS=4

# Switch to non-root user
USER ${USER_NAME}
WORKDIR ${APP_HOME}

# GPU entrypoint
ENTRYPOINT ["docker-entrypoint.sh", "gpu"]

# =============================================================================
# Build-time arguments
# =============================================================================
# Build with: docker build --build-arg BUILD_DATE=$(date -u +'%Y-%m-%dT%H:%M:%SZ') \
#                        --build-arg VERSION=2.1.0 \
#                        --build-arg REVISION=$(git rev-parse HEAD) \
#                        -t neda/ai-engine:latest .

ARG BUILD_DATE
ARG VERSION
ARG REVISION
ARG BUILD_NUMBER

LABEL org.opencontainers.image.created="${BUILD_DATE}" \
    org.opencontainers.image.version="${VERSION}" \
    org.opencontainers.image.revision="${REVISION}" \
    org.opencontainers.image.build-number="${BUILD_NUMBER}"

# =============================================================================
# docker-entrypoint.sh (companion script)
# =============================================================================
# Create this script in the same directory:
#
# #!/bin/bash
# set -e
#
# # Initialize environment
# export NEDA_HOME="/app"
# export PATH="$NEDA_HOME/dotnet:$PATH"
#
# # Check for GPU support
# if [ "$1" = "gpu" ] || [ "$NEDA_GPU_ENABLED" = "true" ]; then
#     echo "GPU mode enabled"
#     export CUDA_VISIBLE_DEVICES=${CUDA_VISIBLE_DEVICES:-0}
# fi
#
# # Check for development mode
# if [ "$1" = "development" ]; then
#     echo "Development mode enabled"
#     export ASPNETCORE_ENVIRONMENT=Development
#     export NODE_ENV=development
# fi
#
# # Run startup scripts
# if [ -f "$NEDA_HOME/scripts/startup.sh" ]; then
#     bash "$NEDA_HOME/scripts/startup.sh"
# fi
#
# # Execute command
# exec "$@"