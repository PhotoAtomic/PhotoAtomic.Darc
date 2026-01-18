using Google.Protobuf.WellKnownTypes;
using System;
using System.Collections.Generic;
using System.Text;
using Testcontainers.KurrentDb;
using KurrentDB.Client;

namespace PhotoAtomic.Darc.Test
{
    public class ContainerizedPersistenceTest
    {
        //[Fact] DISABLED unti kurrent db testcontainer supports multi stream transactions
        public async Task SaveOneEventOutsideTransaction_Expected_EventPersisted()
        {
            var kurrentDbContainer = new KurrentDbBuilder("kurrentplatform/kurrentdb:latest")              
              .Build();
            await kurrentDbContainer.StartAsync();
            var connectionString = kurrentDbContainer.GetConnectionString();

            var client = new KurrentDBClient(KurrentDBClientSettings.Create(connectionString));

            AppendStreamRequest[] requests = [
                new(
                    "A",
                    StreamState.Any,
                    [
                        new EventData(Uuid.NewUuid(), "EventType1", Encoding.UTF8.GetBytes("{\"orderId\": \"21345\", \"amount\": 99.99}"))
                    ]
                ),
                new(
                    "A",
                    StreamState.Any,
                    [
                        new EventData(Uuid.NewUuid(), "EventType1", Encoding.UTF8.GetBytes("{\"itemId\": \"abc123\", \"quantity\": 2}"))
                    ]
                )
            ];

            var response = await client.MultiStreamAppendAsync(requests);


        }
    }
}
