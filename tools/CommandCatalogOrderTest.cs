using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using TelemetryMonitor;

public static class CommandCatalogOrderTest
{
    private static string[] Codes(ObservedCommands store,int page,string kind="",string search="")
    {
        var result=JsonFile.Decode<Dictionary<string,object>>(JsonFile.Encode(store.CatalogPage(page,kind,search)));
        return ((IEnumerable)result["rows"]).Cast<Dictionary<string,object>>().Select(r=>(string)r["code"]).ToArray();
    }
    private static int checks;
    private static void Check(bool ok,string message)
    {if(!ok)throw new Exception(message);checks++;Console.WriteLine("PASS "+message);}
    public static int Main(string[] args)
    {
        string root=Path.Combine(args[0],"test-output","command-import-order","runtime-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store=new ObservedCommands(root);
            var rows=new List<string>();int payload=128;
            Action<string,string,string> add=(code,kind,ip)=>rows.Add(String.Join(",",new[]{code,"ReverseName"+(1000-payload),kind,"192.168.1.16",ip,(payload++).ToString("X4")}));
            add("BC10000","BC","192.168.1.113");
            add("PSS1000","PSS","192.168.1.111");
            add("BC9999","BC","192.168.1.113");
            add("PSS999","PSS","192.168.1.111");
            add("KC0001","KC","192.168.1.137");
            add("PSS002","PSS","192.168.1.111");
            add("","未分类","192.168.1.114");
            for(int n=60;n>=1;n--)add("BC"+n.ToString("D4"),"BC","192.168.1.113");
            var csv="指令编号,指令名称,指令类别,来源IP,目的IP,完整UDP载荷HEX\r\n"+String.Join("\r\n",rows.ToArray())+"\r\n";
            var plan=store.PreviewCsv(Encoding.UTF8.GetBytes(csv));
            Check(plan.Errors.Count==0,String.Join(";",plan.Errors.ToArray())+" shuffled import is valid");store.ApplyCsv(plan);
            var expected=Enumerable.Range(1,60).Select(n=>"BC"+n.ToString("D4")).Concat(new[]{"BC9999","BC10000","KC0001","PSS002","PSS999","PSS1000",""}).ToArray();
            Check(Codes(store,1).SequenceEqual(expected.Take(50)),"first page follows command code rather than name or import order");
            Check(Codes(store,2).SequenceEqual(expected.Skip(50)),"global ordering continues correctly across page boundaries");
            Check(Codes(store,1,"PSS").SequenceEqual(new[]{"PSS002","PSS999","PSS1000"}),"numeric suffixes retain correct order when their digit counts grow");
            Check(Codes(store,1,"BC","BC00").SequenceEqual(expected.Take(50)),"search and category filters keep code ordering");
            Check(Codes(store,1,"BC","999").SequenceEqual(new[]{"BC9999"}),"search still selects the requested command");
            string path=Path.Combine(root,"config","observed-commands.json");var before=File.ReadAllBytes(path);
            Codes(store,1);Codes(store,2);Check(before.SequenceEqual(File.ReadAllBytes(path)),"sorting does not rewrite saved names, codes, payloads or bindings");
            var restarted=new ObservedCommands(root);
            Check(Codes(restarted,1).SequenceEqual(expected.Take(50)) && Codes(restarted,2).SequenceEqual(expected.Skip(50)),"ordering remains consistent after restart");
            Console.WriteLine("All "+checks+" command ordering checks passed.");return 0;
        }
        catch(Exception e){Console.WriteLine(e);return 1;}
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
