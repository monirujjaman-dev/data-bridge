using System.Text.Json;
using System.Xml.Linq;

namespace DataBridge.ArchitectureTests;

/// <summary>
/// Locates the repository on disk and reads project files, so dependency rules are checked
/// against the declared references rather than only against what the compiler emitted.
/// </summary>
internal static class RepositoryLayout
{
    private const string SolutionFileName = "DataBridge.sln";

    private static readonly string[] _buildFileNames = ["Directory.Build.props", "Directory.Build.targets"];

    private static readonly Lazy<DirectoryInfo> _root = new(FindRoot);

    public static DirectoryInfo Root => _root.Value;

    public static string SourceDirectory => Path.Combine(Root.FullName, "src");

    /// <summary>Directories that hold test and benchmark projects.</summary>
    public static IReadOnlyList<string> NonSourceDirectories =>
        [Path.Combine(Root.FullName, "tests"), Path.Combine(Root.FullName, "benchmarks")];

    public static IReadOnlyList<string> SourceProjectNames() => [.. ProjectNames(SourceDirectory)];

    public static IReadOnlyList<string> NonSourceProjectNames() =>
        [.. NonSourceDirectories.SelectMany(ProjectNames).Order(StringComparer.Ordinal)];

    public static ProjectFile LoadSourceProject(string projectName) =>
        ProjectFile.Load(ProjectPath(SourceDirectory, projectName));

    public static ProjectFile LoadNonSourceProject(string projectName) =>
        ProjectFile.Load(NonSourceDirectories
            .Select(directory => ProjectPath(directory, projectName))
            .Single(File.Exists));

    /// <summary>
    /// Every package in the restored dependency graph of a source project, including transitive
    /// packages that no project file names. Requires the project to have been restored.
    /// </summary>
    public static IReadOnlyList<string> ResolvedPackageNames(string projectName)
    {
        var assetsPath = Path.Combine(SourceDirectory, projectName, "obj", "project.assets.json");
        using var assets = JsonDocument.Parse(File.ReadAllText(assetsPath));

        return [.. assets.RootElement.GetProperty("libraries").EnumerateObject()
            .Where(library => library.Value.GetProperty("type").GetString() == "package")
            .Select(library => library.Name.Split('/')[0])];
    }

    /// <summary>
    /// <c>Directory.Build.props</c> and <c>Directory.Build.targets</c> files that MSBuild imports
    /// into source projects: those at the root and anywhere under <c>src</c>.
    /// </summary>
    public static IReadOnlyList<string> SourceBuildFiles() =>
        [.. _buildFileNames
            .Select(name => Path.Combine(Root.FullName, name))
            .Where(File.Exists)
            .Concat(Directory.EnumerateFiles(SourceDirectory, "Directory.Build.*", SearchOption.AllDirectories))];

    private static string ProjectPath(string directory, string projectName) =>
        Path.Combine(directory, projectName, projectName + ".csproj");

    private static IEnumerable<string> ProjectNames(string directory) =>
        Directory.EnumerateFiles(directory, "*.csproj", SearchOption.AllDirectories)
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Order(StringComparer.Ordinal);

    private static DirectoryInfo FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
            {
                return directory;
            }
        }

        throw new InvalidOperationException(
            $"Could not find {SolutionFileName} in any parent of {AppContext.BaseDirectory}.");
    }
}

/// <summary>The references declared in one project file.</summary>
internal sealed record ProjectFile(
    string FilePath,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<string> PackageReferences)
{
    public static ProjectFile Load(string filePath)
    {
        var document = XDocument.Load(filePath);

        return new ProjectFile(
            filePath,
            [.. IncludeValues(document, "ProjectReference")
                .Select(include => Path.GetFileNameWithoutExtension(include.Replace('\\', '/')))],
            [.. IncludeValues(document, "PackageReference")]);
    }

    private static IEnumerable<string> IncludeValues(XDocument document, string itemName) =>
        document.Descendants()
            .Where(element => element.Name.LocalName == itemName)
            .Select(element => (string?)element.Attribute("Include"))
            .OfType<string>();
}
