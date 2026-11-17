using System;
using System.Collections.Generic;
using System.IO;
using TelemetryMonitor;

internal static class LargeJsonTestHost
{
    private static int Main(string[] args)
    {
        string text=new string('x',9*1024*1024);int failed=0;
        try { var json=JsonFile.Encode(new Dictionary<string,string>{{"value",text}});if(json.Length<=8*1024*1024)throw new Exception("Fixture is too small");Console.WriteLine("PASS encode above 8MiB"); }
        catch(Exception ex){Console.WriteLine("FAIL encode: "+ex.Message);failed++;}
        try { var value=JsonFile.Decode<Dictionary<string,string>>("{\"value\":\""+text+"\"}");if(value["value"]!=text)throw new Exception("Data changed");Console.WriteLine("PASS decode above 8MiB"); }
        catch(Exception ex){Console.WriteLine("FAIL decode: "+ex.Message);failed++;}
        if(failed==0){string file=Path.Combine(args[0],"large-json-roundtrip.json");JsonFile.Save(file,new Dictionary<string,string>{{"value",text}});if(JsonFile.Load<Dictionary<string,string>>(file)["value"]!=text)throw new Exception("Saved data changed");Console.WriteLine("PASS save/load above 8MiB");}
        return failed==0?0:1;
    }
}
