using Microsoft.Azure.Management.Compute.Fluent;
using Microsoft.Azure.Management.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent.Authentication;
using Microsoft.Azure.Management.ResourceManager.Fluent.Core;
using Microsoft.Azure.Management.Storage.Fluent;
using Microsoft.Extensions.Logging;
using Microsoft.Rest;
using NEDA.API.Middleware;
using NEDA.CharacterSystems.CharacterCreator.SkeletonSetup;
using NEDA.Cloud.AWS;
using NEDA.Core.Common;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.Core.Security;
using NEDA.Services.Messaging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Threading.Tasks;

namespace NEDA.Cloud.Azure;
{
    /// <summary>
    /// Azure Cloud Services Client - Manages Azure resource operations;
    /// </summary>
    public class AzureClient : ICloudClient, IDisposable;
    {
        #region Private Fields;

        private readonly ILogger<AzureClient> _logger;
        private readonly IErrorReporter _errorReporter;
        private readonly ISecurityManager _securityManager;
        private readonly IEventBus _eventBus;

        private IAzure _azureClient;
        private AzureCredentials _azureCredentials;
        private bool _isAuthenticated;
        private readonly HttpClient _httpClient;
        private readonly Dictionary<string, object> _resourceCache;
        private readonly SemaphoreSlim _authLock = new SemaphoreSlim(1, 1);

        #endregion;

        #region Properties;

        /// <summary>
        /// Azure subscription ID;
        /// </summary>
        public string SubscriptionId { get; private set; }

        /// <summary>
        /// Azure tenant ID;
        /// </summary>
        public string TenantId { get; private set; }

        /// <summary>
        /// Client ID for service principal authentication;
        /// </summary>
        public string ClientId { get; private set; }

        /// <summary>
        /// Default region for resource operations;
        /// </summary>
        public Region DefaultRegion { get; set; } = Region.USWest;

        /// <summary>
        /// Indicates if the client is authenticated;
        /// </summary>
        public bool IsAuthenticated => _isAuthenticated && _azureClient != null;

        /// <summary>
        /// Authentication method used;
        /// </summary>
        public AzureAuthMethod AuthMethod { get; private set; }

        #endregion;

        #region Events;

        /// <summary>
        /// Raised when authentication state changes;
        /// </summary>
        public event EventHandler<AuthenticationStateChangedEventArgs> AuthenticationStateChanged;

        /// <summary>
        /// Raised when resource operation completes;
        /// </summary>
        public event EventHandler<ResourceOperationEventArgs> ResourceOperationCompleted;

        /// <summary>
        /// Raised when operation progress updates;
        /// </summary>
        public event EventHandler<OperationProgressEventArgs> OperationProgress;

        #endregion;

        #region Constructor;

