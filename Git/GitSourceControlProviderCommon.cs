using System;
using System.Collections.Generic;
using System.Linq;
using Inedo.BuildMaster.Extensibility.Agents;
using Inedo.BuildMaster.Extensibility.Providers.SourceControl;
using Inedo.BuildMaster.Files;
using Inedo.BuildMasterExtensions.Git.Clients;

namespace Inedo.BuildMasterExtensions.Git
{
    /// <summary>
    /// Used to share an implementation betweeen the Git provider and the GitHub provider
    /// since they have to inherit different base classes.
    /// </summary>
    internal sealed class GitSourceControlProviderCommon :  IGitSourceControlProvider
    {
        private IGitSourceControlProvider owner;
        private Lazy<GitClientBase> gitClient;

        public GitSourceControlProviderCommon(IGitSourceControlProvider ownerProvider, string gitExePath)
        {
            this.owner = ownerProvider;
            this.gitClient = new Lazy<GitClientBase>(
                () =>
                {
                    if (string.IsNullOrEmpty(gitExePath))
                        return new LilGitClient(this);
                    else
                        return new StandardGitClient(this, gitExePath);
                }
            );
        }

        private GitClientBase GitClient
        {
            get { return this.gitClient.Value; }
        }

        public void GetLatest(SourceControlContext context, string targetPath)
        {
            if (targetPath == null)
                throw new ArgumentNullException("targetPath");
            if (context.Repository == null)
                throw new ArgumentException(context.ToLegacyPathString() + " does not represent a valid Git path.", "sourcePath");

            this.EnsureLocalRepository(context);
            this.UpdateLocalRepository(context, null);
            this.ExportFiles(context, targetPath);
        }

        public DirectoryEntryInfo GetDirectoryEntryInfo(SourceControlContext context)
        {
            return this.GetDirectoryEntryInfo((GitPath)context);
        }

        private DirectoryEntryInfo GetDirectoryEntryInfo(GitPath context)
        {
            if (context.Repository == null)
            {
                return new DirectoryEntryInfo(
                    string.Empty,
                    string.Empty,
                    Repositories.Select(repo => new DirectoryEntryInfo(repo.Name, repo.Name, null, null)).ToArray(),
                    null
                );
            }
            else if (context.PathSpecifiedBranch == null)
            {
                this.EnsureLocalRepository(context);

                return new DirectoryEntryInfo(
                    context.Repository.Name,
                    context.Repository.Name,
                    this.GitClient.EnumBranches(context.Repository)
                        .Select(branch => new DirectoryEntryInfo(branch, GitPath.BuildSourcePath(context.Repository.Name, branch, null), null, null))
                        .ToArray(),
                    null
                );
            }
            else
            {
                this.EnsureLocalRepository(context);
                this.UpdateLocalRepository(context, null);

                var de = this.Agent.GetDirectoryEntry(new GetDirectoryEntryCommand()
                {
                    Path = context.WorkspaceDiskPath,
                    Recurse = false,
                    IncludeRootPath = false
                }).Entry;

                var subDirs = de.SubDirectories
                    .Where(entry => !entry.Name.StartsWith(".git"))
                    .Select(subdir => new DirectoryEntryInfo(subdir.Name, GitPath.BuildSourcePath(context.Repository.Name, context.PathSpecifiedBranch, subdir.Path.Replace('\\', '/')), null, null))
                    .ToArray();

                var files = de.Files
                    .Select(file => new FileEntryInfo(file.Name, GitPath.BuildSourcePath(context.Repository.Name, context.PathSpecifiedBranch, file.Path.Replace('\\', '/'))))
                    .ToArray();

                return new DirectoryEntryInfo(
                    de.Name,
                    context.ToString(),
                    subDirs,
                    files
                );
            }
        }

        public void ApplyLabel(string label, SourceControlContext context)
        {
            if (string.IsNullOrEmpty(label))
                throw new ArgumentNullException("label");

            if (context.Repository == null)
                throw new ArgumentException(context.ToLegacyPathString() + " does not represent a valid Git path.", "sourcePath");

            this.EnsureLocalRepository(context);
            this.UpdateLocalRepository(context, null);
            this.GitClient.ApplyTag(context.Repository, label);
        }

        public void GetLabeled(string label, SourceControlContext context, string targetPath)
        {
            if (string.IsNullOrEmpty(label))
                throw new ArgumentNullException("label");
            if (string.IsNullOrEmpty(targetPath))
                throw new ArgumentNullException("targetPath");

            if (context.Repository == null)
                throw new ArgumentException(context.ToLegacyPathString() + " does not represent a valid Git path.", "sourcePath");

            this.EnsureLocalRepository(context);
            this.UpdateLocalRepository(context, label);
            this.ExportFiles(context, targetPath);
        }

        public object GetCurrentRevision(SourceControlContext context)
        {
            if (context.Repository == null)
                throw new ArgumentException(context.ToLegacyPathString() + " does not represent a valid Git path.", "sourcePath");

