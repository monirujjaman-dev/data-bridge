using System.Reflection;

namespace DataBridge.ArchitectureTests;

/// <summary>
/// Checks the compiled assemblies as a second line of defence: a forbidden dependency can arrive
/// transitively through a package even when no project file names it.
/// </summary>
public sealed class AssemblyReferenceTests
{
    public static TheoryData<string> CompiledLayers =>
    [
        DependencyRules.Shared,
        DependencyRules.Core,
        DependencyRules.Application,
        DependencyRules.Infrastructure,
    ];

    public static TheoryData<string> InnerLayers => [.. DependencyRules.InnerLayers];

    [Theory]
    [MemberData(nameof(CompiledLayers))]
    public void Assembly_references_only_allowed_DataBridge_assemblies(string assemblyName)
    {
        var allowed = DependencyRules.AllowedProjectReferences[assemblyName];

        var violations = ReferencedAssemblyNames(assemblyName)
            .Where(name => name.StartsWith("DataBridge.", StringComparison.Ordinal))
            .Where(name => !allowed.Contains(name, StringComparer.Ordinal))
            .ToArray();

        Assert.True(violations.Length == 0, $"{assemblyName} must not reference: {string.Join(", ", violations)}");
    }

    [Theory]
    [MemberData(nameof(InnerLayers))]
    public void Inner_layer_assembly_does_not_reference_infrastructure_assemblies(string assemblyName)
    {
        var violations = ReferencedAssemblyNames(assemblyName)
            .Where(DependencyRules.IsInfrastructurePackage)
            .ToArray();

        Assert.True(violations.Length == 0, $"{assemblyName} must not reference: {string.Join(", ", violations)}");
    }

    private static string[] ReferencedAssemblyNames(string assemblyName) =>
        [.. Assembly.Load(new AssemblyName(assemblyName))
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .OfType<string>()];
}
