using System;
using System.Reflection;
using TelemetryMonitor;

// An isolated test host advances the scheduler through reflection; no production flush API exists.
internal static class ConfigurationTestHost
{
    private static int Main(string[] args)
    {
        using(var server=new Server(args[0],true,Int32.Parse(args[1])))
        {
            server.Start();Console.WriteLine("READY");string line;
            while((line=Console.ReadLine())!=null&&line!="quit")
            {
                if(line!="tick")continue;
                typeof(Server).GetField("configurationNextTick",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(server,0d);
                typeof(Server).GetMethod("ConfigurationScheduleTick",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(server,new object[]{null});
                Console.WriteLine("TICK");
            }
        }
        return 0;
    }
}
