using System.Xml.Linq;

namespace DataBridge.ArchitectureTests;

public sealed class ProjectReferenceTests
{
    public static TheoryData<string> SourceProjects => [.. DependencyRules.AllowedProjectReferences.Keys];

    public static TheoryData<string> InnerLayers => [.. DependencyRules.InnerLayers];

    public static TheoryData<string> NonSourceProjects => [.. DependencyRules.AllowedTestProjectReferences.Keys];

    [Fact]
    public void Every_source_project_has_a_dependency_rule()
    {
        var unknown = RepositoryLayout.SourceProjectNames()
            .Where(name => !DependencyRules.AllowedProjectReferences.ContainsKey(name))
            .ToArray();

        Assert.True(unknown.Length == 0, $"Add dependency rules for: {string.Join(", ", unknown)}");
    }

    [Theory]
    [MemberData(nameof(SourceProjects))]
    public void Source_project_references_only_allowed_projects(string projectName)
    {
        var project = RepositoryLayout.LoadSourceProject(projectName);
        var allowed = DependencyRules.AllowedProjectReferences[projectName];

        var violations = project.ProjectReferences
            .Where(reference => !allowed.Contains(reference, StringComparer.Ordinal))
            .ToArray();

        Assert.True(violations.Length == 0, $"{projectName} must not reference: {string.Join(", ", violations)}");
    }

    [Theory]
    [MemberData(nameof(InnerLayers))]
    public void Inner_layer_does_not_reference_infrastructure_packages(string projectName)
    {
        var project = RepositoryLayout.LoadSourceProject(projectName);

        var violations = project.PackageReferences
            .Where(DependencyRules.IsInfrastructurePackage)
            .ToArray();

        Assert.True(violations.Length == 0, $"{projectName} must not reference: {string.Join(", ", violations)}");
    }

    [Fact]
    public void Shared_references_the_base_class_library_only()
    {
        var project = RepositoryLayout.LoadSourceProject(DependencyRules.Shared);

        Assert.Empty(project.ProjectReferences);
        Assert.Empty(project.PackageReferences);
    }

    [Theory]
    [MemberData(nameof(InnerLayers))]
    public void Inner_layer_resolved_package_graph_has_no_infrastructure_packages(string projectName)
    {
        var violations = RepositoryLayout.ResolvedPackageNames(projectName)
            .Where(DependencyRules.IsInfrastructurePackage)
            .ToArray();

        Assert.True(violations.Length == 0, $"{projectName} resolves: {string.Join(", ", violations)}");
    }

    [Fact]
    public void Every_test_and_benchmark_project_has_a_dependency_rule()
    {
        var unknown = RepositoryLayout.NonSourceProjectNames()
            .Where(name => !DependencyRules.AllowedTestProjectReferences.ContainsKey(name))
            .ToArray();

        Assert.True(unknown.Length == 0, $"Add dependency rules for: {string.Join(", ", unknown)}");
    }

    [Theory]
    [MemberData(nameof(NonSourceProjects))]
    public void Test_or_benchmark_project_references_only_the_projects_it_covers(string projectName)
    {
        var project = RepositoryLayout.LoadNonSourceProject(projectName);
        var allowed = DependencyRules.AllowedTestProjectReferences[projectName];

        var violations = project.ProjectReferences
            .Where(reference => !allowed.Contains(reference, StringComparer.Ordinal))
            .ToArray();

        Assert.True(violations.Length == 0, $"{projectName} must not reference: {string.Join(", ", violations)}");
    }

    [Fact]
    public void Build_files_imported_by_source_projects_add_no_references()
    {
        var violations = RepositoryLayout.SourceBuildFiles()
            .Where(path => XDocument.Load(path).Descendants()
                .Any(element => element.Name.LocalName is "PackageReference" or "ProjectReference"))
            .ToArray();

        Assert.True(violations.Length == 0, $"Move references out of: {string.Join(", ", violations)}");
    }
}
