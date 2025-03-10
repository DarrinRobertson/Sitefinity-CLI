﻿using EnvDTE;
using McMaster.Extensions.CommandLineUtils;
using Microsoft.Extensions.Logging;
using Sitefinity_CLI.Commands.Validators;
using Sitefinity_CLI.Exceptions;
using Sitefinity_CLI.PackageManagement;
using Sitefinity_CLI.VisualStudio;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;

namespace Sitefinity_CLI.Commands
{
    [HelpOption]
    [Command(Constants.UpgradeCommandName, Description = "Upgrade Sitefinity project/s to a newer version of Sitefinity.")]
    [AdminRightsValidator]
    internal class UpgradeCommand
    {
        [Argument(0, Description = Constants.ProjectOrSolutionPathOptionDescription)]
        [Required(ErrorMessage = "You must specify a path to a solution file.")]
        public string SolutionPath { get; set; }

        [Argument(1, Description = Constants.VersionToOptionDescription)]
        [Required(ErrorMessage = "You must specify the Sitefinity version to upgrade to.")]
        [UpgradeVersionValidator]
        public string Version { get; set; }

        [Option(Constants.SkipPrompts, Description = Constants.SkipPromptsDescription)]
        public bool SkipPrompts { get; set; }

        [Option(Constants.AcceptLicense, Description = Constants.AcceptLicenseOptionDescription)]
        public bool AcceptLicense { get; set; }

        [Option(Constants.PackageSources, Description = Constants.PackageSourcesDescription)]
        public string PackageSources { get; set; }

        public UpgradeCommand(
            IPromptService promptService,
            ISitefinityPackageManager sitefinityPackageManager,
            ICsProjectFileEditor csProjectFileEditor,
            ILogger<UpgradeCommand> logger,
            IProjectConfigFileEditor projectConfigFileEditor,
            IVisualStudioWorker visualStudioWorker)
        {
            this.promptService = promptService;
            this.sitefinityPackageManager = sitefinityPackageManager;
            this.csProjectFileEditor = csProjectFileEditor;
            this.logger = logger;
            this.visualStudioWorker = visualStudioWorker;
            this.projectConfigFileEditor = projectConfigFileEditor;
            this.processedPackagesPerProjectCache = new Dictionary<string, HashSet<string>>();
        }

        protected async Task<int> OnExecuteAsync(CommandLineApplication app)
        {
            try
            {
                await this.ExecuteUpgrade();

                return 0;
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex.Message);

                return 1;
            }
            finally
            {
                this.visualStudioWorker.Dispose();
            }
        }

        protected virtual async Task ExecuteUpgrade()
        {
            if (!File.Exists(this.SolutionPath))
            {
                throw new FileNotFoundException(string.Format(Constants.FileNotFoundMessage, this.SolutionPath));
            }

            if (!this.SkipPrompts && !this.promptService.PromptYesNo(Constants.UpgradeWarning))
            {
                this.logger.LogInformation(Constants.UpgradeWasCanceled);
                return;
            }

            this.logger.LogInformation("Searching the provided project/s for Sitefinity references...");

            var sitefinityProjectFilePaths = this.GetProjectsPathsFromSolution(this.SolutionPath, true);
            var projectsWithouthSitefinityPaths = this.GetProjectsPathsFromSolution(this.SolutionPath).Except(sitefinityProjectFilePaths);

            if (!sitefinityProjectFilePaths.Any())
            {
                Utils.WriteLine(Constants.NoProjectsFoundToUpgradeWarningMessage, ConsoleColor.Yellow);
                return;
            }

            Dictionary<string, string> configsWithoutSitefinity = this.GetConfigsForProjectsWithoutSitefinity(projectsWithouthSitefinityPaths);

            this.logger.LogInformation(string.Format(Constants.NumberOfProjectsWithSitefinityReferencesFoundSuccessMessage, sitefinityProjectFilePaths.Count()));
            this.logger.LogInformation(string.Format("Collecting Sitefinity NuGet package tree for version \"{0}\"...", this.Version));
            var packageSources = this.GetNugetPackageSources();

            NuGetPackage newSitefinityPackage = await this.sitefinityPackageManager.GetSitefinityPackageTree(this.Version, packageSources);

            if (newSitefinityPackage == null)
            {
                this.logger.LogError(string.Format(Constants.VersionNotFound, this.Version));
                return;
            }

            this.sitefinityPackageManager.Restore(this.SolutionPath);
            this.sitefinityPackageManager.SetTargetFramework(sitefinityProjectFilePaths, this.Version);
            this.sitefinityPackageManager.Install(newSitefinityPackage.Id, newSitefinityPackage.Version, this.SolutionPath, packageSources);

            if (!this.AcceptLicense)
            {
                var licenseContent = await GetLicenseContent(newSitefinityPackage);
                var licensePromptMessage = $"{Environment.NewLine}{licenseContent}{Environment.NewLine}{Constants.AcceptLicenseNotification}";
                var hasUserAcceptedEULA = this.promptService.PromptYesNo(licensePromptMessage, false);

                if (!hasUserAcceptedEULA)
                {
                    this.logger.LogInformation(Constants.UpgradeWasCanceled);
                    return;
                }
            }

            await this.GenerateUpgradeConfig(sitefinityProjectFilePaths, newSitefinityPackage, packageSources);

            var updaterPath = Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, Constants.SitefinityUpgradePowershellFolderName, "Updater.ps1");
            this.visualStudioWorker.Initialize(this.SolutionPath);
            this.visualStudioWorker.ExecuteScript(updaterPath);
            this.EnsureOperationSuccess();

