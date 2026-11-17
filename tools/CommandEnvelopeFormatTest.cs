using System;
using System.IO;
using System.Linq;
using System.Text;
using TelemetryMonitor;

internal static class CommandEnvelopeFormatTest
{
    private static int checks;
    private static void Check(bool value,string text){if(!value)throw new Exception(text);checks++;Console.WriteLine("PASS "+text);}
    private static string Hex(byte[] value){return BitConverter.ToString(value).Replace("-","");}
    private static byte[] Sum(byte[] value){uint sum=0;for(int i=0;i<value.Length-4;i++)sum+=value[i];for(int i=0;i<4;i++)value[value.Length-4+i]=(byte)(sum>>(8*i));return value;}
    public static int Main(string[] args)
    {
        string parent=Path.GetFullPath(Path.Combine(args[0],"test-output"))+Path.DirectorySeparatorChar;
        string root=Path.GetFullPath(Path.Combine(parent,"envelope-format-"+Guid.NewGuid().ToString("N")));
        if(!root.StartsWith(parent,StringComparison.OrdinalIgnoreCase))throw new Exception("Invalid test directory");
        try
        {
            byte[] b=PacketCodec.Hex("EB90EB9030000000AA000100000001000000000053152000000011011A08C000001130020A0000000000010800010D370D3707366A060000");
            byte[] broadcast=PacketCodec.Hex("EB90EB90300000000700010000000100000000005315200000001F011A70C000001130020A00000000000101000100050005DB7ED4060000");
            Check(CommandEnvelope.Valid(b)&&CommandEnvelope.Valid(broadcast),"original B sample and corrected single-packet broadcast retain valid outer sums");
            Check(CommandEnvelope.BuildBC(b.Skip(28).Take(24).ToArray(),false).SequenceEqual(b),"B workbook builder retains the user's exact original UDP bytes");
            Check(CommandEnvelope.BuildBC(broadcast.Skip(28).Take(24).ToArray(),true).SequenceEqual(broadcast),"broadcast workbook builder uses little-endian total=1 and sequence=1");
            Check(Hex(CommandEnvelope.DataContent(b))=="1101"+Hex(b.Skip(28).Take(24).ToArray()),"B data area begins with 11 01 after the complete four-byte interface");
            Check(Hex(CommandEnvelope.DataContent(broadcast))=="1F01"+Hex(broadcast.Skip(28).Take(24).ToArray()),"broadcast data area begins with 1F 01");
            byte[] bin=PacketCodec.Hex("1B08C000009730028300000000000108120001666666662200");
            byte[] injection=CommandEnvelope.BuildInjection(bin);
            Check(Hex(injection.Take(28).ToArray())=="EB90EB90"+Hex(BitConverter.GetBytes((uint)(bin.Length+24)))+"AA00010000000100000000005315200000001104","injection wrapper retains the established interface and routing bytes");
            Check(CommandEnvelope.Valid(injection)&&Hex(CommandEnvelope.DataContent(injection))=="1104"+Hex(bin),"injection BIN remains byte-exact and preview excludes all interface bytes");
            var stc=CommandEnvelope.BuildSTC(221,92,PacketCodec.Hex("AA39"));
            Check(Hex(stc)=="EB90EB9018000000AA0001000000010000000000DD005C000000AA39D6050000","XML STC builder matches the independently confirmed 32-byte sample");
            var store=new ObservedCommands(root);
            string csv="指令编号,指令名称,指令类别,来源IP,目的IP,完整UDP载荷HEX\r\nBC3383,B,BC,192.168.1.16,192.168.1.113,"+Hex(b)+"\r\nBC0005,广播,BC,192.168.1.16,192.168.1.112;192.168.1.113,"+Hex(broadcast)+"\r\n";
            var plan=store.PreviewCsv(Encoding.UTF8.GetBytes(csv));Check(plan.Errors.Count==0,"legacy six-column B/broadcast library still imports");store.ApplyCsv(plan);
            var writer=new StringWriter();store.ExportCsv(writer);Check(writer.ToString().Split('\n')[0].Split(',').Length==6,"non-STC libraries retain six-column CSV exports");
            string file=Path.Combine(root,"config","observed-commands.json"),before=File.ReadAllText(file);
            var changed=(byte[])b.Clone();changed[8]=0xFE;changed[9]=0xFF;changed[22]=1;changed[23]=0xEF;changed[24]=0xCD;changed[25]=0xAB;Sum(changed);
            Check(CommandEnvelope.Valid(changed)&&CommandEnvelope.IdentityHex(changed)==CommandEnvelope.IdentityHex(b),"all four interface bytes and the frame counter are outside command data identity");
            Check(store.Observe("192.168.1.16","192.168.1.113",changed,DateTime.UtcNow)==""&&File.ReadAllText(file)==before,"known legacy command with changed header creates no duplicate or rewrite");
            Console.WriteLine("All "+checks+" envelope compatibility checks passed; no UDP traffic.");return 0;
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
