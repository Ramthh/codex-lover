using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Runtime.CompilerServices;

namespace CodexAccountMonitor.Tests;

public sealed class XamlResourceContractTests
{
    [Fact]
    public void DynamicResourcesAreNotAssignedToBindingConvertersOrStyleInheritance()
    {
        // These are CLR properties, not dependency properties. The WPF compiler
        // accepts the markup but the deferred account template fails at runtime.
        // Build outputs may deliberately live outside the checkout (ArtifactsPath).
        // CallerFilePath identifies the source tree used to compile this test.
        var directory = new DirectoryInfo(Path.GetDirectoryName(SourceFile())!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CodexAccountMonitor.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);

        var violations = new List<string>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(directory.FullName, "src", "CodexAccountMonitor"), "*.xaml", SearchOption.AllDirectories))
        {
            var document = XDocument.Load(path, LoadOptions.SetLineInfo);
            foreach (var attribute in document.Descendants().Attributes())
            {
                if (Regex.IsMatch(attribute.Value, @"\bConverter\s*=\s*\{DynamicResource\b")
                    || (attribute.Name.LocalName == "BasedOn" && attribute.Value.Contains("{DynamicResource", StringComparison.Ordinal)))
                    violations.Add($"{Path.GetFileName(path)}: {attribute.Name}={attribute.Value}");
            }
        }

        Assert.True(violations.Count == 0, "Use StaticResource for CLR resource properties:\n" + string.Join("\n", violations));
    }

    private static string SourceFile([CallerFilePath] string path = "") => path;
}
