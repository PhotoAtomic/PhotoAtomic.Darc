using Example.Clients;
using Example.Grains.Interfaces;
using Example.Silos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Serialization.WireProtocol;

namespace PhotoAtomic.Darc.Test
{
    public class ExampleTest
    {
        [Fact]
        public async Task CreatingSiloAndClientAndInvokingASimpleGrainMethod()
        {
            var hostBuilder = new HostBuilder();
            var builder = hostBuilder.UseExampleSilo();//.UseExampleClient();
            var host = hostBuilder.Build();
            await host.StartAsync();

            IClusterClient client = host.Services.GetRequiredService<IClusterClient>();

            var id = Guid.NewGuid();
            IGreetingsGrain greetingGenerator = client.GetGrain<IGreetingsGrain>(id);
            string response = await greetingGenerator.SayHello("Duke");
            Assert.Equal("Hello, Duke!", response);

            await host.StopAsync();
        }
    }
}
