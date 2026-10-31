using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Ex.Core.Course;

/// <summary>Reads and writes <c>exercises.yaml</c>. Writes are atomic (temp file, then replace).</summary>
public static class ManifestStore
{
    public const string FileName = "exercises.yaml";

    private const string Header =
        "# Exercises: course settings, titles and notes.\n" +
        "# Written by the Exercises Pipeline client. Safe to edit by hand while the client is closed.\n" +
        "# Git is the source of truth for which weeks and snapshots exist; this file only adds titles, notes and settings.\n\n";

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .WithEnumNamingConvention(LowerCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .WithEnumNamingConvention(LowerCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitEmptyCollections)
        .Build();

    public static CourseManifest Parse(string yaml)
    {
        var manifest = Deserializer.Deserialize<CourseManifest?>(yaml) ?? new CourseManifest();
        manifest.Course ??= new CourseInfo();
        manifest.Settings ??= new CourseSettings();
        manifest.State ??= new CourseState();
        manifest.State.PrepBranches ??= [];
        manifest.Weeks ??= [];
        foreach (var week in manifest.Weeks)
        {
            week.Repos ??= [];
            week.Steps ??= [];
        }
        return manifest;
    }

    public static string Serialize(CourseManifest manifest) => Header + Serializer.Serialize(manifest);

    public static CourseManifest Load(string path) => Parse(File.ReadAllText(path));

    public static void Save(string path, CourseManifest manifest)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, Serialize(manifest));
        File.Move(temp, path, overwrite: true);
    }
}
