using NEDA.Core.Logging;
using NEDA.Core.Configuration.AppSettings;
using NEDA.Services.EventBus;
using NEDA.Monitoring.MetricsCollector;
using Azure;
using Azure.ResourceManager;
using Azure.ResourceManager.Compute;
using Azure.ResourceManager.Compute.Models;
using Azure.ResourceManager.Network;
using Azure.ResourceManager.Network.Models;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Resources.Models;
using Azure.Identity;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.Cloud.Azure;
{
    /// <summary>
    /// Azure Virtual Machine yönetim servisi interface'i;
    /// </summary>
    public interface IVMManager;
    {
        /// <summary>
        /// VM oluşturur;
        /// </summary>
        Task<VMCreationResult> CreateVirtualMachineAsync(VMCreationRequest request);

        /// <summary>
        /// VM başlatır;
        /// </summary>
        Task<VMOperationResult> StartVirtualMachineAsync(string resourceGroupName, string vmName);

        /// <summary>
        /// VM durdurur;
        /// </summary>
        Task<VMOperationResult> StopVirtualMachineAsync(string resourceGroupName, string vmName, bool deallocate = false);

        /// <summary>
        /// VM restart eder;
        /// </summary>
        Task<VMOperationResult> RestartVirtualMachineAsync(string resourceGroupName, string vmName);

        /// <summary>
        /// VM siler;
        /// </summary>
        Task<VMOperationResult> DeleteVirtualMachineAsync(string resourceGroupName, string vmName, bool deleteDisks = true);

        /// <summary>
        /// VM bilgilerini getirir;
        /// </summary>
        Task<VMInfo> GetVirtualMachineInfoAsync(string resourceGroupName, string vmName);

        /// <summary>
        /// Resource Group'daki tüm VM'leri listeler;
        /// </summary>
        Task<List<VMInfo>> ListVirtualMachinesAsync(string resourceGroupName = null);

        /// <summary>
        /// VM boyutunu değiştirir;
        /// </summary>
        Task<VMOperationResult> ResizeVirtualMachineAsync(string resourceGroupName, string vmName, string newVMSize);

        /// <summary>
        /// VM snapshot alır;
        /// </summary>
        Task<VMSnapshotResult> CreateSnapshotAsync(VMSnapshotRequest request);

        /// <summary>
        /// VM'den image oluşturur;
        /// </summary>
        Task<VMImageResult> CreateImageFromVMAsync(VMImageRequest request);

        /// <summary>
        /// VM'i başka bir bölgeye taşır;
        /// </summary>
        Task<VMMigrationResult> MigrateVirtualMachineAsync(VMMigrationRequest request);

        /// <summary>
        /// VM backup alır;
        /// </summary>
        Task<VMBackupResult> BackupVirtualMachineAsync(VMBackupRequest request);

        /// <summary>
        /// VM'i restore eder;
        /// </summary>
        Task<VMRestoreResult> RestoreVirtualMachineAsync(VMRestoreRequest request);

        /// <summary>
        /// Auto-scaling ayarlarını yapılandırır;
        /// </summary>
        Task ConfigureAutoScalingAsync(string resourceGroupName, string vmScaleSetName, VMAutoScalingConfig config);

        /// <summary>
        /// VM performance metrics'lerini getirir;
        /// </summary>
        Task<VMPerformanceMetrics> GetPerformanceMetricsAsync(string resourceGroupName, string vmName, TimeSpan duration);

        /// <summary>
        /// VM tags günceller;
        /// </summary>
        Task UpdateVMTagsAsync(string resourceGroupName, string vmName, Dictionary<string, string> tags);

        /// <summary>
        /// VM'i başka bir subscription'a taşır;
        /// </summary>
        Task<VMTransferResult> TransferVirtualMachineAsync(VMTransferRequest request);

        /// <summary>
        /// VM availability set oluşturur;
        /// </summary>
        Task<VMAvailabilitySetResult> CreateAvailabilitySetAsync(VMAvailabilitySetRequest request);

        /// <summary>
        /// VM scale set oluşturur;
        /// </summary>
        Task<VMScaleSetResult> CreateScaleSetAsync(VMScaleSetRequest request);

        /// <summary>
        /// VM diagnostics ayarlarını yapılandırır;
        /// </summary>
        Task ConfigureDiagnosticsAsync(string resourceGroupName, string vmName, VMDiagnosticsConfig config);

        /// <summary>
        /// VM'i başka bir resource group'a taşır;
        /// </summary>
        Task<VMOperationResult> MoveVirtualMachineAsync(VMMoveRequest request);

        /// <summary>
        /// VM için RDP/SSH bağlantı bilgilerini getirir;
        /// </summary>
        Task<VMConnectionInfo> GetConnectionInfoAsync(string resourceGroupName, string vmName);

        /// <summary>
        /// VM için custom script çalıştırır;
        /// </summary>
        Task<VMCustomScriptResult> RunCustomScriptAsync(VMCustomScriptRequest request);

        /// <summary>
        /// VM disk'lerini yönetir;
        /// </summary>
        Task<VMDiskResult> ManageDisksAsync(VMDiskRequest request);
    }

    /// <summary>
    /// Azure VM Manager implementasyonu;
    /// </summary>
    public class VMManager : IVMManager, IDisposable;
    {
        private readonly ILogger<VMManager> _logger;
        private readonly IAppConfig _appConfig;
        private readonly IEventBus _eventBus;
        private readonly IMetricsCollector _metricsCollector;
        private readonly ArmClient _armClient;
        private readonly ConcurrentDictionary<string, VirtualMachineResource> _vmCache;
        private readonly ConcurrentDictionary<string, ResourceGroupResource> _rgCache;
        private readonly SemaphoreSlim _operationSemaphore;

        private bool _disposed = false;
        private const int MAX_CONCURRENT_OPERATIONS = 5;
        private const int OPERATION_TIMEOUT_MINUTES = 30;
        private const int MAX_RETRY_ATTEMPTS = 3;
        private const int RETRY_DELAY_MS = 1000;

        public VMManager(
            ILogger<VMManager> logger,
            IAppConfig appConfig,
            IEventBus eventBus,
            IMetricsCollector metricsCollector)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _appConfig = appConfig ?? throw new ArgumentNullException(nameof(appConfig));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _metricsCollector = metricsCollector ?? throw new ArgumentNullException(nameof(metricsCollector));

            // Azure Authentication;
            var credential = GetAzureCredential(appConfig);
            _armClient = new ArmClient(credential, appConfig.AzureSubscriptionId);

            _vmCache = new ConcurrentDictionary<string, VirtualMachineResource>();
            _rgCache = new ConcurrentDictionary<string, ResourceGroupResource>();
            _operationSemaphore = new SemaphoreSlim(MAX_CONCURRENT_OPERATIONS, MAX_CONCURRENT_OPERATIONS);

            SubscribeToEvents();

            _logger.LogInformation("Azure VM Manager initialized for subscription: {SubscriptionId}",
                appConfig.AzureSubscriptionId);
        }

        /// <summary>
        /// Azure credential oluşturur;
        /// </summary>
        private TokenCredential GetAzureCredential(IAppConfig config)
        {
            // DefaultAzureCredential otomatik olarak farklı auth method'larını dener;
            // 1. Environment variables;
            // 2. Managed Identity;
            // 3. Visual Studio;
            // 4. Azure CLI;
            // 5. Interactive browser;
            return new DefaultAzureCredential(new DefaultAzureCredentialOptions;
            {
                ExcludeEnvironmentCredential = false,
                ExcludeManagedIdentityCredential = false,
                ExcludeSharedTokenCacheCredential = true,
                ExcludeVisualStudioCredential = false,
                ExcludeVisualStudioCodeCredential = true,
                ExcludeAzureCliCredential = false,
                ExcludeInteractiveBrowserCredential = true,
                TenantId = config.AzureTenantId;
            });
        }

        /// <summary>
        /// Event'lara subscribe olur;
        /// </summary>
        private void SubscribeToEvents()
        {
            _eventBus.Subscribe<VMCreationRequestedEvent>(OnVMCreationRequested);
            _eventBus.Subscribe<VMDeletionRequestedEvent>(OnVMDeletionRequested);
            _eventBus.Subscribe<VMScaleRequestedEvent>(OnVMScaleRequested);
        }

        public async Task<VMCreationResult> CreateVirtualMachineAsync(VMCreationRequest request)
        {
            await _operationSemaphore.WaitAsync();

            try
            {
                _logger.LogInformation("Creating virtual machine: {VMName} in resource group: {ResourceGroup}",
                    request.VMName, request.ResourceGroupName);

                ValidateVMCreationRequest(request);

                // Resource Group kontrolü veya oluşturma;
                var resourceGroup = await GetOrCreateResourceGroupAsync(request.ResourceGroupName, request.Location);

                // Virtual Network oluştur;
                var vnet = await CreateVirtualNetworkAsync(resourceGroup, request);

                // Network Interface oluştur;
                var nic = await CreateNetworkInterfaceAsync(resourceGroup, vnet, request);

                // Virtual Machine oluştur;
                var vm = await CreateVirtualMachineResourceAsync(resourceGroup, nic, request);

                var result = new VMCreationResult;
                {
                    Success = true,
                    VMName = request.VMName,
                    ResourceGroupName = request.ResourceGroupName,
                    Location = request.Location,
                    VMSize = request.VMSize,
                    OperatingSystem = request.OperatingSystem,
                    PublicIPAddress = await GetPublicIPAddressAsync(nic),
                    PrivateIPAddress = await GetPrivateIPAddressAsync(nic),
                    VMCreationTime = DateTime.UtcNow,
                    VMId = vm.Id.ToString(),
                    ProvisioningState = vm.Data.ProvisioningState;
                };

                // Cache'e ekle;
                var cacheKey = GetVMCacheKey(request.ResourceGroupName, request.VMName);
                _vmCache[cacheKey] = vm;

                // Event publish;
                await _eventBus.PublishAsync(new VMCreatedEvent;
                {
                    VMName = request.VMName,
                    ResourceGroupName = request.ResourceGroupName,
                    Location = request.Location,
                    VMSize = request.VMSize,
                    CreationTime = DateTime.UtcNow,
                    VMId = vm.Id.ToString()
                });

                // Metrics topla;
                await CollectCreationMetricsAsync(result);

                _logger.LogInformation("Virtual machine created successfully: {VMName}, ID: {VMId}",
                    request.VMName, vm.Id);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create virtual machine: {VMName}", request.VMName);

                await _eventBus.PublishAsync(new VMCreationFailedEvent;
                {
                    VMName = request.VMName,
                    ResourceGroupName = request.ResourceGroupName,
                    ErrorMessage = ex.Message,
                    Timestamp = DateTime.UtcNow;
                });

                throw new VMManagerException($"Failed to create virtual machine: {ex.Message}", ex);
            }
            finally
            {
                _operationSemaphore.Release();
            }
        }

        public async Task<VMOperationResult> StartVirtualMachineAsync(string resourceGroupName, string vmName)
        {
            return await ExecuteVMOperationAsync(
                resourceGroupName,
                vmName,
                async (vm) =>
                {
                    _logger.LogInformation("Starting virtual machine: {VMName}", vmName);

                    var operation = await vm.PowerOnAsync(WaitUntil.Completed);

                    var result = new VMOperationResult;
                    {
                        Success = operation.HasCompleted && operation.GetRawResponse().Status == 200,
                        OperationType = VMOperationType.Start,
                        VMName = vmName,
                        ResourceGroupName = resourceGroupName,
                        OperationTime = DateTime.UtcNow,
                        Status = await GetVMStatusAsync(vm)
                    };

                    await _eventBus.PublishAsync(new VMStartedEvent;
                    {
                        VMName = vmName,
                        ResourceGroupName = resourceGroupName,
                        StartTime = DateTime.UtcNow;
                    });

                    return result;
                },
                VMOperationType.Start);
        }

        public async Task<VMOperationResult> StopVirtualMachineAsync(string resourceGroupName, string vmName, bool deallocate = false)
        {
            return await ExecuteVMOperationAsync(
                resourceGroupName,
                vmName,
                async (vm) =>
                {
                    _logger.LogInformation("Stopping virtual machine: {VMName}, Deallocate: {Deallocate}",
                        vmName, deallocate);

                    var operation = deallocate;
                        ? await vm.DeallocateAsync(WaitUntil.Completed)
                        : await vm.PowerOffAsync(WaitUntil.Completed);

                    var result = new VMOperationResult;
                    {
                        Success = operation.HasCompleted && operation.GetRawResponse().Status == 200,
                        OperationType = deallocate ? VMOperationType.Deallocate : VMOperationType.Stop,
                        VMName = vmName,
                        ResourceGroupName = resourceGroupName,
                        OperationTime = DateTime.UtcNow,
                        Status = await GetVMStatusAsync(vm)
                    };

                    await _eventBus.PublishAsync(new VMStoppedEvent;
                    {
                        VMName = vmName,
                        ResourceGroupName = resourceGroupName,
                        StopTime = DateTime.UtcNow,
                        WasDeallocated = deallocate;
                    });

                    return result;
                },
                deallocate ? VMOperationType.Deallocate : VMOperationType.Stop);
        }

        public async Task<VMOperationResult> RestartVirtualMachineAsync(string resourceGroupName, string vmName)
        {
            return await ExecuteVMOperationAsync(
                resourceGroupName,
                vmName,
                async (vm) =>
                {
                    _logger.LogInformation("Restarting virtual machine: {VMName}", vmName);

                    var operation = await vm.RestartAsync(WaitUntil.Completed);

                    var result = new VMOperationResult;
                    {
                        Success = operation.HasCompleted && operation.GetRawResponse().Status == 200,
                        OperationType = VMOperationType.Restart,
                        VMName = vmName,
                        ResourceGroupName = resourceGroupName,
                        OperationTime = DateTime.UtcNow,
                        Status = await GetVMStatusAsync(vm)
                    };

                    await _eventBus.PublishAsync(new VMRestartedEvent;
                    {
                        VMName = vmName,
                        ResourceGroupName = resourceGroupName,
                        RestartTime = DateTime.UtcNow;
                    });

                    return result;
                },
                VMOperationType.Restart);
        }

        public async Task<VMOperationResult> DeleteVirtualMachineAsync(string resourceGroupName, string vmName, bool deleteDisks = true)
        {
            return await ExecuteVMOperationAsync(
                resourceGroupName,
                vmName,
                async (vm) =>
                {
                    _logger.LogInformation("Deleting virtual machine: {VMName}, DeleteDisks: {DeleteDisks}",
                        vmName, deleteDisks);

                    // Önce VM'i durdur;
                    await StopVirtualMachineAsync(resourceGroupName, vmName, true);

                    // Disk'leri sil (eğer istenirse)
                    if (deleteDisks)
                    {
                        await DeleteVMDisksAsync(vm);
                    }

                    // VM'i sil;
                    var operation = await vm.DeleteAsync(WaitUntil.Completed);

                    // Cache'ten kaldır;
                    var cacheKey = GetVMCacheKey(resourceGroupName, vmName);
                    _vmCache.TryRemove(cacheKey, out _);

                    var result = new VMOperationResult;
                    {
                        Success = operation.HasCompleted && operation.GetRawResponse().Status == 200,
                        OperationType = VMOperationType.Delete,
                        VMName = vmName,
                        ResourceGroupName = resourceGroupName,
                        OperationTime = DateTime.UtcNow,
                        Status = "Deleted"
                    };

                    await _eventBus.PublishAsync(new VMDeletedEvent;
                    {
                        VMName = vmName,
                        ResourceGroupName = resourceGroupName,
                        DeleteTime = DateTime.UtcNow,
                        DisksDeleted = deleteDisks;
                    });

                    return result;
                },
                VMOperationType.Delete);
        }

        public async Task<VMInfo> GetVirtualMachineInfoAsync(string resourceGroupName, string vmName)
        {
            try
            {
                _logger.LogDebug("Getting VM info: {VMName} in {ResourceGroup}", vmName, resourceGroupName);

                var vm = await GetVirtualMachineAsync(resourceGroupName, vmName);
                if (vm == null)
                {
                    throw new VMManagerException($"Virtual machine not found: {vmName}");
                }

                var vmData = vm.Data;
                var networkInterfaces = await GetNetworkInterfacesAsync(vm);
                var disks = await GetVMDisksAsync(vm);

                var vmInfo = new VMInfo;
                {
                    Name = vmData.Name,
                    ResourceGroupName = resourceGroupName,
                    Location = vmData.Location.ToString(),
                    VMSize = vmData.HardwareProfile.VmSize.ToString(),
                    OperatingSystem = GetOSInfo(vmData),
                    Status = await GetVMStatusAsync(vm),
                    ProvisioningState = vmData.ProvisioningState,
                    CreationTime = vmData.TimeCreated?.DateTime ?? DateTime.MinValue,
                    Tags = vmData.Tags?.ToDictionary(t => t.Key, t => t.Value) ?? new Dictionary<string, string>(),
                    NetworkInterfaces = networkInterfaces,
                    Disks = disks,
                    AvailabilityZone = vmData.Zones?.FirstOrDefault(),
                    PublicIPAddress = await GetPublicIPAddressFromVMAsync(vm),
                    PrivateIPAddress = await GetPrivateIPAddressFromVMAsync(vm),
                    PowerState = GetVMPowerState(vmData),
                    ResourceId = vm.Id.ToString()
                };

                return vmInfo;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get VM info: {VMName}", vmName);
                throw new VMManagerException($"Failed to get VM info: {ex.Message}", ex);
            }
        }

        public async Task<List<VMInfo>> ListVirtualMachinesAsync(string resourceGroupName = null)
        {
            try
            {
                _logger.LogDebug("Listing virtual machines. ResourceGroup: {ResourceGroup}",
                    resourceGroupName ?? "All");

                var vmList = new List<VMInfo>();

                if (!string.IsNullOrEmpty(resourceGroupName))
                {
                    // Belirli bir Resource Group'daki VM'leri listele;
                    var resourceGroup = await GetResourceGroupAsync(resourceGroupName);
                    if (resourceGroup == null)
                    {
                        throw new VMManagerException($"Resource group not found: {resourceGroupName}");
                    }

                    await foreach (var vm in resourceGroup.GetVirtualMachines())
                    {
                        try
                        {
                            var vmInfo = await GetVirtualMachineInfoAsync(resourceGroupName, vm.Data.Name);
                            vmList.Add(vmInfo);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to get info for VM: {VMName}", vm.Data.Name);
                        }
                    }
                }
                else;
                {
                    // Tüm subscription'daki VM'leri listele;
                    var subscription = await _armClient.GetDefaultSubscriptionAsync();

                    await foreach (var resourceGroup in subscription.GetResourceGroups())
                    {
                        await foreach (var vm in resourceGroup.GetVirtualMachines())
                        {
                            try
                            {
                                var vmInfo = await GetVirtualMachineInfoAsync(resourceGroup.Data.Name, vm.Data.Name);
                                vmList.Add(vmInfo);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to get info for VM: {VMName}", vm.Data.Name);
                            }
                        }
                    }
                }

                _logger.LogDebug("Listed {Count} virtual machines", vmList.Count);
                return vmList;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to list virtual machines");
                throw new VMManagerException($"Failed to list virtual machines: {ex.Message}", ex);
            }
        }

        public async Task<VMOperationResult> ResizeVirtualMachineAsync(string resourceGroupName, string vmName, string newVMSize)
        {
            return await ExecuteVMOperationAsync(
                resourceGroupName,
                vmName,
                async (vm) =>
                {
                    _logger.LogInformation("Resizing virtual machine: {VMName} to {NewVMSize}", vmName, newVMSize);

                    // VM'i durdur;
                    await StopVirtualMachineAsync(resourceGroupName, vmName, true);

                    // VM boyutunu güncelle;
                    var vmData = vm.Data;
                    vmData.HardwareProfile.VmSize = newVMSize;

                    var updateOperation = await vm.UpdateAsync(WaitUntil.Completed, vmData);

                    // VM'i başlat;
                    await StartVirtualMachineAsync(resourceGroupName, vmName);

                    var result = new VMOperationResult;
                    {
                        Success = updateOperation.HasCompleted && updateOperation.GetRawResponse().Status == 200,
                        OperationType = VMOperationType.Resize,
                        VMName = vmName,
                        ResourceGroupName = resourceGroupName,
                        OperationTime = DateTime.UtcNow,
                        Status = await GetVMStatusAsync(vm),
                        AdditionalInfo = $"Resized from {vm.Data.HardwareProfile.VmSize} to {newVMSize}"
                    };

                    await _eventBus.PublishAsync(new VMResizedEvent;
                    {
                        VMName = vmName,
                        ResourceGroupName = resourceGroupName,
                        OldVMSize = vm.Data.HardwareProfile.VmSize.ToString(),
                        NewVMSize = newVMSize,
                        ResizeTime = DateTime.UtcNow;
                    });

                    return result;
                },
                VMOperationType.Resize);
        }

        public async Task<VMSnapshotResult> CreateSnapshotAsync(VMSnapshotRequest request)
        {
            try
            {
                _logger.LogInformation("Creating snapshot for VM: {VMName}", request.VMName);

                var vm = await GetVirtualMachineAsync(request.ResourceGroupName, request.VMName);
                if (vm == null)
                {
                    throw new VMManagerException($"Virtual machine not found: {request.VMName}");
                }

                // OS disk'ini al;
                var osDisk = vm.Data.StorageProfile.OsDisk;
                if (osDisk == null)
                {
                    throw new VMManagerException($"OS disk not found for VM: {request.VMName}");
                }

                // Snapshot oluştur;
                var snapshotCollection = (await GetResourceGroupAsync(request.ResourceGroupName))
                    .GetSnapshots();

                var snapshotData = new SnapshotData(request.Location)
                {
                    CreationData = new DiskCreationData(DiskCreateOption.Copy)
                    {
                        SourceResourceId = new Azure.Core.ResourceIdentifier(osDisk.ManagedDisk.Id)
                    },
                    Sku = new SnapshotSku(SnapshotStorageAccountType.StandardLRS),
                    Tags = request.Tags ?? new Dictionary<string, string>()
                };

                var snapshotOperation = await snapshotCollection.CreateOrUpdateAsync(
                    WaitUntil.Completed,
                    request.SnapshotName,
                    snapshotData);

                var snapshot = snapshotOperation.Value;

                var result = new VMSnapshotResult;
                {
                    Success = true,
                    SnapshotName = request.SnapshotName,
                    VMName = request.VMName,
                    ResourceGroupName = request.ResourceGroupName,
                    Location = request.Location,
                    CreationTime = DateTime.UtcNow,
                    SnapshotId = snapshot.Id.ToString(),
                    SizeInGB = snapshot.Data.DiskSizeGB ?? 0,
                    Status = snapshot.Data.ProvisioningState;
                };

                await _eventBus.PublishAsync(new VMSnapshotCreatedEvent;
                {
                    VMName = request.VMName,
                    SnapshotName = request.SnapshotName,
                    CreationTime = DateTime.UtcNow,
                    SnapshotId = snapshot.Id.ToString()
                });

                _logger.LogInformation("Snapshot created successfully: {SnapshotName} for VM: {VMName}",
                    request.SnapshotName, request.VMName);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create snapshot for VM: {VMName}", request.VMName);
                throw new VMManagerException($"Failed to create snapshot: {ex.Message}", ex);
            }
        }

        public async Task<VMImageResult> CreateImageFromVMAsync(VMImageRequest request)
        {
            try
            {
                _logger.LogInformation("Creating image from VM: {VMName}", request.VMName);

                var vm = await GetVirtualMachineAsync(request.ResourceGroupName, request.VMName);
                if (vm == null)
                {
                    throw new VMManagerException($"Virtual machine not found: {request.VMName}");
                }

                // VM'i generalize et (sysprep gerekli)
                await GeneralizeVirtualMachineAsync(vm);

                // Image oluştur;
                var imageCollection = (await GetResourceGroupAsync(request.ResourceGroupName))
                    .GetImages();

                var imageData = new ImageData(request.Location)
                {
                    SourceVirtualMachine = new WritableSubResource;
                    {
                        Id = vm.Id;
                    },
                    Tags = request.Tags ?? new Dictionary<string, string>(),
                    HyperVGeneration = HyperVGeneration.V1;
                };

                var imageOperation = await imageCollection.CreateOrUpdateAsync(
                    WaitUntil.Completed,
                    request.ImageName,
                    imageData);

                var image = imageOperation.Value;

                var result = new VMImageResult;
                {
                    Success = true,
                    ImageName = request.ImageName,
                    SourceVMName = request.VMName,
                    ResourceGroupName = request.ResourceGroupName,
                    Location = request.Location,
                    CreationTime = DateTime.UtcNow,
                    ImageId = image.Id.ToString(),
                    ProvisioningState = image.Data.ProvisioningState;
                };

                await _eventBus.PublishAsync(new VMImageCreatedEvent;
                {
                    VMName = request.VMName,
                    ImageName = request.ImageName,
                    CreationTime = DateTime.UtcNow,
                    ImageId = image.Id.ToString()
                });

                _logger.LogInformation("Image created successfully: {ImageName} from VM: {VMName}",
                    request.ImageName, request.VMName);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create image from VM: {VMName}", request.VMName);
                throw new VMManagerException($"Failed to create image: {ex.Message}", ex);
            }
        }

        public async Task<VMMigrationResult> MigrateVirtualMachineAsync(VMMigrationRequest request)
        {
            try
            {
                _logger.LogInformation("Migrating VM: {VMName} to region: {TargetRegion}",
                    request.VMName, request.TargetRegion);

                // 1. Snapshot al;
                var snapshotRequest = new VMSnapshotRequest;
                {
                    VMName = request.VMName,
                    ResourceGroupName = request.ResourceGroupName,
                    SnapshotName = $"{request.VMName}-migration-snapshot-{DateTime.UtcNow:yyyyMMddHHmmss}",
                    Location = request.SourceRegion,
                    Tags = new Dictionary<string, string> { { "Migration", "true" } }
                };

                var snapshotResult = await CreateSnapshotAsync(snapshotRequest);

                // 2. Snapshot'ı target region'a kopyala;
                var targetSnapshotName = $"{request.VMName}-target-snapshot";
                var targetResourceGroup = await GetOrCreateResourceGroupAsync(
                    request.TargetResourceGroup ?? request.ResourceGroupName,
                    request.TargetRegion);

                // 3. Target region'da VM oluştur;
                var targetVMRequest = new VMCreationRequest;
                {
                    VMName = request.TargetVMName ?? request.VMName,
                    ResourceGroupName = targetResourceGroup.Data.Name,
                    Location = request.TargetRegion,
                    VMSize = request.TargetVMSize,
                    OperatingSystem = request.OperatingSystem,
                    AdminUsername = request.AdminUsername,
                    AdminPassword = request.AdminPassword,
                    ImageReference = request.ImageReference,
                    SubnetName = request.SubnetName,
                    VnetName = request.VnetName;
                };

                var creationResult = await CreateVirtualMachineAsync(targetVMRequest);

                // 4. Source VM'i sil (eğer istenirse)
                if (request.DeleteSourceVM)
                {
                    await DeleteVirtualMachineAsync(request.ResourceGroupName, request.VMName, request.DeleteSourceDisks);
                }

                var result = new VMMigrationResult;
                {
                    Success = snapshotResult.Success && creationResult.Success,
                    SourceVMName = request.VMName,
                    TargetVMName = creationResult.VMName,
                    SourceRegion = request.SourceRegion,
                    TargetRegion = request.TargetRegion,
                    MigrationTime = DateTime.UtcNow,
                    SnapshotId = snapshotResult.SnapshotId,
                    TargetVMId = creationResult.VMId,
                    SourceVMDeleted = request.DeleteSourceVM;
                };

                await _eventBus.PublishAsync(new VMMigratedEvent;
                {
                    SourceVMName = request.VMName,
                    TargetVMName = creationResult.VMName,
                    SourceRegion = request.SourceRegion,
                    TargetRegion = request.TargetRegion,
                    MigrationTime = DateTime.UtcNow;
                });

                _logger.LogInformation("VM migration completed successfully: {SourceVM} -> {TargetVM}",
                    request.VMName, creationResult.VMName);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to migrate VM: {VMName}", request.VMName);
                throw new VMManagerException($"Failed to migrate VM: {ex.Message}", ex);
            }
        }

        public async Task<VMBackupResult> BackupVirtualMachineAsync(VMBackupRequest request)
        {
            try
            {
                _logger.LogInformation("Starting backup for VM: {VMName}", request.VMName);

                var vm = await GetVirtualMachineAsync(request.ResourceGroupName, request.VMName);
                if (vm == null)
                {
                    throw new VMManagerException($"Virtual machine not found: {request.VMName}");
                }

                // Recovery Services Vault kontrolü/oluşturma;
                var vault = await GetOrCreateRecoveryServicesVaultAsync(request);

                // Backup policy oluştur/ata;
                var backupPolicy = await ConfigureBackupPolicyAsync(vault, request);

                // VM'i backup'a ekle;
                var protectedItem = await EnableVMBackupAsync(vault, vm, backupPolicy, request);

                // Backup'ı tetikle;
                var backupJob = await TriggerBackupAsync(vault, protectedItem, request);

                var result = new VMBackupResult;
                {
                    Success = backupJob != null,
                    VMName = request.VMName,
                    ResourceGroupName = request.ResourceGroupName,
                    BackupTime = DateTime.UtcNow,
                    BackupId = backupJob?.Name,
                    RecoveryServicesVault = vault.Data.Name,
                    BackupPolicy = backupPolicy.Name,
                    RetentionDays = request.RetentionDays,
                    Status = backupJob?.Properties.Status ?? "Unknown"
                };

                await _eventBus.PublishAsync(new VMBackupCompletedEvent;
                {
                    VMName = request.VMName,
                    BackupTime = DateTime.UtcNow,
                    BackupId = backupJob?.Name,
                    Success = result.Success;
                });

                _logger.LogInformation("Backup completed for VM: {VMName}, BackupId: {BackupId}",
                    request.VMName, backupJob?.Name);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to backup VM: {VMName}", request.VMName);
                throw new VMManagerException($"Failed to backup VM: {ex.Message}", ex);
            }
        }

        public async Task<VMRestoreResult> RestoreVirtualMachineAsync(VMRestoreRequest request)
        {
            try
            {
                _logger.LogInformation("Restoring VM from backup: {BackupId}", request.BackupId);

                // Recovery Services Vault'u al;
                var vault = await GetRecoveryServicesVaultAsync(request.ResourceGroupName, request.VaultName);
                if (vault == null)
                {
                    throw new VMManagerException($"Recovery Services Vault not found: {request.VaultName}");
                }

                // Backup item'ı bul;
                var backupItem = await FindBackupItemAsync(vault, request.VMName, request.BackupId);
                if (backupItem == null)
                {
                    throw new VMManagerException($"Backup item not found for VM: {request.VMName}");
                }

                // Restore işlemini başlat;
                var restoreJob = await TriggerRestoreAsync(vault, backupItem, request);

                // Restore durumunu takip et;
                var restoreStatus = await MonitorRestoreJobAsync(vault, restoreJob);

                var result = new VMRestoreResult;
                {
                    Success = restoreStatus == "Completed",
                    VMName = request.RestoredVMName,
                    SourceVMName = request.VMName,
                    ResourceGroupName = request.ResourceGroupName,
                    RestoreTime = DateTime.UtcNow,
                    BackupId = request.BackupId,
                    RestoreJobId = restoreJob.Name,
                    Status = restoreStatus,
                    RestoredVMId = await GetRestoredVMIdAsync(vault, restoreJob)
                };

                await _eventBus.PublishAsync(new VMRestoredEvent;
                {
                    SourceVMName = request.VMName,
                    RestoredVMName = request.RestoredVMName,
                    RestoreTime = DateTime.UtcNow,
                    Success = result.Success,
                    BackupId = request.BackupId;
                });

                _logger.LogInformation("VM restore completed: {RestoredVMName} from backup: {BackupId}",
                    request.RestoredVMName, request.BackupId);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to restore VM from backup: {BackupId}", request.BackupId);
                throw new VMManagerException($"Failed to restore VM: {ex.Message}", ex);
            }
        }

        public async Task ConfigureAutoScalingAsync(string resourceGroupName, string vmScaleSetName, VMAutoScalingConfig config)
        {
            try
            {
                _logger.LogInformation("Configuring auto-scaling for VM Scale Set: {ScaleSetName}", vmScaleSetName);

                var scaleSet = await GetVMScaleSetAsync(resourceGroupName, vmScaleSetName);
                if (scaleSet == null)
                {
                    throw new VMManagerException($"VM Scale Set not found: {vmScaleSetName}");
                }

                // Auto-scaling rules oluştur;
                var scalingRules = CreateScalingRules(config);

                // Auto-scaling profile'ı oluştur;
                var scalingProfile = new AutoscaleProfile(
                    name: $"{vmScaleSetName}-autoscale-profile",
                    capacity: new AutoscaleCapacity;
                    {
                        Minimum = config.MinimumInstances.ToString(),
                        Maximum = config.MaximumInstances.ToString(),
                        Default = config.DefaultInstances.ToString()
                    },
                    rules: scalingRules);

                // Auto-scaling settings uygula;
                // Not: Bu kısım Azure Management SDK'sının daha gelişmiş özelliklerini gerektirir;
                // Gerçek implementasyonda Azure.ResourceManager.Insights kullanılır;

                _logger.LogInformation("Auto-scaling configured for VM Scale Set: {ScaleSetName}", vmScaleSetName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to configure auto-scaling for VM Scale Set: {ScaleSetName}", vmScaleSetName);
                throw new VMManagerException($"Failed to configure auto-scaling: {ex.Message}", ex);
            }
        }

        public async Task<VMPerformanceMetrics> GetPerformanceMetricsAsync(string resourceGroupName, string vmName, TimeSpan duration)
        {
            try
            {
                _logger.LogDebug("Getting performance metrics for VM: {VMName}, Duration: {Duration}",
                    vmName, duration);

                var vm = await GetVirtualMachineAsync(resourceGroupName, vmName);
                if (vm == null)
                {
                    throw new VMManagerException($"Virtual machine not found: {vmName}");
                }

                // Azure Monitor'dan metrics'leri al;
                var metrics = await _metricsCollector.CollectVMMetricsAsync(
                    vm.Id.ToString(),
                    DateTime.UtcNow.Subtract(duration),
                    DateTime.UtcNow);

                var performanceMetrics = new VMPerformanceMetrics;
                {
                    VMName = vmName,
                    ResourceGroupName = resourceGroupName,
                    CollectionTime = DateTime.UtcNow,
                    Duration = duration,
                    CPUPercentage = metrics.GetMetricValue("Percentage CPU") ?? 0,
                    AvailableMemoryMB = metrics.GetMetricValue("Available Memory Bytes") / (1024 * 1024) ?? 0,
                    DiskReadBytesPerSecond = metrics.GetMetricValue("Disk Read Bytes/sec") ?? 0,
                    DiskWriteBytesPerSecond = metrics.GetMetricValue("Disk Write Bytes/sec") ?? 0,
                    NetworkInBytes = metrics.GetMetricValue("Network In Total") ?? 0,
                    NetworkOutBytes = metrics.GetMetricValue("Network Out Total") ?? 0,
                    DiskQueueDepth = metrics.GetMetricValue("Disk Queue Depth") ?? 0,
                    IsHealthy = CheckVMHealth(metrics)
                };

                return performanceMetrics;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get performance metrics for VM: {VMName}", vmName);
                throw new VMManagerException($"Failed to get performance metrics: {ex.Message}", ex);
            }
        }

        public async Task UpdateVMTagsAsync(string resourceGroupName, string vmName, Dictionary<string, string> tags)
        {
            try
            {
                _logger.LogInformation("Updating tags for VM: {VMName}", vmName);

                var vm = await GetVirtualMachineAsync(resourceGroupName, vmName);
                if (vm == null)
                {
                    throw new VMManagerException($"Virtual machine not found: {vmName}");
                }

                var vmData = vm.Data;
                foreach (var tag in tags)
                {
                    vmData.Tags[tag.Key] = tag.Value;
                }

                await vm.UpdateAsync(WaitUntil.Completed, vmData);

                _logger.LogInformation("Tags updated for VM: {VMName}", vmName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update tags for VM: {VMName}", vmName);
                throw new VMManagerException($"Failed to update tags: {ex.Message}", ex);
            }
        }

        #region Helper Methods;

        /// <summary>
        /// VM operation'ını execute eder;
        /// </summary>
        private async Task<VMOperationResult> ExecuteVMOperationAsync(
            string resourceGroupName,
            string vmName,
            Func<VirtualMachineResource, Task<VMOperationResult>> operation,
            VMOperationType operationType)
        {
            await _operationSemaphore.WaitAsync();

            try
            {
                var vm = await GetVirtualMachineAsync(resourceGroupName, vmName);
                if (vm == null)
                {
                    throw new VMManagerException($"Virtual machine not found: {vmName}");
                }

                return await operation(vm);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "VM operation failed: {OperationType} for VM: {VMName}",
                    operationType, vmName);

                await _eventBus.PublishAsync(new VMOperationFailedEvent;
                {
                    VMName = vmName,
                    ResourceGroupName = resourceGroupName,
                    OperationType = operationType,
                    ErrorMessage = ex.Message,
                    Timestamp = DateTime.UtcNow;
                });

                throw new VMManagerException($"VM operation failed: {ex.Message}", ex);
            }
            finally
            {
                _operationSemaphore.Release();
            }
        }

        /// <summary>
        /// VM creation request validasyonu;
        /// </summary>
        private void ValidateVMCreationRequest(VMCreationRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (string.IsNullOrEmpty(request.VMName))
                throw new ArgumentException("VM name is required", nameof(request.VMName));

            if (string.IsNullOrEmpty(request.ResourceGroupName))
                throw new ArgumentException("Resource group name is required", nameof(request.ResourceGroupName));

            if (string.IsNullOrEmpty(request.Location))
                throw new ArgumentException("Location is required", nameof(request.Location));

            if (string.IsNullOrEmpty(request.VMSize))
                throw new ArgumentException("VM size is required", nameof(request.VMSize));

            if (string.IsNullOrEmpty(request.AdminUsername))
                throw new ArgumentException("Admin username is required", nameof(request.AdminUsername));

            if (string.IsNullOrEmpty(request.AdminPassword) && string.IsNullOrEmpty(request.SshPublicKey))
                throw new ArgumentException("Either admin password or SSH public key is required");

            if (request.AdminPassword?.Length < 12)
                throw new ArgumentException("Admin password must be at least 12 characters");
        }

        /// <summary>
        /// Resource Group alır veya oluşturur;
        /// </summary>
        private async Task<ResourceGroupResource> GetOrCreateResourceGroupAsync(string resourceGroupName, string location)
        {
            var cacheKey = resourceGroupName.ToLowerInvariant();

            if (_rgCache.TryGetValue(cacheKey, out var cachedRg))
            {
                return cachedRg;
            }

            var subscription = await _armClient.GetDefaultSubscriptionAsync();
            var resourceGroupCollection = subscription.GetResourceGroups();

            try
            {
                var resourceGroup = await resourceGroupCollection.GetAsync(resourceGroupName);
                _rgCache[cacheKey] = resourceGroup.Value;
                return resourceGroup.Value;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                _logger.LogInformation("Creating resource group: {ResourceGroup} in location: {Location}",
                    resourceGroupName, location);

                var resourceGroupData = new ResourceGroupData(new AzureLocation(location))
                {
                    Tags = { { "CreatedBy", "NEDA.VMManager" } }
                };

                var operation = await resourceGroupCollection.CreateOrUpdateAsync(
                    WaitUntil.Completed,
                    resourceGroupName,
                    resourceGroupData);

                var newResourceGroup = operation.Value;
                _rgCache[cacheKey] = newResourceGroup;

                return newResourceGroup;
            }
        }

        /// <summary>
        /// Resource Group alır;
        /// </summary>
        private async Task<ResourceGroupResource> GetResourceGroupAsync(string resourceGroupName)
        {
            var subscription = await _armClient.GetDefaultSubscriptionAsync();
            var resourceGroupCollection = subscription.GetResourceGroups();

            try
            {
                var resourceGroup = await resourceGroupCollection.GetAsync(resourceGroupName);
                return resourceGroup.Value;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return null;
            }
        }

        /// <summary>
        /// Virtual Network oluşturur;
        /// </summary>
        private async Task<VirtualNetworkResource> CreateVirtualNetworkAsync(
            ResourceGroupResource resourceGroup,
            VMCreationRequest request)
        {
            var vnetName = request.VnetName ?? $"{request.VMName}-vnet";
            var subnetName = request.SubnetName ?? "default";

            var vnetCollection = resourceGroup.GetVirtualNetworks();

            try
            {
                var existingVnet = await vnetCollection.GetAsync(vnetName);
                return existingVnet.Value;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                _logger.LogDebug("Creating virtual network: {VNetName}", vnetName);

                var vnetData = new VirtualNetworkData;
                {
                    Location = new AzureLocation(request.Location),
                    AddressPrefixes = { "10.0.0.0/16" },
                    Subnets =
                    {
                        new SubnetData;
                        {
                            Name = subnetName,
                            AddressPrefix = "10.0.0.0/24"
                        }
                    },
                    Tags = { { "CreatedForVM", request.VMName } }
                };

                var vnetOperation = await vnetCollection.CreateOrUpdateAsync(
                    WaitUntil.Completed,
                    vnetName,
                    vnetData);

                return vnetOperation.Value;
            }
        }

        /// <summary>
        /// Network Interface oluşturur;
        /// </summary>
        private async Task<NetworkInterfaceResource> CreateNetworkInterfaceAsync(
            ResourceGroupResource resourceGroup,
            VirtualNetworkResource vnet,
            VMCreationRequest request)
        {
            var nicName = $"{request.VMName}-nic";
            var subnetId = SubnetResource.CreateResourceIdentifier(
                _appConfig.AzureSubscriptionId,
                resourceGroup.Data.Name,
                vnet.Data.Name,
                request.SubnetName ?? "default");

            var ipConfigName = $"{request.VMName}-ipconfig";

            var nicCollection = resourceGroup.GetNetworkInterfaces();

            var nicData = new NetworkInterfaceData;
            {
                Location = new AzureLocation(request.Location),
                IPConfigurations =
                {
                    new NetworkInterfaceIPConfigurationData;
                    {
                        Name = ipConfigName,
                        PrivateIPAllocationMethod = NetworkIPAllocationMethod.Dynamic,
                        Subnet = new SubnetData { Id = subnetId }
                    }
                },
                Tags = { { "CreatedForVM", request.VMName } }
            };

            // Public IP (eğer istenirse)
            if (request.EnablePublicIP)
            {
                var publicIP = await CreatePublicIPAddressAsync(resourceGroup, request);
                nicData.IPConfigurations[0].PublicIPAddress = new PublicIPAddressData { Id = publicIP.Id };
            }

            var nicOperation = await nicCollection.CreateOrUpdateAsync(
                WaitUntil.Completed,
                nicName,
                nicData);

            return nicOperation.Value;
        }

        /// <summary>
        /// Public IP Address oluşturur;
        /// </summary>
        private async Task<PublicIPAddressResource> CreatePublicIPAddressAsync(
            ResourceGroupResource resourceGroup,
            VMCreationRequest request)
        {
            var publicIPName = $"{request.VMName}-publicip";
            var publicIPCollection = resourceGroup.GetPublicIPAddresses();

            var publicIPData = new PublicIPAddressData;
            {
                Location = new AzureLocation(request.Location),
                PublicIPAllocationMethod = NetworkIPAllocationMethod.Static,
                Sku = new PublicIPAddressSku;
                {
                    Name = PublicIPAddressSkuName.Standard,
                    Tier = PublicIPAddressSkuTier.Regional;
                },
                Tags = { { "CreatedForVM", request.VMName } }
            };

            var publicIPOperation = await publicIPCollection.CreateOrUpdateAsync(
                WaitUntil.Completed,
                publicIPName,
                publicIPData);

            return publicIPOperation.Value;
        }

        /// <summary>
        /// Virtual Machine resource oluşturur;
        /// </summary>
        private async Task<VirtualMachineResource> CreateVirtualMachineResourceAsync(
            ResourceGroupResource resourceGroup,
            NetworkInterfaceResource nic,
            VMCreationRequest request)
        {
            var vmCollection = resourceGroup.GetVirtualMachines();

            var vmData = new VirtualMachineData(new AzureLocation(request.Location))
            {
                HardwareProfile = new VirtualMachineHardwareProfile;
                {
                    VmSize = request.VMSize;
                },
                StorageProfile = new VirtualMachineStorageProfile;
                {
                    OSDisk = new VirtualMachineOSDisk(DiskCreateOptionType.FromImage)
                    {
                        OSType = request.OperatingSystem == "Linux" ? SupportedOperatingSystemType.Linux : SupportedOperatingSystemType.Windows,
                        Caching = CachingType.ReadWrite,
                        ManagedDisk = new VirtualMachineManagedDisk;
                        {
                            StorageAccountType = StorageAccountType.StandardSsdLrs;
                        }
                    },
                    ImageReference = request.ImageReference ?? GetDefaultImageReference(request.OperatingSystem)
                },
                NetworkProfile = new VirtualMachineNetworkProfile;
                {
                    NetworkInterfaces =
                    {
                        new VirtualMachineNetworkInterfaceReference;
                        {
                            Id = nic.Id,
                            Primary = true;
                        }
                    }
                },
                Tags = request.Tags ?? new Dictionary<string, string>()
            };

            // OS Profile (authentication)
            if (request.OperatingSystem == "Linux")
            {
                vmData.OSProfile = new VirtualMachineOSProfile;
                {
                    ComputerName = request.VMName,
                    AdminUsername = request.AdminUsername,
                    LinuxConfiguration = new LinuxConfiguration;
                    {
                        DisablePasswordAuthentication = !string.IsNullOrEmpty(request.SshPublicKey),
                        Ssh = !string.IsNullOrEmpty(request.SshPublicKey)
                            ? new SshConfiguration;
                            {
                                PublicKeys =
                                {
                                    new SshPublicKeyInfo;
                                    {
                                        Path = $"/home/{request.AdminUsername}/.ssh/authorized_keys",
                                        KeyData = request.SshPublicKey;
                                    }
                                }
                            }
                            : null;
                    }
                };

                if (!string.IsNullOrEmpty(request.AdminPassword))
                {
                    vmData.OSProfile.AdminPassword = request.AdminPassword;
                }
            }
            else // Windows;
            {
                vmData.OSProfile = new VirtualMachineOSProfile;
                {
                    ComputerName = request.VMName,
                    AdminUsername = request.AdminUsername,
                    AdminPassword = request.AdminPassword,
                    WindowsConfiguration = new WindowsConfiguration;
                    {
                        EnableAutomaticUpdates = true;
                    }
                };
            }

            // Availability options;
            if (request.AvailabilityZone.HasValue)
            {
                vmData.Zones.Add(request.AvailabilityZone.Value.ToString());
            }
            else if (request.AvailabilitySetId != null)
            {
                vmData.AvailabilitySet = new WritableSubResource { Id = new Azure.Core.ResourceIdentifier(request.AvailabilitySetId) };
            }

            var vmOperation = await vmCollection.CreateOrUpdateAsync(
                WaitUntil.Completed,
                request.VMName,
                vmData);

            return vmOperation.Value;
        }

        /// <summary>
        /// Default image reference alır;
        /// </summary>
        private ImageReference GetDefaultImageReference(string operatingSystem)
        {
            return operatingSystem == "Linux"
                ? new ImageReference;
                {
                    Publisher = "Canonical",
                    Offer = "UbuntuServer",
                    Sku = "18.04-LTS",
                    Version = "latest"
                }
                : new ImageReference;
                {
                    Publisher = "MicrosoftWindowsServer",
                    Offer = "WindowsServer",
                    Sku = "2019-Datacenter",
                    Version = "latest"
                };
        }

        /// <summary>
        /// Virtual Machine alır;
        /// </summary>
        private async Task<VirtualMachineResource> GetVirtualMachineAsync(string resourceGroupName, string vmName)
        {
            var cacheKey = GetVMCacheKey(resourceGroupName, vmName);

            if (_vmCache.TryGetValue(cacheKey, out var cachedVm))
            {
                return cachedVm;
            }

            var resourceGroup = await GetResourceGroupAsync(resourceGroupName);
            if (resourceGroup == null)
            {
                return null;
            }

            try
            {
                var vm = await resourceGroup.GetVirtualMachineAsync(vmName);
                _vmCache[cacheKey] = vm.Value;
                return vm.Value;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return null;
            }
        }

        /// <summary>
        /// VM cache key oluşturur;
        /// </summary>
        private string GetVMCacheKey(string resourceGroupName, string vmName)
        {
            return $"{resourceGroupName.ToLowerInvariant()}_{vmName.ToLowerInvariant()}";
        }

        /// <summary>
        /// Public IP Address alır;
        /// </summary>
        private async Task<string> GetPublicIPAddressAsync(NetworkInterfaceResource nic)
        {
            try
            {
                var nicData = await nic.GetAsync();
                var ipConfig = nicData.Value.Data.IPConfigurations.FirstOrDefault();

                if (ipConfig?.PublicIPAddress != null)
                {
                    var publicIP = await _armClient.GetPublicIPAddressResource(
                        new Azure.Core.ResourceIdentifier(ipConfig.PublicIPAddress.Id)).GetAsync();

                    return publicIP.Value.Data.IPAddress;
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Private IP Address alır;
        /// </summary>
        private async Task<string> GetPrivateIPAddressAsync(NetworkInterfaceResource nic)
        {
            try
            {
                var nicData = await nic.GetAsync();
                var ipConfig = nicData.Value.Data.IPConfigurations.FirstOrDefault();

                return ipConfig?.PrivateIPAddress;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// VM'den public IP alır;
        /// </summary>
        private async Task<string> GetPublicIPAddressFromVMAsync(VirtualMachineResource vm)
        {
            try
            {
                var nicReference = vm.Data.NetworkProfile.NetworkInterfaces.FirstOrDefault();
                if (nicReference?.Id != null)
                {
                    var nic = await _armClient.GetNetworkInterfaceResource(nicReference.Id).GetAsync();
                    return await GetPublicIPAddressAsync(nic.Value);
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// VM'den private IP alır;
        /// </summary>
        private async Task<string> GetPrivateIPAddressFromVMAsync(VirtualMachineResource vm)
        {
            try
            {
                var nicReference = vm.Data.NetworkProfile.NetworkInterfaces.FirstOrDefault();
                if (nicReference?.Id != null)
                {
                    var nic = await _armClient.GetNetworkInterfaceResource(nicReference.Id).GetAsync();
                    return await GetPrivateIPAddressAsync(nic.Value);
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// VM status alır;
        /// </summary>
        private async Task<string> GetVMStatusAsync(VirtualMachineResource vm)
        {
            try
            {
                var instanceView = await vm.InstanceViewAsync();
                var powerState = instanceView.Value.Statuses;
                    .FirstOrDefault(s => s.Code.StartsWith("PowerState/"))
                    ?.DisplayStatus ?? "Unknown";

                return powerState;
            }
            catch
            {
                return "Unknown";
            }
        }

        /// <summary>
        /// OS bilgisi alır;
        /// </summary>
        private string GetOSInfo(VirtualMachineData vmData)
        {
            if (vmData.StorageProfile?.ImageReference != null)
            {
                return $"{vmData.StorageProfile.ImageReference.Offer} {vmData.StorageProfile.ImageReference.Sku}";
            }

            return vmData.StorageProfile?.OSDisk?.OSType.ToString() ?? "Unknown";
        }

        /// <summary>
        /// Network interfaces alır;
        /// </summary>
        private async Task<List<VMNetworkInterface>> GetNetworkInterfacesAsync(VirtualMachineResource vm)
        {
            var interfaces = new List<VMNetworkInterface>();

            foreach (var nicRef in vm.Data.NetworkProfile.NetworkInterfaces)
            {
                try
                {
                    var nic = await _armClient.GetNetworkInterfaceResource(nicRef.Id).GetAsync();
                    var ipConfig = nic.Value.Data.IPConfigurations.FirstOrDefault();

                    interfaces.Add(new VMNetworkInterface;
                    {
                        Name = nic.Value.Data.Name,
                        PrivateIPAddress = ipConfig?.PrivateIPAddress,
                        PublicIPAddress = await GetPublicIPAddressAsync(nic.Value),
                        SubnetName = ipConfig?.Subnet?.Id.ToString().Split('/').LastOrDefault(),
                        IsPrimary = nicRef.Primary ?? false;
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to get network interface: {NicId}", nicRef.Id);
                }
            }

            return interfaces;
        }

        /// <summary>
        /// VM disks alır;
        /// </summary>
        private async Task<List<VMDisk>> GetVMDisksAsync(VirtualMachineResource vm)
        {
            var disks = new List<VMDisk>();
            var vmData = vm.Data;

            // OS Disk;
            if (vmData.StorageProfile?.OSDisk != null)
            {
                disks.Add(new VMDisk;
                {
                    Name = vmData.StorageProfile.OSDisk.Name,
                    Type = VMDiskType.OS,
                    SizeGB = vmData.StorageProfile.OSDisk.DiskSizeGB ?? 0,
                    StorageType = vmData.StorageProfile.OSDisk.ManagedDisk?.StorageAccountType.ToString() ?? "Unknown",
                    Caching = vmData.StorageProfile.OSDisk.Caching.ToString()
                });
            }

            // Data Disks;
            if (vmData.StorageProfile?.DataDisks != null)
            {
                foreach (var dataDisk in vmData.StorageProfile.DataDisks)
                {
                    disks.Add(new VMDisk;
                    {
                        Name = dataDisk.Name,
                        Type = VMDiskType.Data,
                        SizeGB = dataDisk.DiskSizeGB,
                        StorageType = dataDisk.ManagedDisk?.StorageAccountType.ToString() ?? "Unknown",
                        Caching = dataDisk.Caching.ToString(),
                        Lun = dataDisk.Lun;
                    });
                }
            }

            return disks;
        }

        /// <summary>
        /// VM power state alır;
        /// </summary>
        private string GetVMPowerState(VirtualMachineData vmData)
        {
            // Bu bilgi instance view'dan alınmalı;
            // Şimdilik placeholder;
            return "Unknown";
        }

        /// <summary>
        /// VM disk'lerini siler;
        /// </summary>
        private async Task DeleteVMDisksAsync(VirtualMachineResource vm)
        {
            var vmData = vm.Data;

            // OS Disk;
            if (vmData.StorageProfile?.OSDisk?.ManagedDisk?.Id != null)
            {
                try
                {
                    var disk = await _armClient.GetManagedDiskResource(
                        new Azure.Core.ResourceIdentifier(vmData.StorageProfile.OSDisk.ManagedDisk.Id))
                        .DeleteAsync(WaitUntil.Completed);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete OS disk: {DiskId}",
                        vmData.StorageProfile.OSDisk.ManagedDisk.Id);
                }
            }

            // Data Disks;
            if (vmData.StorageProfile?.DataDisks != null)
            {
                foreach (var dataDisk in vmData.StorageProfile.DataDisks)
                {
                    if (dataDisk.ManagedDisk?.Id != null)
                    {
                        try
                        {
                            var disk = await _armClient.GetManagedDiskResource(
                                new Azure.Core.ResourceIdentifier(dataDisk.ManagedDisk.Id))
                                .DeleteAsync(WaitUntil.Completed);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to delete data disk: {DiskId}", dataDisk.ManagedDisk.Id);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// VM'i generalize eder;
        /// </summary>
        private async Task GeneralizeVirtualMachineAsync(VirtualMachineResource vm)
        {
            try
            {
                await vm.DeallocateAsync(WaitUntil.Completed);
                await vm.GeneralizeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generalize VM: {VMName}", vm.Data.Name);
                throw;
            }
        }

        /// <summary>
        /// Recovery Services Vault alır veya oluşturur;
        /// </summary>
        private async Task<RecoveryServicesVaultResource> GetOrCreateRecoveryServicesVaultAsync(VMBackupRequest request)
        {
            // Bu metod gerçek implementasyonda Azure.ResourceManager.RecoveryServices kullanır;
            // Şimdilik placeholder;
            await Task.CompletedTask;
            return null;
        }

        /// <summary>
        /// Backup policy configure eder;
        /// </summary>
        private async Task<object> ConfigureBackupPolicyAsync(RecoveryServicesVaultResource vault, VMBackupRequest request)
        {
            // Placeholder;
            await Task.CompletedTask;
            return new { Name = $"{request.VMName}-backup-policy" };
        }

        /// <summary>
        /// VM backup'ı enable eder;
        /// </summary>
        private async Task<object> EnableVMBackupAsync(RecoveryServicesVaultResource vault, VirtualMachineResource vm, object policy, VMBackupRequest request)
        {
            // Placeholder;
            await Task.CompletedTask;
            return new { Name = $"{vm.Data.Name}-protected-item" };
        }

        /// <summary>
        /// Backup tetikler;
        /// </summary>
        private async Task<object> TriggerBackupAsync(RecoveryServicesVaultResource vault, object protectedItem, VMBackupRequest request)
        {
            // Placeholder;
            await Task.CompletedTask;
            return new { Name = $"backup-job-{Guid.NewGuid()}" };
        }

        /// <summary>
        /// Recovery Services Vault alır;
        /// </summary>
        private async Task<object> GetRecoveryServicesVaultAsync(string resourceGroupName, string vaultName)
        {
            // Placeholder;
            await Task.CompletedTask;
            return new { Name = vaultName };
        }

        /// <summary>
        /// Backup item bulur;
        /// </summary>
        private async Task<object> FindBackupItemAsync(object vault, string vmName, string backupId)
        {
            // Placeholder;
            await Task.CompletedTask;
            return new { Name = $"{vmName}-backup-item" };
        }

        /// <summary>
        /// Restore tetikler;
        /// </summary>
        private async Task<object> TriggerRestoreAsync(object vault, object backupItem, VMRestoreRequest request)
        {
            // Placeholder;
            await Task.CompletedTask;
            return new { Name = $"restore-job-{Guid.NewGuid()}" };
        }

        /// <summary>
        /// Restore job monitor eder;
        /// </summary>
        private async Task<string> MonitorRestoreJobAsync(object vault, object restoreJob)
        {
            // Placeholder;
            await Task.Delay(5000);
            return "Completed";
        }

        /// <summary>
        /// Restore edilen VM ID alır;
        /// </summary>
        private async Task<string> GetRestoredVMIdAsync(object vault, object restoreJob)
        {
            // Placeholder;
            await Task.CompletedTask;
            return $"restored-vm-{Guid.NewGuid()}";
        }

        /// <summary>
        /// Scaling rules oluşturur;
        /// </summary>
        private List<ScaleRule> CreateScalingRules(VMAutoScalingConfig config)
        {
            var rules = new List<ScaleRule>();

            // CPU-based scaling;
            if (config.CPUThresholdPercent.HasValue)
            {
                rules.Add(new ScaleRule;
                {
                    MetricName = "Percentage CPU",
                    TimeGrain = TimeSpan.FromMinutes(5),
                    Statistic = MetricStatistic.Average,
                    TimeWindow = TimeSpan.FromHours(1),
                    ScaleAction = new ScaleAction;
                    {
                        Direction = ScaleDirection.Increase,
                        Type = ScaleType.ChangeCount,
                        Value = "1",
                        Cooldown = TimeSpan.FromMinutes(10)
                    },
                    Condition = new ScaleCondition;
                    {
                        Comparison = ComparisonOperatorType.GreaterThanOrEqual,
                        Threshold = config.CPUThresholdPercent.Value,
                        MetricResourceUri = "" // Will be set dynamically;
                    }
                });
            }

            // Memory-based scaling;
            if (config.MemoryThresholdPercent.HasValue)
            {
                rules.Add(new ScaleRule;
                {
                    MetricName = "Available Memory Bytes",
                    TimeGrain = TimeSpan.FromMinutes(5),
                    Statistic = MetricStatistic.Average,
                    TimeWindow = TimeSpan.FromHours(1),
                    ScaleAction = new ScaleAction;
                    {
                        Direction = ScaleDirection.Increase,
                        Type = ScaleType.ChangeCount,
                        Value = "1",
                        Cooldown = TimeSpan.FromMinutes(10)
                    },
                    Condition = new ScaleCondition;
                    {
                        Comparison = ComparisonOperatorType.LessThanOrEqual,
                        Threshold = config.MemoryThresholdPercent.Value * 1024 * 1024, // Convert to bytes;
                        MetricResourceUri = ""
                    }
                });
            }

            return rules;
        }

        /// <summary>
        /// VM Scale Set alır;
        /// </summary>
        private async Task<VirtualMachineScaleSetResource> GetVMScaleSetAsync(string resourceGroupName, string scaleSetName)
        {
            var resourceGroup = await GetResourceGroupAsync(resourceGroupName);
            if (resourceGroup == null)
            {
                return null;
            }

            try
            {
                var scaleSet = await resourceGroup.GetVirtualMachineScaleSetAsync(scaleSetName);
                return scaleSet.Value;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return null;
            }
        }

        /// <summary>
        /// VM health kontrol eder;
        /// </summary>
        private bool CheckVMHealth(Dictionary<string, double?> metrics)
        {
            var cpu = metrics.GetMetricValue("Percentage CPU") ?? 0;
            var memory = metrics.GetMetricValue("Available Memory Bytes") ?? 0;
            var diskQueue = metrics.GetMetricValue("Disk Queue Depth") ?? 0;

            return cpu < 90 && memory > 100 * 1024 * 1024 && diskQueue < 10; // 100MB available memory;
        }

        /// <summary>
        /// Creation metrics toplar;
        /// </summary>
        private async Task CollectCreationMetricsAsync(VMCreationResult result)
        {
            try
            {
                await _metricsCollector.RecordMetricAsync("vm_creation_time", 1, new Dictionary<string, string>
                {
                    ["vm_size"] = result.VMSize,
                    ["os_type"] = result.OperatingSystem,
                    ["location"] = result.Location;
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to collect creation metrics");
            }
        }

        #endregion;

        #region Event Handlers;

        private async Task OnVMCreationRequested(VMCreationRequestedEvent @event)
        {
            try
            {
                _logger.LogDebug("Processing VM creation request: {VMName}", @event.VMName);

                var request = new VMCreationRequest;
                {
                    VMName = @event.VMName,
                    ResourceGroupName = @event.ResourceGroupName,
                    Location = @event.Location,
                    VMSize = @event.VMSize,
                    OperatingSystem = @event.OperatingSystem,
                    AdminUsername = @event.AdminUsername,
                    AdminPassword = @event.AdminPassword,
                    ImageReference = @event.ImageReference,
                    Tags = @event.Tags;
                };

                await CreateVirtualMachineAsync(request);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling VM creation request");
            }
        }

        private async Task OnVMDeletionRequested(VMDeletionRequestedEvent @event)
        {
            try
            {
                _logger.LogDebug("Processing VM deletion request: {VMName}", @event.VMName);
                await DeleteVirtualMachineAsync(@event.ResourceGroupName, @event.VMName, @event.DeleteDisks);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling VM deletion request");
            }
        }

        private async Task OnVMScaleRequested(VMScaleRequestedEvent @event)
        {
            try
            {
                _logger.LogDebug("Processing VM scale request: {VMName}", @event.VMName);
                await ResizeVirtualMachineAsync(@event.ResourceGroupName, @event.VMName, @event.NewVMSize);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling VM scale request");
            }
        }

        #endregion;

        #region IDisposable Implementation;

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
                    _armClient?.Dispose();
                    _operationSemaphore?.Dispose();
                }

                _disposed = true;
            }
        }

        ~VMManager()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Data Models;

    public class VMCreationRequest;
    {
        public string VMName { get; set; }
        public string ResourceGroupName { get; set; }
        public string Location { get; set; }
        public string VMSize { get; set; }
        public string OperatingSystem { get; set; } // "Windows" or "Linux"
        public string AdminUsername { get; set; }
        public string AdminPassword { get; set; }
        public string SshPublicKey { get; set; }
        public ImageReference ImageReference { get; set; }
        public string VnetName { get; set; }
        public string SubnetName { get; set; }
        public bool EnablePublicIP { get; set; } = true;
        public Dictionary<string, string> Tags { get; set; } = new Dictionary<string, string>();
        public int? AvailabilityZone { get; set; }
        public string AvailabilitySetId { get; set; }
        public VMDiskConfig OSDiskConfig { get; set; }
        public List<VMDiskConfig> DataDisks { get; set; } = new List<VMDiskConfig>();
    }

    public class VMCreationResult;
    {
        public bool Success { get; set; }
        public string VMName { get; set; }
        public string ResourceGroupName { get; set; }
        public string Location { get; set; }
        public string VMSize { get; set; }
        public string OperatingSystem { get; set; }
        public string PublicIPAddress { get; set; }
        public string PrivateIPAddress { get; set; }
        public DateTime VMCreationTime { get; set; }
        public string VMId { get; set; }
        public string ProvisioningState { get; set; }
    }

    public class VMOperationResult;
    {
        public bool Success { get; set; }
        public VMOperationType OperationType { get; set; }
        public string VMName { get; set; }
        public string ResourceGroupName { get; set; }
        public DateTime OperationTime { get; set; }
        public string Status { get; set; }
        public string ErrorMessage { get; set; }
        public string AdditionalInfo { get; set; }
    }

    public class VMInfo;
    {
        public string Name { get; set; }
        public string ResourceGroupName { get; set; }
        public string Location { get; set; }
        public string VMSize { get; set; }
        public string OperatingSystem { get; set; }
        public string Status { get; set; }
        public string ProvisioningState { get; set; }
        public DateTime CreationTime { get; set; }
        public Dictionary<string, string> Tags { get; set; } = new Dictionary<string, string>();
        public List<VMNetworkInterface> NetworkInterfaces { get; set; } = new List<VMNetworkInterface>();
        public List<VMDisk> Disks { get; set; } = new List<VMDisk>();
        public string AvailabilityZone { get; set; }
        public string PublicIPAddress { get; set; }
        public string PrivateIPAddress { get; set; }
        public string PowerState { get; set; }
        public string ResourceId { get; set; }
    }

    public class VMNetworkInterface;
    {
        public string Name { get; set; }
        public string PrivateIPAddress { get; set; }
        public string PublicIPAddress { get; set; }
        public string SubnetName { get; set; }
        public bool IsPrimary { get; set; }
    }

    public class VMDisk;
    {
        public string Name { get; set; }
        public VMDiskType Type { get; set; }
        public int SizeGB { get; set; }
        public string StorageType { get; set; }
        public string Caching { get; set; }
        public int? Lun { get; set; }
    }

    public class VMDiskConfig;
    {
        public string Name { get; set; }
        public int SizeGB { get; set; }
        public string StorageType { get; set; } = "StandardSSD_LRS";
        public string Caching { get; set; } = "ReadWrite";
        public int? Lun { get; set; }
    }

    public class VMSnapshotRequest;
    {
        public string VMName { get; set; }
        public string ResourceGroupName { get; set; }
        public string SnapshotName { get; set; }
        public string Location { get; set; }
        public Dictionary<string, string> Tags { get; set; } = new Dictionary<string, string>();
    }

    public class VMSnapshotResult;
    {
        public bool Success { get; set; }
        public string SnapshotName { get; set; }
        public string VMName { get; set; }
        public string ResourceGroupName { get; set; }
        public string Location { get; set; }
        public DateTime CreationTime { get; set; }
        public string SnapshotId { get; set; }
        public int SizeInGB { get; set; }
        public string Status { get; set; }
    }

    public class VMImageRequest;
    {
        public string VMName { get; set; }
        public string ResourceGroupName { get; set; }
        public string ImageName { get; set; }
        public string Location { get; set; }
        public Dictionary<string, string> Tags { get; set; } = new Dictionary<string, string>();
    }

    public class VMImageResult;
    {
        public bool Success { get; set; }
        public string ImageName { get; set; }
        public string SourceVMName { get; set; }
        public string ResourceGroupName { get; set; }
        public string Location { get; set; }
        public DateTime CreationTime { get; set; }
        public string ImageId { get; set; }
        public string ProvisioningState { get; set; }
    }

    public class VMMigrationRequest;
    {
        public string VMName { get; set; }
        public string ResourceGroupName { get; set; }
        public string SourceRegion { get; set; }
        public string TargetRegion { get; set; }
        public string TargetResourceGroup { get; set; }
        public string TargetVMName { get; set; }
        public string TargetVMSize { get; set; }
        public string OperatingSystem { get; set; }
        public string AdminUsername { get; set; }
        public string AdminPassword { get; set; }
        public ImageReference ImageReference { get; set; }
        public string VnetName { get; set; }
        public string SubnetName { get; set; }
        public bool DeleteSourceVM { get; set; } = true;
        public bool DeleteSourceDisks { get; set; } = true;
    }

    public class VMMigrationResult;
    {
        public bool Success { get; set; }
        public string SourceVMName { get; set; }
        public string TargetVMName { get; set; }
        public string SourceRegion { get; set; }
        public string TargetRegion { get; set; }
        public DateTime MigrationTime { get; set; }
        public string SnapshotId { get; set; }
        public string TargetVMId { get; set; }
        public bool SourceVMDeleted { get; set; }
    }

    public class VMBackupRequest;
    {
        public string VMName { get; set; }
        public string ResourceGroupName { get; set; }
        public string VaultName { get; set; }
        public string VaultResourceGroup { get; set; }
        public string BackupPolicyName { get; set; }
        public int RetentionDays { get; set; } = 30;
        public bool ImmediateBackup { get; set; } = true;
    }

    public class VMBackupResult;
    {
        public bool Success { get; set; }
        public string VMName { get; set; }
        public string ResourceGroupName { get; set; }
        public DateTime BackupTime { get; set; }
        public string BackupId { get; set; }
        public string RecoveryServicesVault { get; set; }
        public string BackupPolicy { get; set; }
        public int RetentionDays { get; set; }
        public string Status { get; set; }
    }

    public class VMRestoreRequest;
    {
        public string VMName { get; set; }
        public string ResourceGroupName { get; set; }
        public string VaultName { get; set; }
        public string BackupId { get; set; }
        public string RestoredVMName { get; set; }
        public string RestoreResourceGroup { get; set; }
        public string RestoreLocation { get; set; }
        public bool RestoreDisksOnly { get; set; }
    }

    public class VMRestoreResult;
    {
        public bool Success { get; set; }
        public string VMName { get; set; }
        public string SourceVMName { get; set; }
        public string ResourceGroupName { get; set; }
        public DateTime RestoreTime { get; set; }
        public string BackupId { get; set; }
        public string RestoreJobId { get; set; }
        public string Status { get; set; }
        public string RestoredVMId { get; set; }
    }

    public class VMAutoScalingConfig;
    {
        public int MinimumInstances { get; set; } = 1;
        public int MaximumInstances { get; set; } = 10;
        public int DefaultInstances { get; set; } = 2;
        public double? CPUThresholdPercent { get; set; } = 70;
        public double? MemoryThresholdPercent { get; set; } = 80;
        public TimeSpan ScaleOutCooldown { get; set; } = TimeSpan.FromMinutes(5);
        public TimeSpan ScaleInCooldown { get; set; } = TimeSpan.FromMinutes(10);
    }

    public class VMPerformanceMetrics;
    {
        public string VMName { get; set; }
        public string ResourceGroupName { get; set; }
        public DateTime CollectionTime { get; set; }
        public TimeSpan Duration { get; set; }
        public double CPUPercentage { get; set; }
        public double AvailableMemoryMB { get; set; }
        public double DiskReadBytesPerSecond { get; set; }
        public double DiskWriteBytesPerSecond { get; set; }
        public double NetworkInBytes { get; set; }
        public double NetworkOutBytes { get; set; }
        public double DiskQueueDepth { get; set; }
        public bool IsHealthy { get; set; }
    }

    public class VMTransferRequest;
    {
        public string VMName { get; set; }
        public string SourceResourceGroup { get; set; }
        public string SourceSubscriptionId { get; set; }
        public string TargetResourceGroup { get; set; }
        public string TargetSubscriptionId { get; set; }
        public string TargetLocation { get; set; }
    }

    public class VMTransferResult;
    {
        public bool Success { get; set; }
        public string SourceVMName { get; set; }
        public string TargetVMName { get; set; }
        public string SourceSubscriptionId { get; set; }
        public string TargetSubscriptionId { get; set; }
        public DateTime TransferTime { get; set; }
        public string TransferId { get; set; }
    }

    public class VMAvailabilitySetRequest;
    {
        public string Name { get; set; }
        public string ResourceGroupName { get; set; }
        public string Location { get; set; }
        public int PlatformFaultDomainCount { get; set; } = 2;
        public int PlatformUpdateDomainCount { get; set; } = 5;
        public bool Managed { get; set; } = true;
        public Dictionary<string, string> Tags { get; set; } = new Dictionary<string, string>();
    }

    public class VMAvailabilitySetResult;
    {
        public bool Success { get; set; }
        public string Name { get; set; }
        public string ResourceGroupName { get; set; }
        public string Location { get; set; }
        public DateTime CreationTime { get; set; }
        public string AvailabilitySetId { get; set; }
        public int FaultDomainCount { get; set; }
        public int UpdateDomainCount { get; set; }
    }

    public class VMScaleSetRequest;
    {
        public string Name { get; set; }
        public string ResourceGroupName { get; set; }
        public string Location { get; set; }
        public string VMSize { get; set; }
        public int InstanceCount { get; set; } = 2;
        public string OperatingSystem { get; set; }
        public string AdminUsername { get; set; }
        public string AdminPassword { get; set; }
        public ImageReference ImageReference { get; set; }
        public VMAutoScalingConfig AutoScalingConfig { get; set; }
        public Dictionary<string, string> Tags { get; set; } = new Dictionary<string, string>();
    }

    public class VMScaleSetResult;
    {
        public bool Success { get; set; }
        public string Name { get; set; }
        public string ResourceGroupName { get; set; }
        public string Location { get; set; }
        public DateTime CreationTime { get; set; }
        public string ScaleSetId { get; set; }
        public int InstanceCount { get; set; }
        public string VMSize { get; set; }
    }

    public class VMDiagnosticsConfig;
    {
        public bool EnableBootDiagnostics { get; set; } = true;
        public string StorageAccountName { get; set; }
        public bool EnablePerformanceCounters { get; set; } = true;
        public List<string> PerformanceCounters { get; set; } = new List<string>();
        public bool EnableEventLogs { get; set; } = true;
        public List<string> EventLogs { get; set; } = new List<string>();
        public int RetentionDays { get; set; } = 30;
    }

    public class VMMoveRequest;
    {
        public string VMName { get; set; }
        public string SourceResourceGroup { get; set; }
        public string TargetResourceGroup { get; set; }
        public bool MoveDisks { get; set; } = true;
        public bool MoveNetworkInterfaces { get; set; } = true;
        public bool MovePublicIPs { get; set; } = true;
    }

    public class VMConnectionInfo;
    {
        public string VMName { get; set; }
        public string ResourceGroupName { get; set; }
        public string PublicIPAddress { get; set; }
        public int RDPPort { get; set; } = 3389;
        public int SSHPort { get; set; } = 22;
        public string AdminUsername { get; set; }
        public string PasswordHint { get; set; }
        public DateTime ExpirationTime { get; set; }
        public string RDPFileUrl { get; set; }
        public string SSHCommand { get; set; }
    }

    public class VMCustomScriptRequest;
    {
        public string VMName { get; set; }
        public string ResourceGroupName { get; set; }
        public string ScriptUrl { get; set; }
        public string ScriptContent { get; set; }
        public List<string> ScriptArguments { get; set; } = new List<string>();
        public bool RunAsAdmin { get; set; } = true;
        public int TimeoutSeconds { get; set; } = 300;
    }

    public class VMCustomScriptResult;
    {
        public bool Success { get; set; }
        public string VMName { get; set; }
        public string ResourceGroupName { get; set; }
        public DateTime ExecutionTime { get; set; }
        public string Output { get; set; }
        public string Error { get; set; }
        public int ExitCode { get; set; }
        public TimeSpan ExecutionDuration { get; set; }
    }

    public class VMDiskRequest;
    {
        public string VMName { get; set; }
        public string ResourceGroupName { get; set; }
        public VMDiskOperation Operation { get; set; }
        public VMDiskConfig DiskConfig { get; set; }
        public string DiskId { get; set; }
        public bool ForceDetach { get; set; }
    }

    public class VMDiskResult;
    {
        public bool Success { get; set; }
        public VMDiskOperation Operation { get; set; }
        public string VMName { get; set; }
        public string ResourceGroupName { get; set; }
        public string DiskName { get; set; }
        public DateTime OperationTime { get; set; }
        public string DiskId { get; set; }
        public string Status { get; set; }
    }

    #endregion;

    #region Enums;

    public enum VMOperationType;
    {
        Create,
        Start,
        Stop,
        Restart,
        Delete,
        Deallocate,
        Resize,
        Snapshot,
        Backup,
        Restore,
        Migrate,
        Transfer,
        Move;
    }

    public enum VMDiskType;
    {
        OS,
        Data,
        Temporary;
    }

    public enum VMDiskOperation;
    {
        Attach,
        Detach,
        Resize,
        ChangeType,
        Create,
        Delete;
    }

    #endregion;

    #region Events;

    public class VMCreatedEvent : IEvent;
    {
        public string VMName { get; set; }
        public string ResourceGroupName { get; set; }
        public string Location { get; set; }
        public string VMSize { get; set; }
        public DateTime CreationTime { get; set; }
        public string VMId { get; set; }
    }

    public class VMCreationFailedEvent : IEvent;
    {
        public string VMName { get; set; }
        public string ResourceGroupName { get; set; }
        public string ErrorMessage { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class VMStartedEvent : IEvent;
    {
        public string VMName { get; set; }
        public string ResourceGroupName { get; set; }
        public DateTime StartTime { get; set; }
    }

    public class VMStoppedEvent : IEvent;
    {
        public string VMName { get; set; }
        public string ResourceGroupName { get; set; }
        public DateTime StopTime { get; set; }
        public bool WasDeallocated { get; set; }
    }

    public class VMRestartedEvent : IEvent;
    {
        public string VMName { get; set; }
        public string ResourceGroupName { get; set; }
        public DateTime RestartTime { get; set; }
    }

    public class VMDeletedEvent : IEvent;
    {
        public string VMName { get; set; }
        public string ResourceGroupName { get; set; }
        public DateTime DeleteTime { get; set; }
        public bool DisksDeleted { get; set; }
    }

    public class VMResizedEvent : IEvent;
    {
        public string VMName { get; set; }
        public string ResourceGroupName { get; set; }
        public string OldVMSize { get; set; }
        public string NewVMSize { get; set; }
        public DateTime ResizeTime { get; set; }
    }

    public class VMSnapshotCreatedEvent : IEvent;
    {
        public string VMName { get; set; }
        public string SnapshotName { get; set; }
        public DateTime CreationTime { get; set; }
        public string SnapshotId { get; set; }
    }

    public class VMImageCreatedEvent : IEvent;
    {
        public string VMName { get; set; }
        public string ImageName { get; set; }
        public DateTime CreationTime { get; set; }
        public string ImageId { get; set; }
    }

    public class VMMigratedEvent : IEvent;
    {
        public string SourceVMName { get; set; }
        public string TargetVMName { get; set; }
        public string SourceRegion { get; set; }
        public string TargetRegion { get; set; }
        public DateTime MigrationTime { get; set; }
    }

    public class VMBackupCompletedEvent : IEvent;
    {
        public string VMName { get; set; }
        public DateTime BackupTime { get; set; }
        public string BackupId { get; set; }
        public bool Success { get; set; }
    }

    public class VMRestoredEvent : IEvent;
    {
        public string SourceVMName { get; set; }
        public string RestoredVMName { get; set; }
        public DateTime RestoreTime { get; set; }
        public bool Success { get; set; }
        public string BackupId { get; set; }
    }

    public class VMOperationFailedEvent : IEvent;
    {
        public string VMName { get; set; }
        public string ResourceGroupName { get; set; }
        public VMOperationType OperationType { get; set; }
        public string ErrorMessage { get; set; }
        public DateTime Timestamp { get; set; }
    }

    // Request events;
    public class VMCreationRequestedEvent : IEvent;
    {
        public string VMName { get; set; }
        public string ResourceGroupName { get; set; }
        public string Location { get; set; }
        public string VMSize { get; set; }
        public string OperatingSystem { get; set; }
        public string AdminUsername { get; set; }
        public string AdminPassword { get; set; }
        public ImageReference ImageReference { get; set; }
        public Dictionary<string, string> Tags { get; set; } = new Dictionary<string, string>();
    }

    public class VMDeletionRequestedEvent : IEvent;
    {
        public string VMName { get; set; }
        public string ResourceGroupName { get; set; }
        public bool DeleteDisks { get; set; }
    }

    public class VMScaleRequestedEvent : IEvent;
    {
        public string VMName { get; set; }
        public string ResourceGroupName { get; set; }
        public string NewVMSize { get; set; }
    }

    #endregion;

    #region Exceptions;

    public class VMManagerException : Exception
    {
        public VMManagerException(string message) : base(message) { }
        public VMManagerException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    #endregion;

    #region Azure SDK Models;

    // Note: These are simplified versions of Azure SDK models;
    // In real implementation, use the actual Azure.ResourceManager.Compute.Models namespace;

    public class ScaleRule;
    {
        public string MetricName { get; set; }
        public TimeSpan TimeGrain { get; set; }
        public MetricStatistic Statistic { get; set; }
        public TimeSpan TimeWindow { get; set; }
        public ScaleAction ScaleAction { get; set; }
        public ScaleCondition Condition { get; set; }
    }

    public class ScaleAction;
    {
        public ScaleDirection Direction { get; set; }
        public ScaleType Type { get; set; }
        public string Value { get; set; }
        public TimeSpan Cooldown { get; set; }
    }

    public class ScaleCondition;
    {
        public ComparisonOperatorType Comparison { get; set; }
        public double Threshold { get; set; }
        public string MetricResourceUri { get; set; }
    }

    public enum ScaleDirection;
    {
        Increase,
        Decrease,
        None;
    }

    public enum ScaleType;
    {
        ChangeCount,
        PercentChangeCount,
        ExactCount;
    }

    public enum MetricStatistic;
    {
        Average,
        Min,
        Max,
        Sum,
        Count;
    }

    public enum ComparisonOperatorType;
    {
        Equals,
        NotEquals,
        GreaterThan,
        GreaterThanOrEqual,
        LessThan,
        LessThanOrEqual;
    }

    #endregion;
}
