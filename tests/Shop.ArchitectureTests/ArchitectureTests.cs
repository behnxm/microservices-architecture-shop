using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Shop.Contracts;
using Shouldly;
using Xunit;

namespace Shop.ArchitectureTests;

public sealed class ArchitectureTests
{
    private static readonly string SolutionRoot = FindSolutionRoot();

    private static string FindSolutionRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "Shop.slnx")) || File.Exists(Path.Combine(dir, "Shop.sln")))
            {
                return dir;
            }
            dir = Directory.GetParent(dir)?.FullName;
        }
        return Directory.GetCurrentDirectory();
    }

    [Fact]
    public void Contracts_Assembly_Should_Have_No_External_Dependencies()
    {
        var contractsAssembly = typeof(IntegrationEventAttribute).Assembly;
        var referencedAssemblies = contractsAssembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => !name.StartsWith("System", StringComparison.OrdinalIgnoreCase)
                           && !name.StartsWith("Microsoft.CSharp", StringComparison.OrdinalIgnoreCase)
                           && !name.Equals("mscorlib", StringComparison.OrdinalIgnoreCase))
            .ToList();

        referencedAssemblies.ShouldBeEmpty("Contracts library must have zero external dependencies.");
    }

    [Fact]
    public void All_Contracts_Must_Be_Sealed_Records_With_Valid_Attribute()
    {
        var contractTypes = typeof(IntegrationEventAttribute).Assembly
            .GetExportedTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(Attribute).IsAssignableFrom(t) == false)
            .ToList();

        contractTypes.ShouldNotBeEmpty();

        foreach (var type in contractTypes)
        {
            type.IsSealed.ShouldBeTrue($"Type '{type.Name}' in Contracts must be sealed.");

            // Verify record type (C# records generate an equality contract method)
            var isRecord = type.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Any(m => m.Name == "<Clone>$" || m.Name == "PrintMembers");
            isRecord.ShouldBeTrue($"Type '{type.Name}' in Contracts must be an immutable record.");

            if (type.Name.EndsWith("IntegrationEvent", StringComparison.Ordinal))
            {
                var attr = type.GetCustomAttribute<IntegrationEventAttribute>();
                attr.ShouldNotBeNull($"Integration event '{type.Name}' must be decorated with [IntegrationEvent].");
                attr.ContractName.ShouldNotBeNullOrWhiteSpace();

                // Strict naming pattern check: e.g. ordering.order-placed.v1
                Regex.IsMatch(attr.ContractName, @"^[a-z]+(\.[a-z-]+)*\.v\d+$")
                    .ShouldBeTrue($"Contract name '{attr.ContractName}' on '{type.Name}' does not match pattern '<domain>.<event>.v<number>'.");
            }
        }
    }

    [Fact]
    public void Contract_Names_Must_Be_Unique()
    {
        var events = typeof(IntegrationEventAttribute).Assembly
            .GetExportedTypes()
            .Select(t => t.GetCustomAttribute<IntegrationEventAttribute>())
            .Where(a => a is not null)
            .Select(a => a!.ContractName)
            .ToList();

        var duplicates = events.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        duplicates.ShouldBeEmpty($"Found duplicate contract names: {string.Join(", ", duplicates)}");
    }

    [Fact]
    public void Every_Contract_In_Code_Must_Appear_In_AsyncApi_Document()
    {
        var asyncApiPath = Path.Combine(SolutionRoot, "docs", "asyncapi.yaml");
        File.Exists(asyncApiPath).ShouldBeTrue($"AsyncAPI documentation was not found at {asyncApiPath}");

        var asyncApiContent = File.ReadAllText(asyncApiPath);

        var eventContracts = typeof(IntegrationEventAttribute).Assembly
            .GetExportedTypes()
            .Select(t => t.GetCustomAttribute<IntegrationEventAttribute>())
            .Where(a => a is not null)
            .Select(a => a!.ContractName)
            .ToList();

        foreach (var contract in eventContracts)
        {
            asyncApiContent.Contains(contract, StringComparison.OrdinalIgnoreCase)
                .ShouldBeTrue($"Contract '{contract}' was found in code but is missing from docs/asyncapi.yaml.");
        }
    }

    [Fact]
    public void Gateway_Should_Not_Reference_Any_Service_Project()
    {
        var gatewayCsproj = Path.Combine(SolutionRoot, "src", "Shop.Gateway", "Shop.Gateway.csproj");
        File.Exists(gatewayCsproj).ShouldBeTrue();

        var doc = XDocument.Load(gatewayCsproj);
        var projectReferences = doc.Descendants("ProjectReference")
            .Select(e => (string?)e.Attribute("Include") ?? string.Empty)
            .ToList();

        foreach (var pref in projectReferences)
        {
            pref.Contains("Catalog", StringComparison.OrdinalIgnoreCase)
                .ShouldBeFalse("Gateway must not reference Shop.Catalog directly.");
            pref.Contains("Ordering", StringComparison.OrdinalIgnoreCase)
                .ShouldBeFalse("Gateway must not reference Shop.Ordering directly.");
            pref.Contains("Notifications", StringComparison.OrdinalIgnoreCase)
                .ShouldBeFalse("Gateway must not reference Shop.Notifications directly.");
        }
    }

    [Fact]
    public void Service_Projects_Should_Not_Reference_Other_Service_Projects()
    {
        var srcDir = Path.Combine(SolutionRoot, "src");
        var csprojFiles = Directory.GetFiles(srcDir, "*.csproj", SearchOption.AllDirectories)
            .Where(f => !f.Contains("Shop.AppHost"))
            .ToList();

        foreach (var file in csprojFiles)
        {
            var fileName = Path.GetFileName(file);
            var doc = XDocument.Load(file);
            var refs = doc.Descendants("ProjectReference")
                .Select(e => (string?)e.Attribute("Include") ?? string.Empty)
                .ToList();

            if (fileName.Contains("Catalog"))
            {
                refs.Any(r => r.Contains("Ordering") || r.Contains("Notifications"))
                    .ShouldBeFalse($"Catalog service ({fileName}) must not reference other services.");
            }
            else if (fileName.Contains("Ordering"))
            {
                refs.Any(r => r.Contains("Catalog") || r.Contains("Notifications"))
                    .ShouldBeFalse($"Ordering service ({fileName}) must not reference other services.");
            }
            else if (fileName.Contains("Notifications"))
            {
                refs.Any(r => r.Contains("Catalog") || r.Contains("Ordering"))
                    .ShouldBeFalse($"Notifications service ({fileName}) must not reference other services.");
            }
        }
    }

    [Fact]
    public void Only_Messaging_BuildingBlock_Should_Reference_RabbitMQ_Client()
    {
        var srcDir = Path.Combine(SolutionRoot, "src");
        var csprojFiles = Directory.GetFiles(srcDir, "*.csproj", SearchOption.AllDirectories)
            .Where(f => !f.Contains("Shop.AppHost"))
            .ToList();

        foreach (var file in csprojFiles)
        {
            var fileName = Path.GetFileName(file);
            var doc = XDocument.Load(file);
            var hasRabbitMqClient = doc.Descendants("PackageReference")
                .Any(e => ((string?)e.Attribute("Include") ?? string.Empty).Equals("RabbitMQ.Client", StringComparison.OrdinalIgnoreCase));

            if (hasRabbitMqClient)
            {
                fileName.ShouldBe("Shop.BuildingBlocks.Messaging.csproj",
                    $"Only Shop.BuildingBlocks.Messaging may reference RabbitMQ.Client directly. Found violation in {fileName}.");
            }
        }
    }
}
