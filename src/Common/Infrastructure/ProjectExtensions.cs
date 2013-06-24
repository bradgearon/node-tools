using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Common.Ioc;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace Common.Infrastructure
{
    public static class ProjectExtensions
    {
        private const string WebConfig = "web.config";
        private const string AppConfig = "app.config";
        private const string BinFolder = "Bin";

        private static readonly Dictionary<string, string> _knownNestedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                {"web.debug.config", "web.config"},
                {"web.release.config", "web.config"}
            };

        private static readonly HashSet<string> _supportedProjectTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                VsConstants.WebSiteProjectTypeGuid,
                VsConstants.CsharpProjectTypeGuid,
                VsConstants.VbProjectTypeGuid,
                VsConstants.CppProjectTypeGuid,
                VsConstants.JsProjectTypeGuid,
                VsConstants.FsharpProjectTypeGuid,
                VsConstants.NemerleProjectTypeGuid,
                VsConstants.WixProjectTypeGuid,
                VsConstants.SynergexProjectTypeGuid,
                VsConstants.NomadForVisualStudioProjectTypeGuid
            };

        private static readonly HashSet<string> _unsupportedProjectTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                VsConstants.LightSwitchProjectTypeGuid,
                VsConstants.InstallShieldLimitedEditionTypeGuid
            };

        private static readonly IEnumerable<string> _fileKinds = new[] {VsConstants.VsProjectItemKindPhysicalFile, VsConstants.VsProjectItemKindSolutionItem};
        private static readonly IEnumerable<string> _folderKinds = new[] {VsConstants.VsProjectItemKindPhysicalFolder};

        // List of project types that cannot have references added to them
        private static readonly string[] _unsupportedProjectTypesForAddingReferences = new[]
            {
                VsConstants.WixProjectTypeGuid,
                VsConstants.CppProjectTypeGuid,
            };

        // List of project types that cannot have binding redirects added
        private static readonly string[] _unsupportedProjectTypesForBindingRedirects = new[]
            {
                VsConstants.WixProjectTypeGuid,
                VsConstants.JsProjectTypeGuid,
                VsConstants.NemerleProjectTypeGuid,
                VsConstants.CppProjectTypeGuid,
                VsConstants.SynergexProjectTypeGuid,
                VsConstants.NomadForVisualStudioProjectTypeGuid,
            };

        private static readonly char[] PathSeparatorChars = new[] {Path.DirectorySeparatorChar};

        // Get the ProjectItems for a folder path
        public static ProjectItems GetProjectItems(this Project project, string folderPath, bool createIfNotExists = false)
        {
            if (String.IsNullOrEmpty(folderPath))
            {
                return project.ProjectItems;
            }

            // Traverse the path to get at the directory
            string[] pathParts = folderPath.Split(PathSeparatorChars, StringSplitOptions.RemoveEmptyEntries);

            // 'cursor' can contain a reference to either a Project instance or ProjectItem instance. 
            // Both types have the ProjectItems property that we want to access.
            object cursor = project;

            string fullPath = project.GetFullPath();
            string folderRelativePath = String.Empty;

            foreach (string part in pathParts)
            {
                fullPath = Path.Combine(fullPath, part);
                folderRelativePath = Path.Combine(folderRelativePath, part);

                cursor = GetOrCreateFolder(project, cursor, fullPath, folderRelativePath, part, createIfNotExists);
                if (cursor == null)
                {
                    return null;
                }
            }

            return GetProjectItems(cursor);
        }

        public static ProjectItem GetProjectItem(this Project project, string path)
        {
            string folderPath = Path.GetDirectoryName(path);
            string itemName = Path.GetFileName(path);

            ProjectItems container = GetProjectItems(project, folderPath);

            ProjectItem projectItem;
            // If we couldn't get the folder, or the child item doesn't exist, return null
            if (container == null ||
                (!container.TryGetFile(itemName, out projectItem) &&
                 !container.TryGetFolder(itemName, out projectItem)))
            {
                return null;
            }

            return projectItem;
        }

        /// <summary>
        ///     Recursively retrieves all supported child projects of a virtual folder.
        /// </summary>
        /// <param name="project">The root container project</param>
        public static IEnumerable<Project> GetSupportedChildProjects(this Project project)
        {
            if (!project.IsSolutionFolder())
            {
                yield break;
            }

            var containerProjects = new Queue<Project>();
            containerProjects.Enqueue(project);

            while (containerProjects.Any())
            {
                Project containerProject = containerProjects.Dequeue();
                foreach (ProjectItem item in containerProject.ProjectItems)
                {
                    Project nestedProject = item.SubProject;
                    if (nestedProject == null)
                    {
                        continue;
                    }
                    else if (nestedProject.IsSupported())
                    {
                        yield return nestedProject;
                    }
                    else if (nestedProject.IsSolutionFolder())
                    {
                        containerProjects.Enqueue(nestedProject);
                    }
                }
            }
        }

        public static bool DeleteProjectItem(this Project project, string path)
        {
            ProjectItem projectItem = GetProjectItem(project, path);
            if (projectItem == null)
            {
                return false;
            }

            projectItem.Delete();
            return true;
        }

        public static bool TryGetFolder(this ProjectItems projectItems, string name, out ProjectItem projectItem)
        {
            projectItem = GetProjectItem(projectItems, name, _folderKinds);

            return projectItem != null;
        }

        public static bool TryGetFile(this ProjectItems projectItems, string name, out ProjectItem projectItem)
        {
            projectItem = GetProjectItem(projectItems, name, _fileKinds);

            if (projectItem == null)
            {
                // Try to get the nested project item
                return TryGetNestedFile(projectItems, name, out projectItem);
            }

            return projectItem != null;
        }

        public static bool ContainsFile(this Project project, string path)
        {
            if (string.Equals(project.Kind, VsConstants.WixProjectTypeGuid, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(project.Kind, VsConstants.NemerleProjectTypeGuid, StringComparison.OrdinalIgnoreCase))
            {
                // For Wix project, IsDocumentInProject() returns not found
                // even though the file is in the project. So we use GetProjectItem()
                // instead.
                ProjectItem item = project.GetProjectItem(path);
                return item != null;
            }
            else
            {
                var vsProject = (IVsProject) project.ToVsHierarchy();
                if (vsProject == null)
                {
                    return false;
                }
                int pFound;
                uint itemId;
                int hr = vsProject.IsDocumentInProject(path, out pFound, new VSDOCUMENTPRIORITY[0], out itemId);
                return ErrorHandler.Succeeded(hr) && pFound == 1;
            }
        }

        /// <summary>
        ///     If we didn't find the project item at the top level, then we look one more level down.
        ///     In VS files can have other nested files like foo.aspx and foo.aspx.cs or web.config and web.debug.config.
        ///     These are actually top level files in the file system but are represented as nested project items in VS.
        /// </summary>
        private static bool TryGetNestedFile(ProjectItems projectItems, string name, out ProjectItem projectItem)
        {
            string parentFileName;
            if (!_knownNestedFiles.TryGetValue(name, out parentFileName))
            {
                parentFileName = Path.GetFileNameWithoutExtension(name);
            }

            // If it's not one of the known nested files then we're going to look up prefixes backwards
            // i.e. if we're looking for foo.aspx.cs then we look for foo.aspx then foo.aspx.cs as a nested file
            ProjectItem parentProjectItem = GetProjectItem(projectItems, parentFileName, _fileKinds);

            if (parentProjectItem != null)
            {
                // Now try to find the nested file
                projectItem = GetProjectItem(parentProjectItem.ProjectItems, name, _fileKinds);
            }
            else
            {
                projectItem = null;
            }

            return projectItem != null;
        }

        public static string GetUniqueName(this Project project)
        {
            if (project.IsWixProject())
            {
                // Wix project doesn't offer UniqueName property
                return project.FullName;
            }

            try
            {
                return project.UniqueName;
            }
            catch (COMException)
            {
                return project.FullName;
            }
        }

        public static bool IsJavaScriptProject(this Project project)
        {
            return project != null && VsConstants.JsProjectTypeGuid.Equals(project.Kind, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsNativeProject(this Project project)
        {
            return project != null && VsConstants.CppProjectTypeGuid.Equals(project.Kind, StringComparison.OrdinalIgnoreCase);
        }

        // TODO: Return null for library projects
        public static string GetConfigurationFile(this Project project)
        {
            return project.IsWebProject() ? WebConfig : AppConfig;
        }

        private static ProjectItem GetProjectItem(this ProjectItems projectItems, string name, IEnumerable<string> allowedItemKinds)
        {
            try
            {
                ProjectItem projectItem = projectItems.Item(name);
                if (projectItem != null && allowedItemKinds.Contains(projectItem.Kind, StringComparer.OrdinalIgnoreCase))
                {
                    return projectItem;
                }
            }
            catch
            {
            }

            return null;
        }

        public static IEnumerable<ProjectItem> GetChildItems(this Project project, string path, string filter, params string[] kinds)
        {
            ProjectItems projectItems = GetProjectItems(project, path);

            if (projectItems == null)
            {
                return Enumerable.Empty<ProjectItem>();
            }

            Regex matcher = filter.Equals("*.*", StringComparison.OrdinalIgnoreCase) ? null : GetFilterRegex(filter);

            return from ProjectItem p in projectItems
                   where kinds.Contains(p.Kind) && (matcher == null || matcher.IsMatch(p.Name))
                   select p;
        }

        public static string GetFullPath(this Project project)
        {
            var fullPath = project.GetPropertyValue<string>("FullPath");
            if (!String.IsNullOrEmpty(fullPath))
            {
                // Some Project System implementations (JS metro app) return the project 
                // file as FullPath. We only need the parent directory
                if (File.Exists(fullPath))
                {
                    fullPath = Path.GetDirectoryName(fullPath);
                }
            }
            else
            {
                // C++ projects do not have FullPath property, but do have ProjectDirectory one.
                fullPath = project.GetPropertyValue<string>("ProjectDirectory");
            }

            return fullPath;
        }

        public static string GetTargetFramework(this Project project)
        {
            if (project == null)
            {
                return null;
            }

            if (project.IsJavaScriptProject())
            {
                // HACK: The JS Metro project does not have a TargetFrameworkMoniker property set. 
                // We hard-code the return value so that it behaves as if it had a WinRT target 
                // framework, i.e. .NETCore, Version=4.5

                // Review: What about future versions? Let's not worry about that for now.
                return ".NETCore, Version=4.5";
            }

            if (project.IsNativeProject())
            {
                // The C++ project does not have a TargetFrameworkMoniker property set. 
                // We hard-code the return value to Native.
                return "Native, Version=0.0";
            }

            return project.GetPropertyValue<string>("TargetFrameworkMoniker");
        }

        public static FrameworkName GetTargetFrameworkName(this Project project)
        {
            string targetFrameworkMoniker = project.GetTargetFramework();
            if (targetFrameworkMoniker != null)
            {
                return new FrameworkName(targetFrameworkMoniker);
            }

            return null;
        }

        public static T GetPropertyValue<T>(this Project project, string propertyName)
        {
            if (project.Properties == null)
            {
                // this happens in unit tests
                return default(T);
            }

            try
            {
                Property property = project.Properties.Item(propertyName);
                if (property != null)
                {
                    // REVIEW: Should this cast or convert?
                    return (T) property.Value;
                }
            }
            catch (ArgumentException)
            {
            }
            return default(T);
        }

        private static Regex GetFilterRegex(string wildcard)
        {
            string pattern = String.Join(String.Empty, wildcard.Split('.').Select(GetPattern));
            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture);
        }

        private static string GetPattern(string token)
        {
            return token == "*" ? @"(.*)" : @"(" + token + ")";
        }

        // 'parentItem' can be either a Project or ProjectItem
        private static ProjectItem GetOrCreateFolder(
            Project project,
            object parentItem,
            string fullPath,
            string folderRelativePath,
            string folderName,
            bool createIfNotExists)
        {
            if (parentItem == null)
            {
                return null;
            }

            ProjectItem subFolder;

            ProjectItems projectItems = GetProjectItems(parentItem);
            if (projectItems.TryGetFolder(folderName, out subFolder))
            {
                // Get the sub folder
                return subFolder;
            }
            else if (createIfNotExists)
            {
                // The JS Metro project system has a bug whereby calling AddFolder() to an existing folder that
                // does not belong to the project will throw. To work around that, we have to manually include 
                // it into our project.
                if (project.IsJavaScriptProject() && Directory.Exists(fullPath))
                {
                    bool succeeded = IncludeExistingFolderToProject(project, folderRelativePath);
                    if (succeeded)
                    {
                        // IMPORTANT: after including the folder into project, we need to get 
                        // a new ProjectItems snapshot from the parent item. Otherwise, reusing 
                        // the old snapshot from above won't have access to the added folder.
                        projectItems = GetProjectItems(parentItem);
                        if (projectItems.TryGetFolder(folderName, out subFolder))
                        {
                            // Get the sub folder
                            return subFolder;
                        }
                    }
                    return null;
                }

                try
                {
                    return projectItems.AddFromDirectory(fullPath);
                }
                catch (NotImplementedException)
                {
                    // This is the case for F#'s project system, we can't add from directory so we fall back
                    // to this impl
                    return projectItems.AddFolder(folderName);
                }
            }

            return null;
        }

        private static ProjectItems GetProjectItems(object parent)
        {
            var project = parent as Project;
            if (project != null)
            {
                return project.ProjectItems;
            }

            var projectItem = parent as ProjectItem;
            if (projectItem != null)
            {
                return projectItem.ProjectItems;
            }

            return null;
        }

        private static bool IncludeExistingFolderToProject(Project project, string folderRelativePath)
        {
            var projectHierarchy = (IVsUIHierarchy) project.ToVsHierarchy();

            uint itemId;
            int hr = projectHierarchy.ParseCanonicalName(folderRelativePath, out itemId);
            if (!ErrorHandler.Succeeded(hr))
            {
                return false;
            }

            // Execute command to include the existing folder into project. Must do this on UI thread.
            hr = ThreadHelper.Generic.Invoke(() =>
                                             projectHierarchy.ExecCommand(
                                                 itemId,
                                                 ref VsMenus.guidStandardCommandSet2K,
                                                 (int) VSConstants.VSStd2KCmdID.INCLUDEINPROJECT,
                                                 0,
                                                 IntPtr.Zero,
                                                 IntPtr.Zero));

            return ErrorHandler.Succeeded(hr);
        }

        public static bool IsWebProject(this Project project)
        {
            string[] types = project.GetProjectTypeGuids();
            return types.Contains(VsConstants.WebSiteProjectTypeGuid, StringComparer.OrdinalIgnoreCase) ||
                   types.Contains(VsConstants.WebApplicationProjectTypeGuid, StringComparer.OrdinalIgnoreCase);
        }

        public static bool IsWebSite(this Project project)
        {
            return project.Kind != null && project.Kind.Equals(VsConstants.WebSiteProjectTypeGuid, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsWindowsStoreApp(this Project project)
        {
            string[] types = project.GetProjectTypeGuids();
            return types.Contains(VsConstants.WindowsStoreProjectTypeGuid, StringComparer.OrdinalIgnoreCase);
        }

        public static bool IsWixProject(this Project project)
        {
            return project.Kind != null && project.Kind.Equals(VsConstants.WixProjectTypeGuid, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsSupported(this Project project)
        {
            return project.Kind != null && _supportedProjectTypes.Contains(project.Kind);
        }

        public static bool IsExplicitlyUnsupported(this Project project)
        {
            return project.Kind == null || _unsupportedProjectTypes.Contains(project.Kind);
        }

        public static bool IsSolutionFolder(this Project project)
        {
            return project.Kind != null && project.Kind.Equals(VsConstants.VsProjectItemKindSolutionFolder, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsTopLevelSolutionFolder(this Project project)
        {
            return IsSolutionFolder(project) && project.ParentProjectItem == null;
        }

        public static bool SupportsReferences(this Project project)
        {
            return project.Kind != null &&
                   !_unsupportedProjectTypesForAddingReferences.Contains(project.Kind, StringComparer.OrdinalIgnoreCase);
        }

        public static bool SupportsBindingRedirects(this Project project)
        {
            return (project.Kind != null & !_unsupportedProjectTypesForBindingRedirects.Contains(project.Kind, StringComparer.OrdinalIgnoreCase)) &&
                   !project.IsWindowsStoreApp();
        }

        public static bool IsUnloaded(this Project project)
        {
            return VsConstants.UnloadedProjectTypeGuid.Equals(project.Kind, StringComparison.OrdinalIgnoreCase);
        }

        public static string GetOutputPath(this Project project)
        {
            // For Websites the output path is the bin folder
            string outputPath = project.IsWebSite() ? BinFolder : project.ConfigurationManager.ActiveConfiguration.Properties.Item("OutputPath").Value.ToString();
            return Path.Combine(project.GetFullPath(), outputPath);
        }

        public static IVsHierarchy ToVsHierarchy(this Project project)
        {
            IVsHierarchy hierarchy;

            // Get the vs solution
            var solution = ServiceLocator.GetInstance<IVsSolution>();
            int hr = solution.GetProjectOfUniqueName(project.GetUniqueName(), out hierarchy);

            if (hr != VsConstants.S_OK)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            return hierarchy;
        }

        public static IVsProjectBuildSystem ToVsProjectBuildSystem(this Project project)
        {
            if (project == null)
            {
                throw new ArgumentNullException("project");
            }
            // Convert the project to an IVsHierarchy and see if it implements IVsProjectBuildSystem
            return project.ToVsHierarchy() as IVsProjectBuildSystem;
        }

        public static string[] GetProjectTypeGuids(this Project project)
        {
            // Get the vs hierarchy as an IVsAggregatableProject to get the project type guids

            IVsHierarchy hierarchy = project.ToVsHierarchy();
            var aggregatableProject = hierarchy as IVsAggregatableProject;
            if (aggregatableProject != null)
            {
                string projectTypeGuids;
                int hr = aggregatableProject.GetAggregateProjectTypeGuids(out projectTypeGuids);

                if (hr != VsConstants.S_OK)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }

                return projectTypeGuids.Split(';');
            }
            else if (!String.IsNullOrEmpty(project.Kind))
            {
                return new[] {project.Kind};
            }
            else
            {
                return new string[0];
            }
        }


        /// <summary>
        ///     Returns the unique name of the specified project including all solution folder names containing it.
        /// </summary>
        /// <remarks>
        ///     This is different from the DTE Project.UniqueName property, which is the absolute path to the project file.
        /// </remarks>
        public static string GetCustomUniqueName(this Project project)
        {
            if (project.IsWebSite())
            {
                // website projects always have unique name
                return project.Name;
            }
            else
            {
                var nameParts = new Stack<string>();

                Project cursor = project;
                nameParts.Push(cursor.Name);

                // walk up till the solution root
                while (cursor.ParentProjectItem != null && cursor.ParentProjectItem.ContainingProject != null)
                {
                    cursor = cursor.ParentProjectItem.ContainingProject;
                    nameParts.Push(cursor.Name);
                }

                return String.Join("\\", nameParts);
            }
        }

        public static bool IsParentProjectExplicitlyUnsupported(this Project project)
        {
            if (project.ParentProjectItem == null || project.ParentProjectItem.ContainingProject == null)
            {
                // this project is not a child of another project
                return false;
            }

            Project parentProject = project.ParentProjectItem.ContainingProject;
            return parentProject.IsExplicitlyUnsupported();
        }


        /// <summary>
        ///     This method truncates Website projects into the VS-format, e.g. C:\..\WebSite1, but it uses Name instead of SafeName from Solution Manager.
        /// </summary>
        public static string GetDisplayName(this Project project)
        {
            return GetDisplayName(project, p => p.Name);
        }

        private static string GetDisplayName(this Project project, Func<Project, string> nameSelector)
        {
            return nameSelector(project);
        }

        private class PathComparer : IEqualityComparer<string>
        {
            public static readonly PathComparer Default = new PathComparer();

            public bool Equals(string x, string y)
            {
                return Path.GetFileName(x).Equals(Path.GetFileName(y));
            }

            public int GetHashCode(string obj)
            {
                return Path.GetFileName(obj).GetHashCode();
            }
        }
    }
}