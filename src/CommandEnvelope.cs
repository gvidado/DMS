using System;
using System.Linq;

namespace TelemetryMonitor
{
    // EB90EB90 wrapper. Length and the final byte-sum are little endian.
    // Preserve captured bytes; only the identity uses the defined data area.
    public static class CommandEnvelope
    {
        private static uint U32(byte[] b,int at)
        {return (uint)(b[at]|b[at+1]<<8|b[at+2]<<16|b[at+3]<<24);}
        private static void Put32(byte[] b,int at,uint n)
        {for(int i=0;i<4;i++)b[at+i]=(byte)(n>>(i*8));}
        public static int InterfaceTypeBytes(ushort functionID) { return functionID==0x00AA?2:4; }
        public static int DataOffset(byte[] packet) { return DataOffset(packet,0); }
        internal static int DataOffset(byte[] packet,int storedInterfaceBytes)
        {
            if(packet==null||packet.Length<28||packet[0]!=0xEB||packet[1]!=0x90||packet[2]!=0xEB||packet[3]!=0x90)return -1;
            int width=storedInterfaceBytes==0?InterfaceTypeBytes((ushort)(packet[20]|packet[21]<<8)):storedInterfaceBytes;
            if(width!=2&&width!=4)return -1;int offset=22+width;return packet.Length>=offset+4?offset:-1;
        }
        public static bool Valid(byte[] b)
        {return Valid(b,0);}
        internal static bool Valid(byte[] b,int storedInterfaceBytes)
        {
            if(DataOffset(b,storedInterfaceBytes)<0 || U32(b,4)!=(uint)(b.Length-8))return false;
            uint sum=0;for(int i=0;i<b.Length-4;i++)sum+=b[i];return sum==U32(b,b.Length-4);
        }
        public static byte[] Identity(byte[] b)
        {
            byte[] content=DataContent(b),data=content??b??new byte[0];
            // Separate framed content from a raw UDP payload containing identical bytes.
            var identity=new byte[data.Length+1];identity[0]=(byte)(content==null?0:1);
            Buffer.BlockCopy(data,0,identity,1,data.Length);return identity;
        }
        public static string IdentityHex(byte[] b)
        {return BitConverter.ToString(Identity(b)).Replace('-',' ');}
        public static byte[] DataContent(byte[] packet)
        {
            int offset=DataOffset(packet);
            return Valid(packet)?packet.Skip(offset).Take(packet.Length-offset-4).ToArray():null;
        }
        internal static byte[] ObservedIdentity(byte[] packet,int storedInterfaceBytes=0)
        {
            // Identification of a captured EB90 layout is separate from file validation.
            // After locating the layout, ignore all header fields and the trailing checksum.
            int offset=DataOffset(packet,storedInterfaceBytes);bool framed=offset>=0;
            int count=packet==null?0:framed?packet.Length-offset-4:packet.Length;
            var identity=new byte[count+1];identity[0]=(byte)(framed?1:0);
            if(count>0)Buffer.BlockCopy(packet,framed?offset:0,identity,1,count);
            return identity;
        }
        internal static string ObservedIdentityHex(byte[] packet,int storedInterfaceBytes=0)
        {return BitConverter.ToString(ObservedIdentity(packet,storedInterfaceBytes)).Replace('-',' ');}
        internal static byte[] UpgradeLegacySTC(byte[] packet,out string issue,int storedInterfaceBytes=4)
        {
            issue="";if(!Valid(packet,storedInterfaceBytes))throw new ArgumentException("旧STC封装无效，原始源码保留");
            int width=InterfaceTypeBytes((ushort)(packet[20]|packet[21]<<8));if(width==storedInterfaceBytes)return packet;
            uint value=storedInterfaceBytes==4?U32(packet,22):(uint)(packet[22]|packet[23]<<8);
            if(width==2&&value>UInt16.MaxValue){issue="功能ID为0xAA时接口类型为2字节；旧值"+value+"超过65535，原始源码保留，请更正后重新入库";return packet;}
            int length=packet.Length+width-storedInterfaceBytes;
            if(length>65507){issue="接口类型改为4字节后报文超过IPv4 UDP的65507字节限制，原始源码保留，请调整数据内容后重新入库";return packet;}
            var result=new byte[length];Buffer.BlockCopy(packet,0,result,0,22);
            for(int i=0;i<width;i++)result[22+i]=(byte)(value>>(i*8));
            Buffer.BlockCopy(packet,22+storedInterfaceBytes,result,22+width,packet.Length-22-storedInterfaceBytes-4);
            Put32(result,4,(uint)(result.Length-8));uint sum=0;for(int i=0;i<result.Length-4;i++)sum+=result[i];Put32(result,result.Length-4,sum);return result;
        }
        public static byte[] PrepareForSending(byte[] original)
        {
            // Preserve the actual little-endian total/sequence counts, as well as
            // the stored frame counter. Each importer supplies the correct counts.
            var packet=(byte[])original.Clone();
            if(!Valid(packet))return packet;
            Put32(packet,4,(uint)(packet.Length-8));
            uint sum=0;for(int i=0;i<packet.Length-4;i++)sum+=packet[i];
            Put32(packet,packet.Length-4,sum);return packet;
        }
        public static byte[] BuildBC(byte[] spp,bool broadcast)
        {
            // Single-packet 1553 commands: 4-byte interface type, then 2 routing bytes.
            byte[] prefix=PacketCodec.Hex(broadcast?
                "EB90EB90300000000700010000000100000000005315200000001F01":
                "EB90EB9030000000AA00010000000100000000005315200000001101");
            var packet=new byte[prefix.Length+spp.Length+4];Buffer.BlockCopy(prefix,0,packet,0,prefix.Length);Buffer.BlockCopy(spp,0,packet,prefix.Length,spp.Length);
            Put32(packet,4,(uint)(packet.Length-8));uint sum=0;for(int i=0;i<packet.Length-4;i++)sum+=packet[i];Put32(packet,packet.Length-4,sum);return packet;
        }
        public const int MaximumInjectionBytes=65507-32;
        public const int MaximumSTCBytes=65507-28;
        public static byte[] BuildSTC(ushort functionID,uint interfaceType,byte[] data,ushort frameCounter=0x00AA)
        {
            int width=InterfaceTypeBytes(functionID),offset=22+width,maximum=65507-offset-4;
            if(width==2&&interfaceType>UInt16.MaxValue)throw new ArgumentException("功能ID为0xAA时接口类型为2字节，数值须为0～65535");
            if(data==null || data.Length>maximum)throw new ArgumentException("该功能ID的STC数据内容区最多"+maximum+"字节，超过时需另行定义分包规则");
            var packet=new byte[data.Length+offset+4];
            Buffer.BlockCopy(PacketCodec.Hex("EB90EB90"),0,packet,0,4);
            Put32(packet,4,(uint)(packet.Length-8));packet[8]=(byte)frameCounter;packet[9]=(byte)(frameCounter>>8);
            Put32(packet,10,1);Put32(packet,14,1);packet[20]=(byte)functionID;packet[21]=(byte)(functionID>>8);
            for(int i=0;i<width;i++)packet[22+i]=(byte)(interfaceType>>(i*8));
            Buffer.BlockCopy(data,0,packet,offset,data.Length);uint sum=0;for(int i=0;i<packet.Length-4;i++)sum+=packet[i];Put32(packet,packet.Length-4,sum);return packet;
        }
        internal static bool IsInjection(byte[] packet)
        {
            if(!Valid(packet) || packet.Length<=32)return false;
            byte[] template=PacketCodec.Hex("010000000100000000005315200000001104");
            return packet.Skip(10).Take(template.Length).SequenceEqual(template);
        }
        public static byte[] BuildInjection(byte[] data)
        {
            if(data==null || data.Length==0)throw new ArgumentException("注入BIN文件不能为空");
            if(data.Length>MaximumInjectionBytes)throw new ArgumentException("单条UDP注入的BIN文件最多"+MaximumInjectionBytes+"字节；更大文件需要另行配置分包规则");
            // A BIN is already the complete inner content, including its own checksum.
            // Use interface type 20 00 00 00 and route 11 04 without changing BIN bytes.
            byte[] prefix=PacketCodec.Hex("EB90EB9000000000AA00010000000100000000005315200000001104");
            var packet=new byte[prefix.Length+data.Length+4];Buffer.BlockCopy(prefix,0,packet,0,prefix.Length);Buffer.BlockCopy(data,0,packet,prefix.Length,data.Length);
            Put32(packet,4,(uint)(packet.Length-8));uint sum=0;for(int i=0;i<packet.Length-4;i++)sum+=packet[i];Put32(packet,packet.Length-4,sum);return packet;
        }
        public static string BCSubtype(string code)
        {
            int n;if(code==null || !code.StartsWith("BC",StringComparison.Ordinal) || !Int32.TryParse(code.Substring(2),out n))return "";
            return n>=2000 && n<=3999?"B指令":n>=1 && n<=99?"广播令":"";
        }
        internal static bool FletcherValid(byte[] b)
        {int a=0,c=0;foreach(byte v in b){a=(a+v)%255;c=(c+a)%255;}return a==0 && c==0;}
    }
}
