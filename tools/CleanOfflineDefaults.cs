using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using TelemetryMonitor;

internal static class CleanOfflineDefaults
{
    public static int Main(string[] args)
    {
        try
        {
            if(args.Length!=1)throw new ArgumentException("Specify a new empty staging directory.");
            string root=Path.GetFullPath(args[0]);
            if(Directory.Exists(root)&&Directory.GetFileSystemEntries(root).Length!=0)
                throw new InvalidOperationException("Clean defaults can only be written to an empty staging directory.");
            string config=Path.Combine(root,"config");Directory.CreateDirectory(config);
            var json=new JavaScriptSerializer{MaxJsonLength=Int32.MaxValue};
            Action<string,object> save=(name,value)=>File.WriteAllText(Path.Combine(config,name),json.Serialize(value),new UTF8Encoding(false));
            var settings=new Settings{UdpPort=30008,MulticastGroup="224.10.2.24",
                SourceIPs=new[]{"192.168.1.111","192.168.1.112","192.168.1.113"}};
            settings.Validate();
            save("server.json",settings);
            save("parameters.json",new List<Parameter>());
            save("decoder.json",new DecoderConfiguration{Revision=Guid.NewGuid().ToString("N")});
            save("continuous-rules.json",new ContinuousRuleConfig{Revision=Guid.NewGuid().ToString("N")});
            save("tc-bindings.json",new TCBindingConfig{Revision=Guid.NewGuid().ToString("N")});
            save("sequences.json",new SequenceConfiguration{Revision=Guid.NewGuid().ToString("N")});
            save("observed-commands.json",new ObservedCommandConfiguration());
            save("history-storage.json",new HistoryStorageConfiguration{ArchiveEnabled=false});
            Console.WriteLine("Generated empty offline configuration at "+root);
            return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error.Message);return 1;}
    }
}
