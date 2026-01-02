using Example.Grains.Interfaces;
using Orleans.TestingHost;
using System;
using System.Collections.Generic;
using System.Text;

namespace PhotoAtomic.Darc.Test
{
    public class OrleansTestClusterTest
    {
        [Fact]
        public async Task SaysHelloCorrectly()
        {
            var builder = new TestClusterBuilder();
            var cluster = builder.Build();
            cluster.Deploy();

            var hello = cluster.GrainFactory.GetGrain<IGreetingsGrain>(Guid.NewGuid());
            var greeting = await hello.SayHello("Duke");

            cluster.StopAllSilos();

            Assert.Equal("Hello, Duke!", greeting);
        }
    }
}
