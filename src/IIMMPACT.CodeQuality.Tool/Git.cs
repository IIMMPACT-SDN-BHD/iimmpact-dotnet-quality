/// <summary>Reads trusted baseline files and source snapshots from Git revisions.</summary>
internal static class Git
{
    /// <summary>Resolves a revision string to a full commit SHA inside the repository.</summary>
    public static async Task<string> ResolveCommitAsync(string repoDir, string revision)
    {
        var (exit, output) = await ScanCollector.RunProcessAsync(
            "git", ["rev-parse", "--verify", $"{revision}^{{commit}}"], repoDir);
        if (exit != 0)
        {
            throw new ScanException(
                $"cannot resolve '{revision}' to a commit in {repoDir}\n{output.Trim()}");
        }
        return output.Trim();
    }

    /// <summary>The repository root that contains the solution.</summary>
    public static async Task<string> RepositoryRootAsync(string solutionDir)
    {
        var (exit, output) = await ScanCollector.RunProcessAsync(
            "git", ["rev-parse", "--show-toplevel"], solutionDir);
        if (exit != 0)
        {
            throw new ScanException($"solution is not inside a Git repository: {solutionDir}");
        }
        return output.Trim();
    }

    /// <summary>Reads a file out of a commit; null when the file does not exist there.</summary>
    public static async Task<string?> ReadFileAsync(string repoDir, string commit, string repoRelativePath)
    {
        var (exit, output) = await ScanCollector.RunProcessAsync(
            "git", ["show", $"{commit}:{ToGitPath(repoRelativePath)}"], repoDir);
        return exit == 0 ? output : null;
    }

    /// <summary>Lists every file a commit contains under a directory prefix.</summary>
    public static async Task<List<string>> ListFilesAsync(string repoDir, string commit, string prefix = "")
    {
        var args = new List<string> { "ls-tree", "-r", "--name-only", commit };
        if (!string.IsNullOrEmpty(prefix))
        {
            args.Add("--");
            args.Add(prefix);
        }
        var (exit, output) = await ScanCollector.RunProcessAsync("git", args, repoDir);
        if (exit != 0)
        {
            throw new ScanException($"git ls-tree failed for {commit}\n{output.Trim()}");
        }
        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    /// <summary>A file's content hash inside a commit; null when absent.</summary>
    public static async Task<string?> FileDigestAsync(string repoDir, string commit, string repoRelativePath)
    {
        var (exit, output) = await ScanCollector.RunProcessBytesAsync(
            "git", ["show", $"{commit}:{ToGitPath(repoRelativePath)}"], repoDir);
        if (exit != 0)
        {
            return null;
        }
        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(output)).ToLowerInvariant();
    }

    public static async Task<string> ExportTreeAsync(string repoDir, string commit)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"iimmpact-quality-source-{Guid.NewGuid():N}");
        var archive = directory + ".tar";
        Directory.CreateDirectory(directory);
        var (archiveExit, archiveOutput) = await ScanCollector.RunProcessAsync(
            "git", ["archive", "--format=tar", "-o", archive, commit], repoDir);
        if (archiveExit != 0) throw new ScanException($"git archive failed for {commit}: {archiveOutput.Trim()}");
        var (extractExit, extractOutput) = await ScanCollector.RunProcessAsync(
            "tar", ["-xf", archive, "-C", directory], repoDir);
        File.Delete(archive);
        if (extractExit != 0)
        {
            Directory.Delete(directory, recursive: true);
            throw new ScanException($"cannot extract source revision {commit}: {extractOutput.Trim()}");
        }
        return directory;
    }

    private static string ToGitPath(string path) => path.Replace('\\', '/');
}