            this.EnsureLocalRepository(context);
            this.GitClient.UpdateLocalRepo(context.Repository, context.Branch, null);
            return this.GitClient.GetLastCommit(context.Repository, context.Branch);
        }

        public void ValidateConnection()
        {
            foreach (var repo in this.Repositories)
            {
                if (!this.Agent.DirectoryExists(repo.GetDiskPath(this.Agent)))
                {
                    this.Agent.CreateDirectory(repo.GetDiskPath(this.Agent));
                    this.GitClient.CloneRepo(repo);
                }
            }
            
            this.GitClient.ValidateConnection();
        }

        public bool IsAvailable()
        {
            return true;
        }

        public byte[] GetFileContents(string filePath)
        {
            var context = new GitPath(this, filePath);
            if (context.Repository == null)
                throw new ArgumentException(filePath + " does not represent a valid Git path.", "filePath");

            this.EnsureLocalRepository(context);
            this.UpdateLocalRepository(context, null);

            return this.Agent.ReadFileBytes(context.WorkspaceDiskPath);
        }

        public IEnumerable<string> EnumerateBranches(SourceControlContext context)
        {
            return this.GitClient.EnumBranches(context.Repository);
        }

        public void EnsureLocalRepository(SourceControlContext context)
        {
            var fileOps = (IFileOperationsExecuter)this.Agent;
            var repoPath = context.Repository.GetDiskPath(fileOps);
            if (!fileOps.DirectoryExists(repoPath) || !fileOps.DirectoryExists(fileOps.CombinePath(repoPath, ".git")))
            {
                fileOps.CreateDirectory(repoPath);
                this.GitClient.CloneRepo(context.Repository);
            }
        }

        public void UpdateLocalRepository(SourceControlContext context, string tag)
        {
            this.GitClient.UpdateLocalRepo(context.Repository, context.Branch, tag);
        }

        public void ExportFiles(SourceControlContext context, string targetDirectory)
        {
            this.CopyNonGitFiles(context.WorkspaceDiskPath, targetDirectory);
        }

        public void Clone(SourceControlContext context)
        {
            this.GitClient.CloneRepo(context.Repository);
        }

        public void DeleteWorkspace(SourceControlContext context)
        {
            this.Agent.ClearFolder(context.WorkspaceDiskPath);
        }

        public IEnumerable<string> EnumerateLabels(SourceControlContext context)
        {
            throw new NotImplementedException();
        }

        public SourceRepository[] Repositories
        {
            get { return this.owner.Repositories; }
        }

        public IFileOperationsExecuter Agent
        {
            get { return this.owner.Agent; }
        }

        public Clients.ProcessResults ExecuteCommandLine(string fileName, string arguments, string workingDirectory)
        {
            return this.owner.ExecuteCommandLine(fileName, arguments, workingDirectory);
        }

        /// <summary>
        /// Copies files and subfolders from sourceFolder to targetFolder.
        /// </summary>
        /// <param name="sourceFolder">A path of the folder to be copied</param>
        /// <param name="targetFolder">A path of a folder to copy files to.  If targetFolder doesn't exist, it is created.</param>
        private void CopyNonGitFiles(string sourceFolder, string targetFolder)
        {
            var agent = this.Agent;

            if (!agent.DirectoryExists(sourceFolder))
                return;

            agent.CreateDirectory(targetFolder);
            char separator = agent.GetDirectorySeparator();
            var entry = agent.GetDirectoryEntry(new GetDirectoryEntryCommand
            {
                Path = sourceFolder,
                IncludeRootPath = true,
                Recurse = true
            }).Entry;
            Func<string, string> escapeSpecialChars = (p1) =>
            {
                return p1.Replace(@"$", @"\$");
            };
            Func<string, string, string> combinePaths = (p1, p2) =>
            {
                return p1.TrimEnd(separator) + separator + p2.TrimStart(separator);
            };

            string[] foldersToCreate = entry.Flatten().SelectMany(di => di.SubDirectories).Select(fi => escapeSpecialChars(fi.Path)).Where(path => !path.Contains(separator + @".git")).ToArray().Select(name => combinePaths(targetFolder, name.Substring(sourceFolder.Length))).ToArray();
            string[] filesToCopy = entry.Flatten().SelectMany(di => di.Files).Select(fi => escapeSpecialChars(fi.Path)).Where(path => !path.Contains(separator + @".git")).ToArray();

            foreach (string folder in foldersToCreate)
            {
                if (!agent.DirectoryExists(folder))
                {
                    agent.CreateDirectory(folder);
                }
            }
            agent.FileCopyBatch(
                sourceFolder,
                filesToCopy,
                targetFolder,
                filesToCopy.Select(name => combinePaths(targetFolder, name.Substring(sourceFolder.Length))).ToArray(),
                true,
                true
             );
        }
    }
}