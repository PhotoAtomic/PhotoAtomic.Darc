using System;
using System.Collections.Generic;
using System.Text;

namespace Example.Grains.Interfaces
{
    public interface IGreetingsGrain : IGrainWithGuidKey
    {
        ValueTask<string> SayHello(string toName);
    }
}