            this.visualStudioWorker.Dispose();

            // revert config changes caused by nuget for projects we are not upgrading
            this.RestoreConfigValuesForNoSfProjects(configsWithoutSitefinity);

            this.SyncProjectReferencesWithPackages(sitefinityProjectFilePaths, Path.GetDirectoryName(this.SolutionPath));

            this.logger.LogInformation(string.Format(Constants.UpgradeSuccessMessage, this.SolutionPath, this.Version));
        }

        private IEnumerable<string> GetNugetPackageSources()
        {
            if (string.IsNullOrEmpty(this.PackageSources))
            {
                return this.sitefinityPackageManager.DefaultPackageSource;
            }

            var packageSources = this.PackageSources.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(ps => ps.Trim());
            return packageSources;
        }

        private void RestoreConfigValuesForNoSfProjects(Dictionary<string, string> configsWithoutSitefinity)
        {
            foreach (var item in configsWithoutSitefinity)
            {
                File.WriteAllText(item.Key, item.Value);
            }
        }

        private Dictionary<string, string> GetConfigsForProjectsWithoutSitefinity(IEnumerable<string> projectsWithouthSfreferencePaths)
        {
            var configsWithoutSitefinity = new Dictionary<string, string>();
            foreach (var project in projectsWithouthSfreferencePaths)
            {
                var porjectConfigPath = this.projectConfigFileEditor.GetProjectConfigPath(project);
                if (!string.IsNullOrEmpty(porjectConfigPath))
                {
                    var configValue = File.ReadAllText(porjectConfigPath);
                    configsWithoutSitefinity.Add(porjectConfigPath, configValue);
                }
            }

            return configsWithoutSitefinity;
        }

        private async Task<string> GetLicenseContent(NuGetPackage newSitefinityPackage)
        {
            var pathToPackagesFolder = Path.Combine(Path.GetDirectoryName(this.SolutionPath), Constants.PackagesFolderName);
            var pathToTheLicense = Path.Combine(pathToPackagesFolder, $"{newSitefinityPackage.Id}.{newSitefinityPackage.Version}", Constants.LicenseAgreementsFolderName, "License.txt");
            var licenseContent = await File.ReadAllTextAsync(pathToTheLicense);

            return licenseContent;
        }

        private Version DetectSitefinityVersion(string sitefinityProjectPath)
        {
            CsProjectFileReference sitefinityReference = this.csProjectFileEditor.GetReferences(sitefinityProjectPath)
                .FirstOrDefault(r => this.IsSitefinityReference(r));

            if (sitefinityReference != null)
            {
                string versionPattern = @"Version=(.*?),";
                Match versionMatch = Regex.Match(sitefinityReference.Include, versionPattern);

                if (versionMatch.Success)
                {
                    // 13.2.7500.76032 .ToString(3) will return 13.2.7500 - we need the version without the revision
                    var sitefinityVersionWithoutRevision = System.Version.Parse(versionMatch.Groups[1].Value).ToString(3);

                    return System.Version.Parse(sitefinityVersionWithoutRevision);
                }
            }

            return null;
        }

        private bool ContainsSitefinityRefKeyword(CsProjectFileReference projectReference)
        {
            return (projectReference.Include.Contains(Constants.TelerikSitefinityReferenceKeyWords) || projectReference.Include.Contains(Constants.ProgressSitefinityReferenceKeyWords)) && 
                !projectReference.Include.Contains(Constants.ProgressSitefinityRendererReferenceKeyWords);
        }

        private void EnsureOperationSuccess()
        {
            this.logger.LogInformation("Waiting for operation to complete...");

            var resultFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Constants.SitefinityUpgradePowershellFolderName, "result.log");
            var progressFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Constants.SitefinityUpgradePowershellFolderName, "progress.log");
            File.Delete(resultFile);
            int waitStep = 500;
            int iterations = 0;
            var lastProgressUpdate = string.Empty;
            while (true)
            {
                if (!File.Exists(resultFile))
                {
                    if (iterations % 2 == 0 && File.Exists(progressFile))
                    {
                        try
                        {
                            var progressInfo = this.ReadAllTextFromFile(progressFile).Replace("\r\n", string.Empty);
                            if (lastProgressUpdate != progressInfo)
                            {
                                lastProgressUpdate = progressInfo;
                                this.logger.LogInformation(progressInfo);
                            }
                        }
                        catch { }
                    }

                    iterations++;

                    // Last operation is still not executed and the file is not created.
                    System.Threading.Thread.Sleep(waitStep);
                    continue;
                }

                System.Threading.Thread.Sleep(waitStep);
                var result = this.ReadAllTextFromFile(resultFile);
                if (result != "success")
                {
                    this.logger.LogError(string.Format("Error occured while upgrading nuget packages. {0}", result));
                    throw new UpgradeException("Upgrade failed");
                }

                break;
            }

            this.logger.LogInformation("Operation completed successfully!");
        }

        private string ReadAllTextFromFile(string path)
        {
            using (var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var textReader = new StreamReader(fileStream))
            {
                return textReader.ReadToEnd();
            }
        }

        private async Task GenerateUpgradeConfig(IEnumerable<string> projectFilePaths, NuGetPackage newSitefinityVersionPackageTree, IEnumerable<string> packageSources)
        {
            this.logger.LogInformation("Exporting upgrade config...");

            XmlDocument powerShellXmlConfig = new XmlDocument();
            XmlElement powerShellXmlConfigNode = powerShellXmlConfig.CreateElement("config");
            powerShellXmlConfig.AppendChild(powerShellXmlConfigNode);

            foreach (string projectFilePath in projectFilePaths)
            {
                await this.GenerateProjectUpgradeConfigSection(powerShellXmlConfig, powerShellXmlConfigNode, projectFilePath, newSitefinityVersionPackageTree, packageSources);
            }

            powerShellXmlConfig.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Constants.SitefinityUpgradePowershellFolderName, "config.xml"));

            this.logger.LogInformation("Successfully exported upgrade config!");
        }

        private async Task GenerateProjectUpgradeConfigSection(XmlDocument powerShellXmlConfig, XmlElement powerShellXmlConfigNode, string projectFilePath, NuGetPackage newSitefinityVersionPackageTree, IEnumerable<string> packageSources)
        {
            XmlElement projectNode = powerShellXmlConfig.CreateElement("project");
            XmlAttribute projectNameAttribute = powerShellXmlConfig.CreateAttribute("name");
            projectNameAttribute.Value = projectFilePath.Split(new string[] { "\\", Constants.CsprojFileExtension, Constants.VBProjFileExtension }, StringSplitOptions.RemoveEmptyEntries).Last();
            projectNode.Attributes.Append(projectNameAttribute);
            powerShellXmlConfigNode.AppendChild(projectNode);

            Version currentSitefinityVersion = this.DetectSitefinityVersion(projectFilePath);
            if (currentSitefinityVersion == null)
            {
                this.logger.LogInformation($"Skip upgrade for project: '{projectFilePath}'. Current Sitefinity version was not detected.");

                return;
            }

            this.logger.LogInformation($"Detected sitefinity version for '{projectFilePath}' - '{currentSitefinityVersion}'.");

            this.logger.LogInformation($"Collecting Sitefinity NuGet package tree for '{projectFilePath}'...");
            NuGetPackage currentSitefinityVersionPackageTree = await this.sitefinityPackageManager.GetSitefinityPackageTree(currentSitefinityVersion.ToString(), packageSources);

            this.processedPackagesPerProjectCache[projectFilePath] = new HashSet<string>();
            if (!this.TryAddPackageTreeToProjectUpgradeConfigSection(powerShellXmlConfig, projectNode, projectFilePath, currentSitefinityVersionPackageTree, newSitefinityVersionPackageTree))
            {
                await this.ProcessPackagesForProjectUpgradeConfigSection(powerShellXmlConfig, projectNode, projectFilePath, currentSitefinityVersionPackageTree.Dependencies, newSitefinityVersionPackageTree);
            }
        }

        private async Task ProcessPackagesForProjectUpgradeConfigSection(XmlDocument powerShellXmlConfig, XmlElement projectNode, string projectFilePath, IEnumerable<NuGetPackage> currentSitefinityVersionPackages, NuGetPackage newSitefinityVersionPackageTree)
        {
            IList<NuGetPackage> packageTreesToProcessFurther = new List<NuGetPackage>();
            foreach (NuGetPackage currentSitefinityVersionPackage in currentSitefinityVersionPackages)
            {
                bool isPackageAlreadyProcessed = this.processedPackagesPerProjectCache[projectFilePath].Contains(currentSitefinityVersionPackage.Id);
                if (!isPackageAlreadyProcessed &&
                    !this.TryAddPackageTreeToProjectUpgradeConfigSection(powerShellXmlConfig, projectNode, projectFilePath, currentSitefinityVersionPackage, newSitefinityVersionPackageTree))
                {
                    packageTreesToProcessFurther.Add(currentSitefinityVersionPackage);
                }
            }

            foreach (NuGetPackage packageTree in packageTreesToProcessFurther)
            {
                await this.ProcessPackagesForProjectUpgradeConfigSection(powerShellXmlConfig, projectNode, projectFilePath, packageTree.Dependencies, newSitefinityVersionPackageTree);
            }
        }

        private bool TryAddPackageTreeToProjectUpgradeConfigSection(XmlDocument powerShellXmlConfig, XmlElement projectNode, string projectFilePath, NuGetPackage currentSitefinityVersionPackage, NuGetPackage newSitefinityVersionPackageTree)
        {
            bool packageExists = this.sitefinityPackageManager.PackageExists(currentSitefinityVersionPackage.Id, projectFilePath);
            if (!packageExists)
            {
                return false;
            }

            NuGetPackage newSitefinityVersionPackage = this.FindNuGetPackageByIdInDependencyTree(newSitefinityVersionPackageTree, currentSitefinityVersionPackage.Id);
            if (newSitefinityVersionPackage == null)
            {
                this.logger.LogWarning($"New version for package '{currentSitefinityVersionPackage.Id}' was not found. Package will not be upgraded.");

                return false;
            }

            this.AddPackageNodeToProjectUpgradeConfigSection(powerShellXmlConfig, projectNode, newSitefinityVersionPackage);

            // Add the NuGet package and all of its dependencies to the cache, because those packages will be upgraded as dependencies of the root package
            this.AddNuGetPackageTreeToCache(projectFilePath, newSitefinityVersionPackage);

            return true;
        }

        private void AddPackageNodeToProjectUpgradeConfigSection(XmlDocument powerShellXmlConfig, XmlElement projectNode, NuGetPackage nuGetPackage)
        {
            XmlElement packageNode = powerShellXmlConfig.CreateElement("package");
            XmlAttribute nameAttribute = powerShellXmlConfig.CreateAttribute("name");
            nameAttribute.Value = nuGetPackage.Id;
            XmlAttribute versionAttribute = powerShellXmlConfig.CreateAttribute("version");
            versionAttribute.Value = nuGetPackage.Version;
            packageNode.Attributes.Append(nameAttribute);
            packageNode.Attributes.Append(versionAttribute);
            projectNode.AppendChild(packageNode);
        }

        private void AddNuGetPackageTreeToCache(string projectFilePath, NuGetPackage nuGetPackage)
        {
            if (!this.processedPackagesPerProjectCache[projectFilePath].Contains(nuGetPackage.Id))
            {
                this.processedPackagesPerProjectCache[projectFilePath].Add(nuGetPackage.Id);
            }

            foreach (NuGetPackage nuGetPackageDependency in nuGetPackage.Dependencies)
            {
                this.AddNuGetPackageTreeToCache(projectFilePath, nuGetPackageDependency);
            }
        }

        private NuGetPackage FindNuGetPackageByIdInDependencyTree(NuGetPackage nuGetPackageTree, string id)
        {
            if (nuGetPackageTree.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            {
                return nuGetPackageTree;
            }

            foreach (NuGetPackage nuGetPackageTreeDependency in nuGetPackageTree.Dependencies)
            {
                NuGetPackage nuGetPackage = this.FindNuGetPackageByIdInDependencyTree(nuGetPackageTreeDependency, id);
                if (nuGetPackage != null)
                {
                    return nuGetPackage;
                }
            }

            return null;
        }

        private void SyncProjectReferencesWithPackages(IEnumerable<string> projectFilePaths, string solutionFolder)
        {
            foreach (string projectFilePath in projectFilePaths)
            {
                this.sitefinityPackageManager.SyncReferencesWithPackages(projectFilePath, solutionFolder);
            }
        }

        private IList<string> GetProjectsPathsFromSolution(string solutionPath, bool onlySitefinityProjects = false)
        {
            if (!solutionPath.EndsWith(Constants.SlnFileExtension, StringComparison.InvariantCultureIgnoreCase))
            {
                throw new UpgradeException(string.Format(Constants.FileIsNotSolutionMessage, solutionPath));
            }

            IEnumerable<string> projectFilesAbsolutePaths = SolutionFileEditor.GetProjects(solutionPath)
                .Select(sp => sp.AbsolutePath)
                .Where(ap => (ap.EndsWith(Constants.CsprojFileExtension, StringComparison.InvariantCultureIgnoreCase) || ap.EndsWith(Constants.VBProjFileExtension, StringComparison.InvariantCultureIgnoreCase)));

            if (onlySitefinityProjects)
            {
                projectFilesAbsolutePaths = projectFilesAbsolutePaths
                    .Where(ap => this.HasSitefinityReferences(ap) && this.HasValidSitefinityVersion(ap));
            }

            return projectFilesAbsolutePaths.ToList();
        }

        private bool HasValidSitefinityVersion(string projectFilePath)
        {
            Version currentVersion = this.DetectSitefinityVersion(projectFilePath);

            // The new version can be preview one, or similar. That's why we split by '-' and get the first part for validation.
            System.Version versionToUpgrade;
            if (string.IsNullOrWhiteSpace(this.Version) || !System.Version.TryParse(this.Version.Split('-').First(), out versionToUpgrade))
                throw new UpgradeException(string.Format("The version '{0}' you are trying to upgrade to is not valid.", this.Version));

            var projectName = Path.GetFileName(projectFilePath);
            if (versionToUpgrade <= currentVersion)
            {
                this.logger.LogWarning(string.Format(Constants.VersionIsGreaterThanOrEqual, projectName, currentVersion, versionToUpgrade));

                return false;
            }

            return true;
        }

        private bool HasSitefinityReferences(string projectFilePath)
        {
            IEnumerable<CsProjectFileReference> references = this.csProjectFileEditor.GetReferences(projectFilePath);
            bool hasSitefinityReferences = references.Any(r => this.IsSitefinityReference(r));

            return hasSitefinityReferences;
        }

        private bool IsSitefinityReference(CsProjectFileReference reference)
        {
            return this.ContainsSitefinityRefKeyword(reference) && reference.Include.Contains($"PublicKeyToken={Constants.SitefinityPublicKeyToken}");
        }

        private readonly IPromptService promptService;

        private readonly ISitefinityPackageManager sitefinityPackageManager;

        private readonly ICsProjectFileEditor csProjectFileEditor;

        private readonly IProjectConfigFileEditor projectConfigFileEditor;

        private readonly ILogger<object> logger;

        private readonly IVisualStudioWorker visualStudioWorker;

        private readonly IDictionary<string, HashSet<string>> processedPackagesPerProjectCache;
    }
}
