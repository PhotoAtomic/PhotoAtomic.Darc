using Example.Grains.Interfaces;
using System;
using System.Collections.Generic;
using System.Text;

namespace Example.Grains
{
    public class GreetingsGrain : IGreetingsGrain
    {
        //public GreetingsGrain(IPersistentState<string> state)
        //{
        //}

        public async ValueTask<string> SayHello(string toName)
        {
            if (string.IsNullOrWhiteSpace(toName))
            {
                return $"Hello unknown traveller.";
            }
            else
            {
                return $"Hello, {toName}!";
            }
        }
    }
}
