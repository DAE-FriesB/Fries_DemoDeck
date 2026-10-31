namespace Ex.Core.Course;

/// <summary>
/// A course folder: <c>exercises.yaml</c> in its root and a projects folder (default <c>Projects\</c>)
/// holding the exercise repos side by side.
/// </summary>
public sealed class CourseFolder
{
    private CourseFolder(string folder, CourseManifest manifest)
    {
        Folder = folder;
        Manifest = manifest;
    }

    public string Folder { get; }
    public CourseManifest Manifest { get; }

    public string ManifestPath => Path.Combine(Folder, ManifestStore.FileName);

    public string ProjectsPath => Path.GetFullPath(Path.Combine(Folder, Manifest.Settings.ProjectsDir));

    public string DisplayName => Manifest.Course.Name is { Length: > 0 } name
        ? (Manifest.Course.Year is { Length: > 0 } year ? $"{name} ({year})" : name)
        : Path.GetFileName(Folder);

    public static bool HasManifest(string folder) => File.Exists(Path.Combine(folder, ManifestStore.FileName));

    public static CourseFolder Open(string folder)
    {
        folder = Path.GetFullPath(folder);
        var path = Path.Combine(folder, ManifestStore.FileName);
        if (!File.Exists(path))
            throw new FileNotFoundException($"No {ManifestStore.FileName} in {folder}.", path);
        return new CourseFolder(folder, ManifestStore.Load(path));
    }

    /// <summary>Creates a new <c>exercises.yaml</c> (and the projects folder) in <paramref name="folder"/>.</summary>
    public static CourseFolder Create(string folder, CourseManifest? manifest = null)
    {
        folder = Path.GetFullPath(folder);
        var course = new CourseFolder(folder, manifest ?? new CourseManifest());
        Directory.CreateDirectory(course.ProjectsPath);
        course.Save();
        return course;
    }

    public void Save() => ManifestStore.Save(ManifestPath, Manifest);

    /// <summary>Every direct subfolder of the projects folder that is a git repository.</summary>
    public IReadOnlyList<string> FindRepoPaths()
    {
        if (!Directory.Exists(ProjectsPath))
            return [];
        return Directory.EnumerateDirectories(ProjectsPath)
            .Where(d => Directory.Exists(Path.Combine(d, ".git")) || File.Exists(Path.Combine(d, ".git")))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
