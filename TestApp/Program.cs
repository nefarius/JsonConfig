using System;
using JsonConfig;

namespace TestApp
{
    /// <summary>
    ///     This is here because the NUnit project is legacy and broken at the moment :(
    /// </summary>

    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine(Config.Global.Foo);
        }
    }
}
