using System;
using System.Linq;
using TelemetryMonitor;
public static class SendDefaultsTest
{
    private static int checks;
    private static void Check(bool ok,string label){if(!ok)throw new Exception(label);checks++;Console.WriteLine("PASS "+label);}
    public static int Main()
    {
        var expected=new[]{"192.168.1.111","192.168.1.112","192.168.1.113","192.168.1.137"};
        var fresh=new SendMonitorSettings();fresh.Validate();Check(fresh.DestinationIPs.SequenceEqual(expected)&&fresh.DestinationIP==expected[0],"fresh defaults include all four destinations in requested order");
        var missing=JsonFile.Decode<SendMonitorSettings>("{}");missing.Validate();Check(missing.DestinationIPs.SequenceEqual(expected),"missing destination fields use new defaults");
        var legacy=JsonFile.Decode<SendMonitorSettings>("{\"SourceIP\":\"127.0.0.1\",\"DestinationIP\":\"127.0.0.2\"}");legacy.Validate();Check(legacy.DestinationIPs.SequenceEqual(new[]{"127.0.0.2"}),"legacy saved single destination remains unchanged");
        var multiple=JsonFile.Decode<SendMonitorSettings>("{\"DestinationIP\":\"192.168.1.113\",\"DestinationIPs\":[\"192.168.1.112\",\"192.168.1.111\"]}");multiple.Validate();Check(multiple.DestinationIPs.SequenceEqual(new[]{"192.168.1.112","192.168.1.111"}),"explicit saved destination list retains priority and order");
        var restored=JsonFile.Decode<SendMonitorSettings>(JsonFile.Encode(fresh));restored.Validate();Check(restored.DestinationIPs.SequenceEqual(expected),"default destination list survives save and reload");
        bool rejected=false;try{var blank=new SendMonitorSettings{DestinationIP=""};blank.Validate();}catch(ArgumentException){rejected=true;}Check(rejected,"explicit blank destination still reports an error");
        var routes=new CommandSendConfiguration().Routes;Check(routes.Select(r=>r.DestinationIP).SequenceEqual(expected)&&routes.Single(r=>r.DestinationIP=="192.168.1.137").SourcePort==8080&&routes.Single(r=>r.DestinationIP=="192.168.1.137").DestinationPort==8080,"137 uses source port 8080 and destination port 8080");
        Check(routes[0].SourcePort==53333&&routes[0].DestinationPort==31000&&routes[1].SourcePort==53335&&routes[1].DestinationPort==31000&&routes[2].SourcePort==53334&&routes[2].DestinationPort==31000,"111, 112 and 113 retain their existing port pairs");
        Console.WriteLine("Completed "+checks+" default/compatibility checks; no network traffic sent.");return 0;
    }
}
