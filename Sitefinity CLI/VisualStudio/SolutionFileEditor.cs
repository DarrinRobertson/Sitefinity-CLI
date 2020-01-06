﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Sitefinity_CLI.VisualStudio
{
    /// <summary>
    /// A class used to manage the contents of a solution file.
    /// </summary>
    public static class SolutionFileEditor
    {
        /// <summary>
        /// Returns the projects from a solution file.
        /// </summary>
        /// <param name="solutionFilePath">The path to the solution file.</param>
        /// <returns>Collection of <see cref="SolutionProject"/>.</returns>
        public static IEnumerable<SolutionProject> GetProjects(string solutionFilePath)
        {
            string solutionFileContent = GetSolutionFileContentAsString(solutionFilePath);

            IList<SolutionProject> solutionProjects = new List<SolutionProject>();
            foreach (Match match in projectLineRegex.Matches(solutionFileContent))
            {
                Guid projectTypeGuid = Guid.Parse(match.Groups[ProjectTypeGuidRegexGroupName].Value.Trim());
                string projectName = match.Groups[ProjectNameRegexGroupName].Value.Trim();
                string relativePath = match.Groups[ProjectRelativePathRegexGroupName].Value.Trim();
                Guid projectGuid = Guid.Parse(match.Groups[ProjectGuidRegexGroupName].Value.Trim());

                SolutionProject solutionProject = new SolutionProject(projectGuid, projectName, relativePath, projectTypeGuid, solutionFilePath);
                solutionProjects.Add(solutionProject);
            }

            return solutionProjects;
        }

        /// <summary>
        /// Adds reference to a csproj file in a solution. 
        /// </summary>
        /// <param name="slnFilePath">The solution file path</param>
        /// <param name="csProjFilePath">The csproj file path</param>
        /// <param name="projectGuid">The guid of the project</param>
        /// <param name="webAppName">The name of the SitefinityWebAWpp</param>
        public static void AddProject(string solutionFilePath, SolutionProject solutionProject)
        {
            string solutionFileContent = GetSolutionFileContentAsString(solutionFilePath);

            solutionFileContent = AddProjectInfoInSolutionFileContent(solutionFileContent, solutionProject);
            solutionFileContent = AddProjectGlobalSectionInSolutionFileContent(solutionFileContent, solutionProject);

            SaveSolutionFileContent(solutionFilePath, solutionFileContent);
        }

        private static string GetSolutionFileContentAsString(string filePath)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"Unable to find {filePath}");
            }

            return File.ReadAllText(filePath);
        }

        private static void SaveSolutionFileContent(string filePath, string fileContent)
        {
            try
            {
                File.WriteAllText(filePath, fileContent);
            }
            catch (UnauthorizedAccessException)
            {
                throw new UnauthorizedAccessException(Constants.AddFilesInsufficientPrivilegesMessage);
            }
            catch
            {
                throw new Exception(Constants.AddFilesToSolutionFailureMessage);
            }
        }

        private static string AddProjectInfoInSolutionFileContent(string solutionFileContent, SolutionProject solutionProject)
        {
            var endProjectIndex = solutionFileContent.LastIndexOf("EndProject");
            if (endProjectIndex < 0)
            {
                throw new Exception(Constants.SolutionNotReadable);
            }

            var projectInfo = GenerateProjectInfoFromSolutionProject(solutionProject);

            solutionFileContent = solutionFileContent.Insert(endProjectIndex, projectInfo);

            return solutionFileContent;
        }

        private static string AddProjectGlobalSectionInSolutionFileContent(string solutionFileContent, SolutionProject solutionProject)
        {
            int beginGlobalSectionIndex = solutionFileContent.IndexOf("GlobalSection(ProjectConfigurationPlatforms)");
            int endGlobalSectionIndex = solutionFileContent.IndexOf("EndGlobalSection", beginGlobalSectionIndex);

            if (endGlobalSectionIndex < 0)
            {
                throw new Exception(Constants.SolutionNotReadable);
            }

            var globalSection = GenerateGlobalSectionFromSolutionProject(solutionProject);
            solutionFileContent = solutionFileContent.Insert(endGlobalSectionIndex, globalSection);

            return solutionFileContent;
        }

        private static string GenerateGlobalSectionFromSolutionProject(SolutionProject solutionProject)
        {
            return string.Format(GlobalSectionMask, solutionProject.ProjectGuid.ToString().ToUpper());
        }

        private static string GenerateProjectInfoFromSolutionProject(SolutionProject solutionProject)
        {
            return string.Format(ProjectInfoMask,
                solutionProject.ProjectTypeGuid.ToString().ToUpper(),
                solutionProject.ProjectGuid.ToString().ToUpper(),
                solutionProject.ProjectName,
                solutionProject.RelativePath);
        }

        private const string ProjectTypeGuidRegexGroupName = "PROJECTTYPEGUID";

        private const string ProjectNameRegexGroupName = "PROJECTNAME";

        private const string ProjectRelativePathRegexGroupName = "RELATIVEPATH";

        private const string ProjectGuidRegexGroupName = "PROJECTGUID";

        // An example of a project line looks like this:
        // Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "ClassLibrary1", "ClassLibrary1\ClassLibrary1.csproj", "{05A5AD00-71B5-4612-AF2F-9EA9121C4111}"
        private static readonly Regex projectLineRegex = new Regex
        (
            $"Project\\(\"{{(?<{ProjectTypeGuidRegexGroupName}>.*)}}\"\\)"
            + "\\s*=\\s*"                                   // Any amount of whitespace plus "=" plus any amount of whitespace
            + $"\"(?<{ProjectNameRegexGroupName}>.*)\""
            + "\\s*,\\s*"                                   // Any amount of whitespace plus "," plus any amount of whitespace
            + $"\"(?<{ProjectRelativePathRegexGroupName}>.*)\""
            + "\\s*,\\s*"                                   // Any amount of whitespace plus "," plus any amount of whitespace
            + $"\"(?<{ProjectGuidRegexGroupName}>.*)\""
        );

        private const string ProjectInfoMask = @"EndProject
Project(""{{{0}}}"") = ""{2}"", ""{3}"", ""{{{1}}}""
";

        private const string GlobalSectionMask = @"    {{{0}}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{{{0}}}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{{{0}}}.Release Pro|Any CPU.ActiveCfg = Release|Any CPU
		{{{0}}}.Release Pro|Any CPU.Build.0 = Release|Any CPU
		{{{0}}}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{{{0}}}.Release|Any CPU.Build.0 = Release|Any CPU
    ";
    }
}
