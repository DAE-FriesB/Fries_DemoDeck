using Ex.Core.Course;

namespace Ex.Core.Tests;

public class ProjectWatcherTests
{
    [Theory]
    [InlineData(@"unity-project\Assets\Scripts\Player.cs", true)]
    [InlineData(@"unity-project\Assets\Scripts\Player.cs.meta", true)]
    [InlineData(@"unity-project\Packages\manifest.json", true)]
    [InlineData(@"unity-project\.git\HEAD", true)]
    [InlineData(@"unity-project\.git\refs\heads\week01\solution", true)]
    [InlineData(@"unity-project\.git\packed-refs", true)]
    [InlineData(@"unity-project", true)]
    [InlineData(@"unity-project\.git\index", false)]
    [InlineData(@"unity-project\.git\objects\ab\cdef", false)]
    [InlineData(@"unity-project\.git", false)]
    [InlineData(@"unity-project\Library\ArtifactDB", false)]
    [InlineData(@"unity-project\Temp\UnityLockfile", false)]
    [InlineData(@"unity-project\Logs\shadercompiler.log", false)]
    [InlineData(@"webapi-project\src\Api\obj\project.assets.json", false)]
    [InlineData(@"webapi-project\src\Api\bin\Debug\Api.dll", false)]
    [InlineData(@"unity-project\.vs\slnx.sqlite", false)]
    public void Filters_noise(string path, bool relevant) => Assert.Equal(relevant, ProjectWatcher.IsRelevant(path));

    [Fact]
    public async Task Raises_one_debounced_event_for_a_burst_and_ignores_library()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ex-tests", "watch-" + Guid.NewGuid().ToString("N")[..8]);
        var repo = Directory.CreateDirectory(Path.Combine(folder, "unity-project")).FullName;
        Directory.CreateDirectory(Path.Combine(repo, "Library"));
        try
        {
            var count = 0;
            using var watcher = new ProjectWatcher(folder, TimeSpan.FromMilliseconds(200));
            watcher.Changed += () => Interlocked.Increment(ref count);

            for (var i = 0; i < 20; i++)
                File.WriteAllText(Path.Combine(repo, "Library", $"cache{i}.bin"), "x");
            await Task.Delay(800);
            Assert.Equal(0, count);

            for (var i = 0; i < 20; i++)
                File.WriteAllText(Path.Combine(repo, $"Script{i}.cs"), "x");
            await Task.Delay(1000);
            Assert.Equal(1, count);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
