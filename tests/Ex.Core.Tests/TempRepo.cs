using Ex.Core.Course;
using Ex.Core.Git;
using Ex.Core.Services;

namespace Ex.Core.Tests;

/// <summary>A throwaway course folder with one git repo in Projects\, for tests that run real git commands.</summary>
public sealed class TempCourse : IDisposable
{
    static TempCourse()
    {
        // Commits need an identity; do not depend on the machine's global git config.
        Environment.SetEnvironmentVariable("GIT_AUTHOR_NAME", "Test");
        Environment.SetEnvironmentVariable("GIT_AUTHOR_EMAIL", "test@example.com");
        Environment.SetEnvironmentVariable("GIT_COMMITTER_NAME", "Test");
        Environment.SetEnvironmentVariable("GIT_COMMITTER_EMAIL", "test@example.com");
    }

    private TempCourse(string folder, CourseSession session, string repoPath)
    {
        Folder = folder;
        Session = session;
        RepoPath = repoPath;
    }

    public string Folder { get; }
    public CourseSession Session { get; }
    public string RepoPath { get; }
    public RepoService Repos => Session.Repos;
    public GitRunner Git => Session.Git;

    public static async Task<TempCourse> CreateAsync(ProjectType type = ProjectType.DotNet)
    {
        var folder = Path.Combine(Path.GetTempPath(), "ex-tests", Guid.NewGuid().ToString("N")[..8]);
        var course = CourseFolder.Create(folder);
        var session = new CourseSession(course, new GitRunner());
        var repoPath = await session.Repos.InitRepoAsync(course.ProjectsPath, "test-repo", type);
        await session.RefreshAsync();
        return new TempCourse(folder, session, repoPath);
    }

    public void Write(string relativePath, string content)
    {
        var full = Path.Combine(RepoPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public string Read(string relativePath) => File.ReadAllText(Path.Combine(RepoPath, relativePath));

    public bool Exists(string relativePath) => File.Exists(Path.Combine(RepoPath, relativePath));

    public async Task CommitAsync(string file, string content, string message)
    {
        Write(file, content);
        await Repos.CommitAllAsync(RepoPath, message);
    }

    public async Task<string> FileAtAsync(string branch, string file) =>
        await Repos.ReadFileAtAsync(RepoPath, branch, file) ?? throw new InvalidOperationException($"{file} not on {branch}");

    public async Task<Model.RepoState> StateAsync()
    {
        await Session.RefreshAsync();
        return Session.States.Single();
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Folder, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal); // git object files are read-only
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
