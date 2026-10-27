using System.Linq;
using System.Threading.Tasks;
using Aspire.Hosting.Testing;
using Shouldly;
using Xunit;

namespace Shop.EndToEndTests;

public sealed class AppHostConfigurationTests
{
    [Fact]
    public async Task AppHost_Registers_All_Required_Distributed_Resources()
    {
        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.Shop_AppHost>();

        var resourceNames = appHost.Resources.Select(r => r.Name).ToList();

        resourceNames.ShouldContain("postgres");
        resourceNames.ShouldContain("rabbitmq");
        resourceNames.ShouldContain("mailpit");
        resourceNames.ShouldContain("catalog-api");
        resourceNames.ShouldContain("ordering-api");
        resourceNames.ShouldContain("notifications-worker");
        resourceNames.ShouldContain("gateway");
    }
}
