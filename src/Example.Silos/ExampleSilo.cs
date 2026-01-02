using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Hosting;

namespace Example.Silos
{
    public static class ExampleSilo
    {
        extension(IHostBuilder builder)
        {
            public IHostBuilder UseExampleSilo()
            {
                return builder.UseOrleans(silo =>
                {
                    silo.UseTransactions();
                    silo.UseLocalhostClustering()
                        .ConfigureLogging(logging => logging.AddConsole());
                });
            }
        }
    }
}
