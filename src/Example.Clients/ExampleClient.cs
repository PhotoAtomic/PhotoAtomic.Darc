using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Example.Clients
{
    static public class ExampleClient
    {
        extension(IHostBuilder builder)
        {
            public IHostBuilder UseExampleClient()
            {
                return builder.UseOrleansClient(client =>
                {
                    client.UseTransactions();
                    client.UseLocalhostClustering();
                })
                .ConfigureLogging(logging => logging.AddConsole());
            }
        }
    }
}
