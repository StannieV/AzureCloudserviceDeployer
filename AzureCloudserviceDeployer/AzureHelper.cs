﻿using log4net;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using Microsoft.WindowsAzure;
using Microsoft.WindowsAzure.Management.Compute;
using Microsoft.WindowsAzure.Management.Compute.Models;
using Microsoft.WindowsAzure.Management.Storage;
using Microsoft.WindowsAzure.Management.Storage.Models;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Auth;
using Microsoft.WindowsAzure.Subscriptions;
using Microsoft.WindowsAzure.Subscriptions.Models;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Runtime.Caching;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AzureCloudserviceDeployer
{
    public enum UpgradePreference
    {
        [Description("Delete/create new deployment")]
        DeleteAndCreateDeployment,
        [Description("Delete/create new deployment (stopped)")]
        DeleteAndCreateDeploymentInitiallyStopped,
        [Description("Upgrade (or create) respecting update domains")]
        UpgradeWithUpdateDomains,
        [Description("Upgrade (or create) with all update domains simultaneously")]
        UpgradeSimultaneously
    }

    public class AzureHelper
    {
        private static ILog Logger = LogManager.GetLogger(typeof(AzureHelper));

        private static Dictionary<string, AuthenticationContext> _authenticationContexts = new Dictionary<string, AuthenticationContext>();

        #region Cache

        private static ObjectCache _cache = new MemoryCache("AzureHelperCache");

        public static void ClearCache()
        {
            lock (_cache)
            {
                foreach (var key in _cache.Select(s => s.Key).ToArray())
                {
                    _cache.Remove(key);
                }
            }
        }

        private static async Task<T> GetCachedAsync<T>(string source, string key, Func<Task<T>> func)
        {
            var val = _cache.Get(key);
            if (val == null)
            {
                Logger.Debug("[" + source + "] " + "Data not in cache, fetching and adding (key=" + key + ")");
                val = await func().ConfigureAwait(true);
                _cache.Set(key, val, DateTime.UtcNow.AddHours(1));
            }
            else
            {
                Logger.Debug("[" + source + "] " + "Fetched data from cache (key=" + key + ")");
            }
            return (T)val;
        }

        #endregion

        public static AuthenticationResult GetAuthentication(string source, string tenantId = null, bool forceRefresh = false)
        {
            var login = ConfigurationManager.AppSettings["login"];
            tenantId = tenantId ?? ConfigurationManager.AppSettings["tenantId"]; // Use the tenant of the selected subscription, or the default
            var apiEndpoint = ConfigurationManager.AppSettings["apiEndpoint"];
            var clientId = ConfigurationManager.AppSettings["clientId"];
            var redirectUri = ConfigurationManager.AppSettings["redirectUri"];

            var prompt = forceRefresh ? PromptBehavior.RefreshSession : PromptBehavior.Auto;

            var context = GetAuthenticationContext(string.Format(login, tenantId), forceRefresh);
            var result = context.AcquireToken(apiEndpoint, clientId, new Uri(redirectUri), prompt);
            Logger.Debug("[" + source + "] " + "Token for authority '" + context.Authority + "' valid until: " + result.ExpiresOn);
            return result;
        }

        private static AuthenticationContext GetAuthenticationContext(string authority, bool forceRefresh)
        {
            lock (_authenticationContexts)
            {
                if (forceRefresh)
                    _authenticationContexts.Clear();

                if (_authenticationContexts.ContainsKey(authority))
                    return _authenticationContexts[authority];
                _authenticationContexts.Add(authority, new AuthenticationContext(authority));
                return _authenticationContexts[authority];
            }
        }

        public static TokenCloudCredentials GetCredentials(string source, SubscriptionListOperationResponse.Subscription subscription = null)
        {
            TokenCloudCredentials token = null;
            if (subscription == null)
                token = new TokenCloudCredentials(GetAuthentication(source).AccessToken);
            else
                token = new TokenCloudCredentials(subscription.SubscriptionId, GetAuthentication(source, subscription.ActiveDirectoryTenantId).AccessToken);
            return token;
        }

        public static async Task<SubscriptionListOperationResponse> GetSubscriptionsAsync(string source, string userAccount)
        {
            return await GetCachedAsync(source, "Subscriptions_" + userAccount, async () =>
            {
                var subscriptionClient = new SubscriptionClient(GetCredentials(source));
                Logger.Info("[" + source + "] " + "Retrieving subscriptions...");
                return await subscriptionClient.Subscriptions.ListAsync();
            });
        }


        public static async Task<HostedServiceListResponse> GetCloudservicesAsync(string source, SubscriptionListOperationResponse.Subscription subscription)
        {
            return await GetCachedAsync(source, "CloudServices_" + subscription.SubscriptionId, async () =>
            {
                var computeClient = new ComputeManagementClient(GetCredentials(source, subscription));
                Logger.Info("[" + source + "] " + "Retrieving hosted services...");
                return await computeClient.HostedServices.ListAsync();
            });
        }

        public static async Task<StorageAccountListResponse> GetStorageAccountsAsync(string source, SubscriptionListOperationResponse.Subscription subscription)
        {
            return await GetCachedAsync(source, "StorageAccounts_" + subscription.SubscriptionId, async () =>
            {
                var storageClient = new StorageManagementClient(GetCredentials(source, subscription));
                Logger.Info("[" + source + "] " + "Retrieving storage accounts...");
                return await storageClient.StorageAccounts.ListAsync();
            });
        }

        public static async Task DeployAsync(string source, SubscriptionListOperationResponse.Subscription subscription, HostedServiceListResponse.HostedService service,
            StorageAccount storage, DeploymentSlot slot, UpgradePreference upgradePreference, string pathToCspkg,
            string pathToCscfg, string pathToDiagExtensionConfig, StorageAccount diagStorage, string deploymentLabel,
            bool cleanupUnusedExtensions, bool forceWhenUpgrading)
        {
            Logger.InfoFormat("[" + source + "] " + "Preparing for deployment of {0}...", service.ServiceName);
            var credentials = GetCredentials(source, subscription);
            var computeClient = new ComputeManagementClient(credentials);
            var storageClient = new StorageManagementClient(credentials);

            // Load csdef
            var csCfg = File.ReadAllText(pathToCscfg);

            // Load diag config
            var diagConfig = pathToDiagExtensionConfig != null ? File.ReadAllText(pathToDiagExtensionConfig) : null;

            // Upgrade cspkg to storage
            Logger.InfoFormat("[" + source + "] " + "Fetching key for storage account {0}...", storage.Name);
            var keys = await storageClient.StorageAccounts.GetKeysAsync(storage.Name);
            var csa = new CloudStorageAccount(new StorageCredentials(storage.Name, keys.PrimaryKey), true);
            var blobClient = csa.CreateCloudBlobClient();
            var containerRef = blobClient.GetContainerReference("acd-deployments");
            if (!await containerRef.ExistsAsync())
            {
                Logger.InfoFormat("[" + source + "] " + "Creating container {0}...", containerRef.Name);
                await containerRef.CreateIfNotExistsAsync();
            }
            var filename = service.ServiceName + "-" + slot.ToString() + ".cspkg";

            var blobRef = containerRef.GetBlockBlobReference(filename);
            Logger.InfoFormat("[" + source + "] " + "Uploading package to {0}...", blobRef.Uri);
            await blobRef.UploadFromFileAsync(pathToCspkg, FileMode.Open);

            // Private diagnostics config
            string diagStorageName = null, diagStorageKey = null;
            if (diagStorage == null)
            {
                Logger.InfoFormat("[" + source + "] " + "Using storage account credentials from .cscfg for diagnostics...");
                Regex regex = new Regex("Diagnostics.ConnectionString.*?DefaultEndpointsProtocol.*?AccountName=(.+?);.*?AccountKey=([a-zA-Z0-9/+=]+)", RegexOptions.Singleline);
                Match m = regex.Match(csCfg);
                if (m.Success)
                {
                    diagStorageName = m.Groups[1].Value;
                    diagStorageKey = m.Groups[2].Value;
                    Logger.InfoFormat("[" + source + "] " + "Extracted storage account {0} from .cscfg for diagnostics...", diagStorageName);
                }
                else
                {
                    throw new ApplicationException("Couldn't extract Diagnostics ConnectionString from .cscfg");
                }
            }
            else
            {
                Logger.InfoFormat("[" + source + "] " + "Using storage account {0} for diagnostics...", diagStorage.Name);
                if (storage.Name == diagStorage.Name)
                {
                    diagStorageName = diagStorage.Name;
                    diagStorageKey = keys.PrimaryKey;
                }
                else
                {
                    Logger.InfoFormat("[" + source + "] " + "Fetching keys for storage account {0}...", diagStorage.Name);
                    var diagKeys = await storageClient.StorageAccounts.GetKeysAsync(diagStorage.Name);
                    diagStorageName = diagStorage.Name;
                    diagStorageKey = diagKeys.PrimaryKey;
                }
            }
            var privateDiagConfig = string.Format(@"<PrivateConfig xmlns=""http://schemas.microsoft.com/ServiceHosting/2010/10/DiagnosticsConfiguration"">
    <StorageAccount name=""{0}"" key=""{1}"" />
  </PrivateConfig>", diagStorageName, diagStorageKey);

            // Get diagnostics extension template
            Logger.InfoFormat("[" + source + "] " + "Retrieving available extensions...");
            var availableExtensions = await computeClient.HostedServices.ListAvailableExtensionsAsync();
            var availableDiagnosticsExtension = availableExtensions.Where(d => d.Type == "PaaSDiagnostics").First();

            // Cleanup of existing extensions
            if (cleanupUnusedExtensions)
            {
                await CleanupExistingExtensions(source, subscription, service, computeClient, availableDiagnosticsExtension);
            }
            else
            {
                Logger.InfoFormat("[" + source + "] " + "Skip cleaning unused extensions as configured");
            }

            // Extensions config
            var extensionConfiguration = new ExtensionConfiguration();

            // Create the extension with new configuration
            if (diagConfig != null)
            {
                var diagnosticsId = "acd-diagnostics-" + Guid.NewGuid().ToString("N");
                Logger.InfoFormat("[" + source + "] " + "Adding new diagnostics extension {0}...", diagnosticsId);
                var createExtensionOperation = await computeClient.HostedServices.BeginAddingExtensionAsync(service.ServiceName, new HostedServiceAddExtensionParameters()
                {
                    ProviderNamespace = availableDiagnosticsExtension.ProviderNameSpace,
                    Type = availableDiagnosticsExtension.Type,
                    Id = diagnosticsId,
                    Version = availableDiagnosticsExtension.Version,
                    PublicConfiguration = diagConfig,
                    PrivateConfiguration = privateDiagConfig
                });
                await WaitForOperationAsync(source, subscription, computeClient, createExtensionOperation.RequestId);

                // Extension configuration
                extensionConfiguration.AllRoles.Add(new ExtensionConfiguration.Extension
                {
                    Id = diagnosticsId
                });
            }

            // Create deployment parameters
            var deployParams = new DeploymentCreateParameters
            {
                StartDeployment = true,
                Name = Guid.NewGuid().ToString("N"),
                Configuration = csCfg,
                PackageUri = blobRef.Uri,
                Label = deploymentLabel ?? DateTime.UtcNow.ToString("u") + " " + Environment.UserName,
                ExtensionConfiguration = extensionConfiguration
            };
            var upgradeParams = new DeploymentUpgradeParameters
            {
                Configuration = csCfg,
                PackageUri = blobRef.Uri,
                Label = deploymentLabel ?? DateTime.UtcNow.ToString("u") + " " + Environment.UserName,
                ExtensionConfiguration = extensionConfiguration,
                Mode = upgradePreference == UpgradePreference.UpgradeSimultaneously ? DeploymentUpgradeMode.Simultaneous : DeploymentUpgradeMode.Auto,
                Force = forceWhenUpgrading
            };
            Logger.InfoFormat("[" + source + "] " + "Label for deployment: {0}", deployParams.Label);

            switch (upgradePreference)
            {
                case UpgradePreference.DeleteAndCreateDeploymentInitiallyStopped:
                case UpgradePreference.DeleteAndCreateDeployment:
                    {
                        // In the case of initially stopped, set StartDeployment to false (we default to 'true' above)
                        if (upgradePreference == UpgradePreference.DeleteAndCreateDeploymentInitiallyStopped)
                            deployParams.StartDeployment = false;

                        // Is there a deployment in this slot?
                        Logger.InfoFormat("[" + source + "] " + "Fetching detailed service information...");
                        var detailedService = await computeClient.HostedServices.GetDetailedAsync(service.ServiceName);
                        var currentDeployment = detailedService.Deployments.Where(s => s.DeploymentSlot == slot).FirstOrDefault();
                        if (currentDeployment != null)
                        {
                            // Yes, there is. Save the deployment name for the recreate. This is to increase compatibility with
                            // cloud service monitoring tools.
                            deployParams.Name = currentDeployment.Name;

                            // Delete it.
                            Logger.InfoFormat("[" + source + "] " + "Deployment in {0} slot exists, deleting...", slot);
                            var deleteOperation = await computeClient.Deployments.BeginDeletingBySlotAsync(service.ServiceName, slot);
                            await WaitForOperationAsync(source, subscription, computeClient, deleteOperation.RequestId);
                        }

                        // Create a new deployment in this slot
                        Logger.InfoFormat("[" + source + "] " + "Creating deployment in {0} slot...", slot);
                        var createOperation = await computeClient.Deployments.BeginCreatingAsync(service.ServiceName, slot, deployParams);
                        await WaitForOperationAsync(source, subscription, computeClient, createOperation.RequestId);
                    }

                    break;
                case UpgradePreference.UpgradeWithUpdateDomains:
                case UpgradePreference.UpgradeSimultaneously:
                    {
                        // Is there a deployment in this slot?
                        Logger.InfoFormat("[" + source + "] " + "Fetching detailed service information...");
                        var detailedService = await computeClient.HostedServices.GetDetailedAsync(service.ServiceName);
                        var currentDeployment = detailedService.Deployments.Where(s => s.DeploymentSlot == slot).FirstOrDefault();
                        if (currentDeployment != null)
                        {
                            // Yes, there is. Upgrade it.
                            Logger.InfoFormat("[" + source + "] " + "Deployment in {0} slot exists, upgrading...", slot);
                            var upgradeOperation = await computeClient.Deployments.BeginUpgradingBySlotAsync(service.ServiceName, slot, upgradeParams);
                            await WaitForOperationAsync(source, subscription, computeClient, upgradeOperation.RequestId);
                        }
                        else
                        {
                            // No, there isn't. Create.
                            Logger.InfoFormat("[" + source + "] " + "No deployment in {0} slot yet, creating...", slot);
                            var createOperation = await computeClient.Deployments.BeginCreatingAsync(service.ServiceName, slot, deployParams);
                            await WaitForOperationAsync(source, subscription, computeClient, createOperation.RequestId);
                        }
                    }

                    break;
            }

            Logger.InfoFormat("[" + source + "] " + "Deployment succesful");
        }

        private static async Task CleanupExistingExtensions(string source, SubscriptionListOperationResponse.Subscription subscription, HostedServiceListResponse.HostedService service, ComputeManagementClient computeClient, ExtensionImage availableDiagnosticsExtension)
        {

            // Get whatever is currently on Production
            Logger.InfoFormat("[" + source + "] " + "Retrieving current deployment details and currently used extensions...");
            DeploymentGetResponse currentProductionSlotDetails = null, currentStagingSlotDetails = null;
            var details = computeClient.HostedServices.GetDetailed(service.ServiceName);
            if (details.Deployments.Where(s => s.DeploymentSlot == DeploymentSlot.Production).Any())
                currentProductionSlotDetails = await computeClient.Deployments.GetBySlotAsync(service.ServiceName, DeploymentSlot.Production);
            if (details.Deployments.Where(s => s.DeploymentSlot == DeploymentSlot.Staging).Any())
                currentStagingSlotDetails = await computeClient.Deployments.GetBySlotAsync(service.ServiceName, DeploymentSlot.Staging);

            // Compile a list of diagnostic id's still in use
            List<string> diagIdsInUse = new List<string>();
            if (currentProductionSlotDetails != null && currentProductionSlotDetails.ExtensionConfiguration != null)
            {
                diagIdsInUse.AddRange(currentProductionSlotDetails.ExtensionConfiguration.AllRoles.Select(s => s.Id));
                diagIdsInUse.AddRange(currentProductionSlotDetails.ExtensionConfiguration.NamedRoles.SelectMany(s => s.Extensions).Select(s => s.Id));
            }
            if (currentStagingSlotDetails != null && currentStagingSlotDetails.ExtensionConfiguration != null)
            {
                diagIdsInUse.AddRange(currentStagingSlotDetails.ExtensionConfiguration.AllRoles.Select(s => s.Id));
                diagIdsInUse.AddRange(currentStagingSlotDetails.ExtensionConfiguration.NamedRoles.SelectMany(s => s.Extensions).Select(s => s.Id));
            }

            // Check if diag extension already exists for this service. If so, delete it.
            Logger.InfoFormat("[" + source + "] " + "Retrieving all extensions for {0}...", service.ServiceName);
            var currentExtensions = await computeClient.HostedServices.ListExtensionsAsync(service.ServiceName);
            foreach (var currentDiagExtension in currentExtensions.Where(d => d.Type == availableDiagnosticsExtension.Type))
            {
                if (diagIdsInUse.Contains(currentDiagExtension.Id))
                {
                    Logger.InfoFormat("[" + source + "] " + "Skip deleting diagnostics extension {0}, because it is in use by the deployment", currentDiagExtension.Id);
                }
                else
                {
                    Logger.InfoFormat("[" + source + "] " + "Deleting unused diagnostics extension named {0}...", currentDiagExtension.Id);
                    try
                    {
                        var deleteOperation = await computeClient.HostedServices.DeleteExtensionAsync(service.ServiceName, currentDiagExtension.Id);
                        await WaitForOperationAsync(source, subscription, computeClient, deleteOperation.RequestId);
                    }
                    catch (Exception)
                    {
                        Logger.ErrorFormat("[" + source + "] " + "Couldn't delete extension {0}, might be in use...", currentDiagExtension.Id);
                    }
                }
            }
        }

        public static async Task DownloadDeploymentAsync(string source, SubscriptionListOperationResponse.Subscription subscription, HostedServiceListResponse.HostedService service, DeploymentSlot slot, StorageAccount temporaryStorage, string downloadToPath, bool warnInsteadOfThrow = false)
        {
            Logger.InfoFormat("[" + source + "] " + "Preparing to download {0}/{1}...", service.ServiceName, slot);
            var computeClient = new ComputeManagementClient(GetCredentials(source, subscription));
            var storageClient = new StorageManagementClient(GetCredentials(source, subscription));

            Logger.InfoFormat("[" + source + "] " + "Checking existing deployment in {0} slot...", slot);
            var detailedService = await computeClient.HostedServices.GetDetailedAsync(service.ServiceName);
            if (!detailedService.Deployments.Where(s => s.DeploymentSlot == slot).Any())
            {
                if (warnInsteadOfThrow)
                {
                    Logger.WarnFormat("[" + source + "] " + "No deployment found in selected slot, not downloading...");
                    return;
                }
                else
                {
                    throw new ApplicationException("No deployment found in selected slot");
                }
            }

            Logger.InfoFormat("[" + source + "] " + "Getting current diagnostics extension data (if any)...");
            var detailedSlot = await computeClient.Deployments.GetBySlotAsync(service.ServiceName, slot);
            string pubConfig = null;
            if (detailedSlot.ExtensionConfiguration != null)
            {
                foreach (var ext in detailedSlot.ExtensionConfiguration.AllRoles.Union(detailedSlot.ExtensionConfiguration.NamedRoles.SelectMany(s => s.Extensions)))
                {
                    Logger.InfoFormat("[" + source + "] " + "Fetching extension with id {0}...", ext.Id);
                    var extension = await computeClient.HostedServices.GetExtensionAsync(service.ServiceName, ext.Id);
                    if (extension.Type == "PaaSDiagnostics")
                    {
                        Logger.InfoFormat("[" + source + "] " + "Found extension of type {0}, which should be the diagnostics PubConfig...", extension.Type);
                        pubConfig = extension.PublicConfiguration;
                    }
                }
            }
            if (pubConfig == null)
            {
                Logger.InfoFormat("[" + source + "] " + "No PaaSDiagnostics extension found...");
            }

            Logger.InfoFormat("[" + source + "] " + "Preparing temp storage account {0}...", temporaryStorage.Name);
            var keys = await storageClient.StorageAccounts.GetKeysAsync(temporaryStorage.Name);
            var csa = new CloudStorageAccount(new StorageCredentials(temporaryStorage.Name, keys.PrimaryKey), true);
            var client = csa.CreateCloudBlobClient();
            var container = client.GetContainerReference("acd-temp-" + Guid.NewGuid().ToString("N").ToLower());

            Logger.InfoFormat("[" + source + "] " + "Creating temp container {0}...", container.Name);
            await container.CreateIfNotExistsAsync();

            Logger.InfoFormat("[" + source + "] " + "Downloading package to storage account {0}...", temporaryStorage.Name);
            var getPackageOperation = await computeClient.Deployments.BeginGettingPackageBySlotAsync(service.ServiceName, slot, new DeploymentGetPackageParameters
            {
                ContainerUri = container.Uri
            });
            await WaitForOperationAsync(source, subscription, computeClient, getPackageOperation.RequestId);

            Logger.InfoFormat("[" + source + "] " + "Downloading from storage to {0}...", downloadToPath);
            var result = await container.ListBlobsSegmentedAsync(null);
            var prefix = string.Format("{0} {1} {2}", DateTime.Now.ToString("yyyy-MM-dd HH-mm"), service.ServiceName, slot);
            foreach (var item in result.Results)
            {
                var cloudblob = client.GetBlobReferenceFromServer(item.StorageUri);
                string filename = prefix + " " + cloudblob.Name;
                Logger.Info("[" + source + "] " + "Downloading: " + filename);
                await cloudblob.DownloadToFileAsync(Path.Combine(downloadToPath, filename), FileMode.Create);
            }

            if (pubConfig != null)
            {
                Logger.InfoFormat("[" + source + "] " + "Writing PubConfig to {0}...", prefix + ".PubConfig.xml");
                string pubconfigFilename = Path.Combine(downloadToPath, prefix + ".PubConfig.xml");
                File.WriteAllText(pubconfigFilename, pubConfig);
            }

            Logger.Info("[" + source + "] " + "Cleaning up temp storage...");
            await container.DeleteAsync();

            Logger.Info("[" + source + "] " + "Package download succesful");
        }

        private static async Task WaitForOperationAsync(string source, SubscriptionListOperationResponse.Subscription subscription, ComputeManagementClient client, string requestId)
        {
            var statusResponse = await client.GetOperationStatusAsync(requestId);
            while (statusResponse.Status == OperationStatus.InProgress)
            {
                Logger.InfoFormat("Waiting for operation to finish...");
                await Task.Delay(5000);
                RefreshCredentials(source, subscription, client.Credentials);
                statusResponse = await client.GetOperationStatusAsync(requestId);
            }
            if (statusResponse.Status == OperationStatus.Failed)
            {
                throw new CloudException("Operation failed with code " + statusResponse.Error.Code + ": " + statusResponse.Error.Message);
            }
        }

        private static void RefreshCredentials(string source, SubscriptionListOperationResponse.Subscription subscription, SubscriptionCloudCredentials credentials)
        {
            var tokenCloudCredentials = credentials as TokenCloudCredentials;
            if (tokenCloudCredentials != null)
            {
                var newCredentials = GetCredentials(source, subscription);
                tokenCloudCredentials.Token = newCredentials.Token;
            }
        }
    }
}