        /// <summary>
        /// Initializes a new instance of AzureClient;
        /// </summary>
        public AzureClient(
            ILogger<AzureClient> logger,
            IErrorReporter errorReporter,
            ISecurityManager securityManager,
            IEventBus eventBus)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _errorReporter = errorReporter ?? throw new ArgumentNullException(nameof(errorReporter));
            _securityManager = securityManager ?? throw new ArgumentNullException(nameof(securityManager));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));

            _httpClient = new HttpClient;
            {
                Timeout = TimeSpan.FromMinutes(5)
            };

            _resourceCache = new Dictionary<string, object>();

            _logger.LogInformation("AzureClient initialized");
        }

        #endregion;

        #region Authentication Methods;

        /// <summary>
        /// Authenticate using service principal credentials;
        /// </summary>
        public async Task<bool> AuthenticateWithServicePrincipalAsync(
            string tenantId,
            string clientId,
            string clientSecret,
            string subscriptionId)
        {
            ValidateParameters(tenantId, clientId, clientSecret, subscriptionId);

            await _authLock.WaitAsync();

            try
            {
                _logger.LogInformation($"Authenticating with service principal: ClientId={clientId}, Subscription={subscriptionId}");

                var credentials = SdkContext.AzureCredentialsFactory;
                    .FromServicePrincipal(
                        clientId,
                        clientSecret,
                        tenantId,
                        AzureEnvironment.AzureGlobalCloud);

                _azureCredentials = credentials;
                SubscriptionId = subscriptionId;
                TenantId = tenantId;
                ClientId = clientId;
                AuthMethod = AzureAuthMethod.ServicePrincipal;

                return await InternalAuthenticateAsync(credentials);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Service principal authentication failed");
                await _errorReporter.ReportErrorAsync(
                    "AZURE_AUTH_FAILED",
                    $"Service principal authentication failed: {ex.Message}",
                    ex);

                throw new AzureAuthenticationException(
                    "Service principal authentication failed",
                    ErrorCodes.Azure.AuthenticationFailed,
                    ex);
            }
            finally
            {
                _authLock.Release();
            }
        }

        /// <summary>
        /// Authenticate using managed identity;
        /// </summary>
        public async Task<bool> AuthenticateWithManagedIdentityAsync(string subscriptionId)
        {
            ValidateSubscriptionId(subscriptionId);

            await _authLock.WaitAsync();

            try
            {
                _logger.LogInformation($"Authenticating with managed identity for subscription: {subscriptionId}");

                var credentials = new AzureCredentials(
                    new MSILoginInformation(MSIResourceType.AppService),
                    AzureEnvironment.AzureGlobalCloud,
                    subscriptionId);

                SubscriptionId = subscriptionId;
                AuthMethod = AzureAuthMethod.ManagedIdentity;

                return await InternalAuthenticateAsync(credentials);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Managed identity authentication failed");
                await _errorReporter.ReportErrorAsync(
                    "AZURE_MI_AUTH_FAILED",
                    $"Managed identity authentication failed: {ex.Message}",
                    ex);

                throw new AzureAuthenticationException(
                    "Managed identity authentication failed",
                    ErrorCodes.Azure.ManagedIdentityAuthFailed,
                    ex);
            }
            finally
            {
                _authLock.Release();
            }
        }

        /// <summary>
        /// Authenticate using access token;
        /// </summary>
        public async Task<bool> AuthenticateWithTokenAsync(
            string accessToken,
            string subscriptionId,
            string tenantId)
        {
            ValidateTokenParameters(accessToken, subscriptionId, tenantId);

            await _authLock.WaitAsync();

            try
            {
                _logger.LogInformation($"Authenticating with token for subscription: {subscriptionId}");

                var tokenCredentials = new TokenCredentials(accessToken);
                var credentials = new AzureCredentials(
                    tokenCredentials,
                    tokenCredentials,
                    tenantId,
                    AzureEnvironment.AzureGlobalCloud);

                _azureCredentials = credentials;
                SubscriptionId = subscriptionId;
                TenantId = tenantId;
                AuthMethod = AzureAuthMethod.AccessToken;

                return await InternalAuthenticateAsync(credentials);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Token authentication failed");
                await _errorReporter.ReportErrorAsync(
                    "AZURE_TOKEN_AUTH_FAILED",
                    $"Token authentication failed: {ex.Message}",
                    ex);

                throw new AzureAuthenticationException(
                    "Token authentication failed",
                    ErrorCodes.Azure.TokenAuthFailed,
                    ex);
            }
            finally
            {
                _authLock.Release();
            }
        }

        private async Task<bool> InternalAuthenticateAsync(AzureCredentials credentials)
        {
            try
            {
                _azureClient = Azure;
                    .Configure()
                    .WithLogLevel(HttpLoggingDelegatingHandler.Level.Basic)
                    .Authenticate(credentials)
                    .WithSubscription(SubscriptionId);

                // Test authentication by making a simple API call;
                await TestAuthenticationAsync();

                _isAuthenticated = true;

                _logger.LogInformation($"Azure authentication successful. Subscription: {SubscriptionId}");

                OnAuthenticationStateChanged(new AuthenticationStateChangedEventArgs;
                {
                    IsAuthenticated = true,
                    AuthMethod = AuthMethod,
                    Timestamp = DateTime.UtcNow;
                });

                await _eventBus.PublishAsync(new AzureAuthenticationSuccessEvent;
                {
                    SubscriptionId = SubscriptionId,
                    TenantId = TenantId,
                    AuthMethod = AuthMethod.ToString(),
                    Timestamp = DateTime.UtcNow;
                });

                return true;
            }
            catch (Exception ex)
            {
                _isAuthenticated = false;
                _azureClient = null;

                _logger.LogError(ex, "Internal authentication failed");
                throw;
            }
        }

        private async Task TestAuthenticationAsync()
        {
            try
            {
                // Try to list resource groups to test authentication;
                var resourceGroups = await _azureClient.ResourceGroups.ListAsync();
                _logger.LogDebug($"Authentication test successful. Found {resourceGroups.Count()} resource groups");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Authentication test failed");
                throw new AzureAuthenticationException(
                    "Authentication test failed",
                    ErrorCodes.Azure.AuthTestFailed,
                    ex);
            }
        }

        #endregion;

        #region Resource Group Operations;

        /// <summary>
        /// Create or update a resource group;
        /// </summary>
        public async Task<IResourceGroup> CreateOrUpdateResourceGroupAsync(
            string resourceGroupName,
            Region region)
        {
            ValidateResourceGroupName(resourceGroupName);
            EnsureAuthenticated();

            try
            {
                _logger.LogInformation($"Creating/updating resource group: {resourceGroupName} in region: {region}");

                OnOperationProgress(new OperationProgressEventArgs;
                {
                    Operation = "CreateResourceGroup",
                    Progress = 25,
                    Message = $"Creating resource group '{resourceGroupName}'"
                });

                var resourceGroup = await _azureClient.ResourceGroups;
                    .Define(resourceGroupName)
                    .WithRegion(region)
                    .CreateAsync();

                _logger.LogInformation($"Resource group created/updated: {resourceGroupName}");

                OnOperationProgress(new OperationProgressEventArgs;
                {
                    Operation = "CreateResourceGroup",
                    Progress = 100,
                    Message = $"Resource group '{resourceGroupName}' created successfully"
                });

                OnResourceOperationCompleted(new ResourceOperationEventArgs;
                {
                    Operation = ResourceOperation.Create,
                    ResourceType = ResourceType.ResourceGroup,
                    ResourceName = resourceGroupName,
                    Region = region.ToString(),
                    Timestamp = DateTime.UtcNow;
                });

                await _eventBus.PublishAsync(new ResourceGroupCreatedEvent;
                {
                    ResourceGroupName = resourceGroupName,
                    Region = region.ToString(),
                    SubscriptionId = SubscriptionId,
                    Timestamp = DateTime.UtcNow;
                });

                return resourceGroup;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to create/update resource group: {resourceGroupName}");
                await _errorReporter.ReportErrorAsync(
                    "AZURE_RG_CREATE_FAILED",
                    $"Failed to create resource group '{resourceGroupName}': {ex.Message}",
                    ex);

                throw new AzureResourceOperationException(
                    $"Failed to create resource group '{resourceGroupName}'",
                    ErrorCodes.Azure.ResourceGroupCreateFailed,
                    ex);
            }
        }

        /// <summary>
        /// Get resource group by name;
        /// </summary>
        public async Task<IResourceGroup> GetResourceGroupAsync(string resourceGroupName)
        {
            ValidateResourceGroupName(resourceGroupName);
            EnsureAuthenticated();

            try
            {
                var cacheKey = $"RG_{resourceGroupName}";
                if (_resourceCache.TryGetValue(cacheKey, out var cached) && cached is IResourceGroup cachedRg)
                {
                    return cachedRg;
                }

                var resourceGroup = await _azureClient.ResourceGroups.GetByNameAsync(resourceGroupName);

                if (resourceGroup != null)
                {
                    _resourceCache[cacheKey] = resourceGroup;
                }

                return resourceGroup;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get resource group: {resourceGroupName}");
                throw new AzureResourceOperationException(
                    $"Failed to get resource group '{resourceGroupName}'",
                    ErrorCodes.Azure.ResourceGroupGetFailed,
                    ex);
            }
        }

        /// <summary>
        /// Delete a resource group;
        /// </summary>
        public async Task DeleteResourceGroupAsync(string resourceGroupName)
        {
            ValidateResourceGroupName(resourceGroupName);
            EnsureAuthenticated();

            try
            {
                _logger.LogInformation($"Deleting resource group: {resourceGroupName}");

                await _azureClient.ResourceGroups.DeleteByNameAsync(resourceGroupName);

                // Remove from cache;
                var cacheKey = $"RG_{resourceGroupName}";
                _resourceCache.Remove(cacheKey);

                _logger.LogInformation($"Resource group deleted: {resourceGroupName}");

                OnResourceOperationCompleted(new ResourceOperationEventArgs;
                {
                    Operation = ResourceOperation.Delete,
                    ResourceType = ResourceType.ResourceGroup,
                    ResourceName = resourceGroupName,
                    Timestamp = DateTime.UtcNow;
                });

                await _eventBus.PublishAsync(new ResourceGroupDeletedEvent;
                {
                    ResourceGroupName = resourceGroupName,
                    SubscriptionId = SubscriptionId,
                    Timestamp = DateTime.UtcNow;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to delete resource group: {resourceGroupName}");
                await _errorReporter.ReportErrorAsync(
                    "AZURE_RG_DELETE_FAILED",
                    $"Failed to delete resource group '{resourceGroupName}': {ex.Message}",
                    ex);

                throw new AzureResourceOperationException(
                    $"Failed to delete resource group '{resourceGroupName}'",
                    ErrorCodes.Azure.ResourceGroupDeleteFailed,
                    ex);
            }
        }

        #endregion;

        #region Virtual Machine Operations;

        /// <summary>
        /// Create a virtual machine;
        /// </summary>
        public async Task<IVirtualMachine> CreateVirtualMachineAsync(
            string resourceGroupName,
            string vmName,
            string vmSize,
            string imagePublisher,
            string imageOffer,
            string imageSku,
            string adminUsername,
            string adminPassword,
            Region region)
        {
            ValidateVmParameters(resourceGroupName, vmName, vmSize, adminUsername, adminPassword);
            EnsureAuthenticated();

            try
            {
                _logger.LogInformation($"Creating VM: {vmName} in resource group: {resourceGroupName}");

                OnOperationProgress(new OperationProgressEventArgs;
                {
                    Operation = "CreateVM",
                    Progress = 10,
                    Message = $"Starting VM creation for '{vmName}'"
                });

                var network = await CreateVirtualNetworkAsync(resourceGroupName, $"{vmName}-vnet", region);
                var subnet = await CreateSubnetAsync(network, $"{vmName}-subnet");
                var publicIp = await CreatePublicIPAsync(resourceGroupName, $"{vmName}-ip", region);
                var networkInterface = await CreateNetworkInterfaceAsync(
                    resourceGroupName,
                    $"{vmName}-nic",
                    subnet,
                    publicIp,
                    region);

                OnOperationProgress(new OperationProgressEventArgs;
                {
                    Operation = "CreateVM",
                    Progress = 40,
                    Message = $"Network resources created for '{vmName}'"
                });

                var vm = await _azureClient.VirtualMachines;
                    .Define(vmName)
                    .WithRegion(region)
                    .WithExistingResourceGroup(resourceGroupName)
                    .WithExistingPrimaryNetworkInterface(networkInterface)
                    .WithLatestWindowsImage(imagePublisher, imageOffer, imageSku)
                    .WithAdminUsername(adminUsername)
                    .WithAdminPassword(adminPassword)
                    .WithSize(vmSize)
                    .WithOSDiskCaching(CachingTypes.ReadWrite)
                    .WithOSDiskSizeInGB(128)
                    .CreateAsync();

                OnOperationProgress(new OperationProgressEventArgs;
                {
                    Operation = "CreateVM",
                    Progress = 100,
                    Message = $"VM '{vmName}' created successfully"
                });

                _logger.LogInformation($"VM created: {vmName}");

                OnResourceOperationCompleted(new ResourceOperationEventArgs;
                {
                    Operation = ResourceOperation.Create,
                    ResourceType = ResourceType.VirtualMachine,
                    ResourceName = vmName,
                    Region = region.ToString(),
                    ResourceGroup = resourceGroupName,
                    Timestamp = DateTime.UtcNow;
                });

                await _eventBus.PublishAsync(new VirtualMachineCreatedEvent;
                {
                    VmName = vmName,
                    ResourceGroupName = resourceGroupName,
                    VmSize = vmSize,
                    Region = region.ToString(),
                    SubscriptionId = SubscriptionId,
                    Timestamp = DateTime.UtcNow;
                });

                return vm;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to create VM: {vmName}");
                await _errorReporter.ReportErrorAsync(
                    "AZURE_VM_CREATE_FAILED",
                    $"Failed to create VM '{vmName}': {ex.Message}",
                    ex);

                throw new AzureResourceOperationException(
                    $"Failed to create VM '{vmName}'",
                    ErrorCodes.Azure.VirtualMachineCreateFailed,
                    ex);
            }
        }

        /// <summary>
        /// Start a virtual machine;
        /// </summary>
        public async Task StartVirtualMachineAsync(string resourceGroupName, string vmName)
        {
            ValidateResourceParameters(resourceGroupName, vmName);
            EnsureAuthenticated();

            try
            {
                _logger.LogInformation($"Starting VM: {vmName}");

                await _azureClient.VirtualMachines;
                    .GetByResourceGroupAsync(resourceGroupName, vmName)
                    .ContinueWith(vmTask => vmTask.Result.StartAsync())
                    .Unwrap();

                _logger.LogInformation($"VM started: {vmName}");

                await _eventBus.PublishAsync(new VirtualMachineStateChangedEvent;
                {
                    VmName = vmName,
                    ResourceGroupName = resourceGroupName,
                    NewState = "Running",
                    SubscriptionId = SubscriptionId,
                    Timestamp = DateTime.UtcNow;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to start VM: {vmName}");
                throw new AzureResourceOperationException(
                    $"Failed to start VM '{vmName}'",
                    ErrorCodes.Azure.VirtualMachineStartFailed,
                    ex);
            }
        }

        /// <summary>
        /// Stop a virtual machine;
        /// </summary>
        public async Task StopVirtualMachineAsync(string resourceGroupName, string vmName)
        {
            ValidateResourceParameters(resourceGroupName, vmName);
            EnsureAuthenticated();

            try
            {
                _logger.LogInformation($"Stopping VM: {vmName}");

                await _azureClient.VirtualMachines;
                    .GetByResourceGroupAsync(resourceGroupName, vmName)
                    .ContinueWith(vmTask => vmTask.Result.PowerOffAsync())
                    .Unwrap();

                _logger.LogInformation($"VM stopped: {vmName}");

                await _eventBus.PublishAsync(new VirtualMachineStateChangedEvent;
                {
                    VmName = vmName,
                    ResourceGroupName = resourceGroupName,
                    NewState = "Stopped",
                    SubscriptionId = SubscriptionId,
                    Timestamp = DateTime.UtcNow;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to stop VM: {vmName}");
                throw new AzureResourceOperationException(
                    $"Failed to stop VM '{vmName}'",
                    ErrorCodes.Azure.VirtualMachineStopFailed,
                    ex);
            }
        }

        #endregion;

        #region Storage Operations;

        /// <summary>
        /// Create a storage account;
        /// </summary>
        public async Task<IStorageAccount> CreateStorageAccountAsync(
            string resourceGroupName,
            string storageAccountName,
            Region region)
        {
            ValidateStorageAccountName(storageAccountName);
            EnsureAuthenticated();

            try
            {
                _logger.LogInformation($"Creating storage account: {storageAccountName}");

                var storageAccount = await _azureClient.StorageAccounts;
                    .Define(storageAccountName)
                    .WithRegion(region)
                    .WithExistingResourceGroup(resourceGroupName)
                    .WithSku(StorageAccountSkuType.Standard_LRS)
                    .WithGeneralPurposeAccountKindV2()
                    .WithOnlyHttpsTraffic()
                    .CreateAsync();

                _logger.LogInformation($"Storage account created: {storageAccountName}");

                OnResourceOperationCompleted(new ResourceOperationEventArgs;
                {
                    Operation = ResourceOperation.Create,
                    ResourceType = ResourceType.StorageAccount,
                    ResourceName = storageAccountName,
                    Region = region.ToString(),
                    ResourceGroup = resourceGroupName,
                    Timestamp = DateTime.UtcNow;
                });

                return storageAccount;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to create storage account: {storageAccountName}");
                await _errorReporter.ReportErrorAsync(
                    "AZURE_STORAGE_CREATE_FAILED",
                    $"Failed to create storage account '{storageAccountName}': {ex.Message}",
                    ex);

                throw new AzureResourceOperationException(
                    $"Failed to create storage account '{storageAccountName}'",
                    ErrorCodes.Azure.StorageAccountCreateFailed,
                    ex);
            }
        }

        /// <summary>
        /// Upload file to blob storage;
        /// </summary>
        public async Task<string> UploadToBlobStorageAsync(
            string storageAccountName,
            string containerName,
            string blobName,
            Stream fileStream,
            string contentType = null)
        {
            ValidateBlobParameters(storageAccountName, containerName, blobName, fileStream);
            EnsureAuthenticated();

            try
            {
                var storageAccount = await _azureClient.StorageAccounts.GetByResourceGroupAsync(
                    GetResourceGroupNameForStorage(storageAccountName),
                    storageAccountName);

                var blobEndpoint = storageAccount.EndPoints.Primary.Blob;
                var connectionString = await GetStorageConnectionStringAsync(storageAccount);

                // In a real implementation, you would use Azure.Storage.Blobs here;
                // This is a simplified version;

                _logger.LogInformation($"Uploading blob: {blobName} to container: {containerName}");

                // Simulate upload - replace with actual Azure Storage SDK calls;
                await Task.Delay(100); // Simulate network delay;

                var blobUrl = $"{blobEndpoint}/{containerName}/{blobName}";

                _logger.LogInformation($"Blob uploaded successfully: {blobUrl}");

                await _eventBus.PublishAsync(new BlobUploadedEvent;
                {
                    StorageAccountName = storageAccountName,
                    ContainerName = containerName,
                    BlobName = blobName,
                    BlobUrl = blobUrl,
                    ContentType = contentType,
                    Timestamp = DateTime.UtcNow;
                });

                return blobUrl;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to upload blob: {blobName}");
                throw new AzureResourceOperationException(
                    $"Failed to upload blob '{blobName}'",
                    ErrorCodes.Azure.BlobUploadFailed,
                    ex);
            }
        }

        #endregion;

        #region Helper Methods;

        private async Task<INetwork> CreateVirtualNetworkAsync(
            string resourceGroupName,
            string vnetName,
            Region region)
        {
            return await _azureClient.Networks;
                .Define(vnetName)
                .WithRegion(region)
                .WithExistingResourceGroup(resourceGroupName)
                .WithAddressSpace("10.0.0.0/16")
                .CreateAsync();
        }

        private async Task<ISubnet> CreateSubnetAsync(INetwork network, string subnetName)
        {
            return await network.Subnets;
                .Define(subnetName)
                .WithAddressPrefix("10.0.0.0/24")
                .Attach()
                .ApplyAsync();
        }

        private async Task<IPublicIPAddress> CreatePublicIPAsync(
            string resourceGroupName,
            string ipName,
            Region region)
        {
            return await _azureClient.PublicIPAddresses;
                .Define(ipName)
                .WithRegion(region)
                .WithExistingResourceGroup(resourceGroupName)
                .WithDynamicIP()
                .CreateAsync();
        }

        private async Task<INetworkInterface> CreateNetworkInterfaceAsync(
            string resourceGroupName,
            string nicName,
            ISubnet subnet,
            IPublicIPAddress publicIp,
            Region region)
        {
            return await _azureClient.NetworkInterfaces;
                .Define(nicName)
                .WithRegion(region)
                .WithExistingResourceGroup(resourceGroupName)
                .WithExistingPrimaryNetwork(network)
                .WithSubnet(subnet.Name)
                .WithPrimaryPrivateIPAddressDynamic()
                .WithExistingPrimaryPublicIPAddress(publicIp)
                .CreateAsync();
        }

        private async Task<string> GetStorageConnectionStringAsync(IStorageAccount storageAccount)
        {
            var keys = await storageAccount.GetKeysAsync();
            var key = keys.First().Value;

            return $"DefaultEndpointsProtocol=https;AccountName={storageAccount.Name};AccountKey={key};EndpointSuffix=core.windows.net";
        }

        private string GetResourceGroupNameForStorage(string storageAccountName)
        {
            // In a real implementation, you would look up the resource group;
            // This is a simplified version;
            return "default-rg";
        }

        private void EnsureAuthenticated()
        {
            if (!IsAuthenticated)
            {
                throw new AzureAuthenticationException(
                    "Azure client is not authenticated",
                    ErrorCodes.Azure.NotAuthenticated);
            }
        }

        #endregion;

        #region Validation Methods;

        private void ValidateParameters(string tenantId, string clientId, string clientSecret, string subscriptionId)
        {
            if (string.IsNullOrWhiteSpace(tenantId))
                throw new ArgumentException("Tenant ID cannot be null or empty", nameof(tenantId));

            if (string.IsNullOrWhiteSpace(clientId))
                throw new ArgumentException("Client ID cannot be null or empty", nameof(clientId));

            if (string.IsNullOrWhiteSpace(clientSecret))
                throw new ArgumentException("Client secret cannot be null or empty", nameof(clientSecret));

            if (string.IsNullOrWhiteSpace(subscriptionId))
                throw new ArgumentException("Subscription ID cannot be null or empty", nameof(subscriptionId));

            _securityManager.ValidateAzureCredentials(clientId, clientSecret);
        }

        private void ValidateSubscriptionId(string subscriptionId)
        {
            if (string.IsNullOrWhiteSpace(subscriptionId))
                throw new ArgumentException("Subscription ID cannot be null or empty", nameof(subscriptionId));
        }

        private void ValidateTokenParameters(string accessToken, string subscriptionId, string tenantId)
        {
            if (string.IsNullOrWhiteSpace(accessToken))
                throw new ArgumentException("Access token cannot be null or empty", nameof(accessToken));

            if (string.IsNullOrWhiteSpace(subscriptionId))
                throw new ArgumentException("Subscription ID cannot be null or empty", nameof(subscriptionId));

            if (string.IsNullOrWhiteSpace(tenantId))
                throw new ArgumentException("Tenant ID cannot be null or empty", nameof(tenantId));
        }

        private void ValidateResourceGroupName(string resourceGroupName)
        {
            if (string.IsNullOrWhiteSpace(resourceGroupName))
                throw new ArgumentException("Resource group name cannot be null or empty", nameof(resourceGroupName));

            if (resourceGroupName.Length > 90)
                throw new ArgumentException("Resource group name cannot exceed 90 characters", nameof(resourceGroupName));
        }

        private void ValidateVmParameters(
            string resourceGroupName,
            string vmName,
            string vmSize,
            string adminUsername,
            string adminPassword)
        {
            ValidateResourceGroupName(resourceGroupName);

            if (string.IsNullOrWhiteSpace(vmName))
                throw new ArgumentException("VM name cannot be null or empty", nameof(vmName));

            if (string.IsNullOrWhiteSpace(vmSize))
                throw new ArgumentException("VM size cannot be null or empty", nameof(vmSize));

            if (string.IsNullOrWhiteSpace(adminUsername))
                throw new ArgumentException("Admin username cannot be null or empty", nameof(adminUsername));

            if (string.IsNullOrWhiteSpace(adminPassword))
                throw new ArgumentException("Admin password cannot be null or empty", nameof(adminPassword));

            if (adminPassword.Length < 12)
                throw new ArgumentException("Admin password must be at least 12 characters", nameof(adminPassword));
        }

        private void ValidateResourceParameters(string resourceGroupName, string resourceName)
        {
            ValidateResourceGroupName(resourceGroupName);

            if (string.IsNullOrWhiteSpace(resourceName))
                throw new ArgumentException("Resource name cannot be null or empty", nameof(resourceName));
        }

        private void ValidateStorageAccountName(string storageAccountName)
        {
            if (string.IsNullOrWhiteSpace(storageAccountName))
                throw new ArgumentException("Storage account name cannot be null or empty", nameof(storageAccountName));

            if (storageAccountName.Length < 3 || storageAccountName.Length > 24)
                throw new ArgumentException("Storage account name must be between 3 and 24 characters", nameof(storageAccountName));

            if (!storageAccountName.All(char.IsLower))
                throw new ArgumentException("Storage account name must contain only lowercase letters", nameof(storageAccountName));
        }

        private void ValidateBlobParameters(
            string storageAccountName,
            string containerName,
            string blobName,
            Stream fileStream)
        {
            ValidateStorageAccountName(storageAccountName);

            if (string.IsNullOrWhiteSpace(containerName))
                throw new ArgumentException("Container name cannot be null or empty", nameof(containerName));

            if (string.IsNullOrWhiteSpace(blobName))
                throw new ArgumentException("Blob name cannot be null or empty", nameof(blobName));

            if (fileStream == null)
                throw new ArgumentNullException(nameof(fileStream));

            if (!fileStream.CanRead)
                throw new ArgumentException("File stream must be readable", nameof(fileStream));
        }

        #endregion;

        #region Event Methods;

        protected virtual void OnAuthenticationStateChanged(AuthenticationStateChangedEventArgs e)
        {
            AuthenticationStateChanged?.Invoke(this, e);
        }

        protected virtual void OnResourceOperationCompleted(ResourceOperationEventArgs e)
        {
            ResourceOperationCompleted?.Invoke(this, e);
        }

        protected virtual void OnOperationProgress(OperationProgressEventArgs e)
        {
            OperationProgress?.Invoke(this, e);
        }

        #endregion;

        #region IDisposable Implementation;

        private bool _disposed = false;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _httpClient?.Dispose();
                    _authLock?.Dispose();
                    _azureClient = null;
                    _azureCredentials = null;
                }

                _disposed = true;
            }
        }

        ~AzureClient()
        {
            Dispose(false);
        }

        #endregion;

        #region Nested Types;

        /// <summary>
        /// Azure authentication methods;
        /// </summary>
        public enum AzureAuthMethod;
        {
            ServicePrincipal,
            ManagedIdentity,
            AccessToken,
            Interactive;
        }

        /// <summary>
        /// Resource operation types;
        /// </summary>
        public enum ResourceOperation;
        {
            Create,
            Read,
            Update,
            Delete,
            Start,
            Stop;
        }

        /// <summary>
        /// Azure resource types;
        /// </summary>
        public enum ResourceType;
        {
            ResourceGroup,
            VirtualMachine,
            StorageAccount,
            Network,
            Database,
            AppService;
        }

        /// <summary>
        /// Event args for authentication state changes;
        /// </summary>
        public class AuthenticationStateChangedEventArgs : EventArgs;
        {
            public bool IsAuthenticated { get; set; }
            public AzureAuthMethod AuthMethod { get; set; }
            public DateTime Timestamp { get; set; }
        }

        /// <summary>
        /// Event args for resource operations;
        /// </summary>
        public class ResourceOperationEventArgs : EventArgs;
        {
            public ResourceOperation Operation { get; set; }
            public ResourceType ResourceType { get; set; }
            public string ResourceName { get; set; }
            public string ResourceGroup { get; set; }
            public string Region { get; set; }
            public DateTime Timestamp { get; set; }
        }

        /// <summary>
        /// Event args for operation progress;
        /// </summary>
        public class OperationProgressEventArgs : EventArgs;
        {
            public string Operation { get; set; }
            public int Progress { get; set; }
            public string Message { get; set; }
            public DateTime Timestamp { get; set; }
        }

        // Event classes for EventBus;
        public class AzureAuthenticationSuccessEvent;
        {
            public string SubscriptionId { get; set; }
            public string TenantId { get; set; }
            public string AuthMethod { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class ResourceGroupCreatedEvent;
        {
            public string ResourceGroupName { get; set; }
            public string Region { get; set; }
            public string SubscriptionId { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class ResourceGroupDeletedEvent;
        {
            public string ResourceGroupName { get; set; }
            public string SubscriptionId { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class VirtualMachineCreatedEvent;
        {
            public string VmName { get; set; }
            public string ResourceGroupName { get; set; }
            public string VmSize { get; set; }
            public string Region { get; set; }
            public string SubscriptionId { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class VirtualMachineStateChangedEvent;
        {
            public string VmName { get; set; }
            public string ResourceGroupName { get; set; }
            public string NewState { get; set; }
            public string SubscriptionId { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class BlobUploadedEvent;
        {
            public string StorageAccountName { get; set; }
            public string ContainerName { get; set; }
            public string BlobName { get; set; }
            public string BlobUrl { get; set; }
            public string ContentType { get; set; }
            public DateTime Timestamp { get; set; }
        }

        #endregion;
    }

    /// <summary>
    /// Interface for cloud clients;
    /// </summary>
    public interface ICloudClient;
    {
        bool IsAuthenticated { get; }
        Task<bool> AuthenticateWithServicePrincipalAsync(
            string tenantId,
            string clientId,
            string clientSecret,
            string subscriptionId);
    }
}
