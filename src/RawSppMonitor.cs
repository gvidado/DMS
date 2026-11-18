using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TelemetryMonitor
{
    internal sealed class RawSppRecord
    {
        public long Id,CaptureFrame;
        public DateTime Time;
        public string PacketId,PacketName,Source,Mode,Reason,Checksum;
        public int OuterSourceId,Apid,Sequence,Offset,DeclaredLength;
        public bool Valid;
        public byte[] Bytes;
        internal LinkedListNode<RawSppRecord> OrderNode;
    }
    public sealed partial class Monitor
    {
        internal const int RawSppCapacity=200;
        private readonly Dictionary<string,Queue<RawSppRecord>> rawSppBuffers=new Dictionary<string,Queue<RawSppRecord>>();
        private readonly LinkedList<RawSppRecord> rawSppOrder=new LinkedList<RawSppRecord>();
        private readonly Dictionary<long,RawSppRecord> rawSppById=new Dictionary<long,RawSppRecord>();
        private long rawSppNextId,rawSppRevision;
        private int RawSppRetained {get{return rawSppById.Count;}}
        // Called after the listener's IP whitelist, before any decoder checksum/duplicate filtering.
        private bool CaptureRawSpp(byte[] bytes,string ip,bool replay,int port,long captureFrame,out bool valid,out Packet parsed)
        {
            valid=false;parsed=null;
            EnsureRuntimeIndexes();
            if (runtimeRawEnabled == 0) return false;
            var route=PacketCodec.Resolve(bytes,ip,RuntimeRouteCandidates(bytes,true));
            if (runtimeRawApidOffsets.Length > 0)
            {
                PacketDefinition apidOnly = null;
                foreach (var packet in RuntimeRawApidCandidates(bytes))
                {
                    if (!String.IsNullOrEmpty(packet.SourceIP) && packet.SourceIP != ip) continue;
                    if (apidOnly != null || route.Definition != null) return false;
                    apidOnly = packet;
                }
                if (apidOnly != null) route = new PacketRoute { Definition = apidOnly, Offset = apidOnly.SppOffset < 0 ? 6 : apidOnly.SppOffset };
            }
            if(route.Definition==null)return false;
            var definition=route.Definition;int offset=route.Offset;
            bool checksum=SppChecksum.Verify(bytes,offset,bytes.Length-offset);
            int length=bytes.Length-offset;bool onlyApid=definition.OuterSourceId<0;
            var record=new RawSppRecord{Id=++rawSppNextId,Time=DateTime.UtcNow,PacketId=definition.Id,PacketName=definition.Name,OuterSourceId=onlyApid?(offset==6&&bytes.Length>=6?Protocol.U16(bytes,0):-1):definition.OuterSourceId,
                Apid=Protocol.U16(bytes,offset)&2047,Sequence=length>=4?Protocol.U16(bytes,offset+2)&16383:-1,Offset=offset,DeclaredLength=length>=6?Protocol.U16(bytes,offset+4)+7:0,
                Source=ip+(port>0?":"+port:""),Mode=replay?"文件演示":"实时UDP",CaptureFrame=captureFrame,Bytes=bytes,Reason="",
                Checksum=checksum?"通过":"失败"};
            try
            {
                if(onlyApid)
                {
                    parsed=new Packet{Offset=offset,Length=length,Apid=record.Apid,Sequence=record.Sequence,PacketId=definition.Id,PacketName=definition.Name,ChecksumVerified=checksum};
                    if(length<8)throw new ArgumentException("SPP包过短");
                    if(record.DeclaredLength!=length)throw new ArgumentException("SPP实际长度不一致：声明"+record.DeclaredLength+"，实际"+length);
                    if(!checksum)throw new ArgumentException("Fletcher-16累加和校验失败");
                    record.Valid=true;
                }
                else{parsed=PacketCodec.ParseAt(bytes,definition,offset,checksum);record.Valid=String.IsNullOrEmpty(parsed.Warning);record.Reason=parsed.Warning;}
            }
            catch(ArgumentException ex){record.Reason=ex.Message;}
            valid=record.Valid;
            Queue<RawSppRecord> buffer;if(!rawSppBuffers.TryGetValue(definition.Id,out buffer))rawSppBuffers[definition.Id]=buffer=new Queue<RawSppRecord>();
            if (buffer.Count == RawSppCapacity) RemoveRawSppRecord(buffer.Dequeue());
            buffer.Enqueue(record);
            record.OrderNode=rawSppOrder.AddFirst(record);
            rawSppById.Add(record.Id,record);
            rawSppRevision++;
            return true;
        }
        private void RemoveRawSppRecord(RawSppRecord record)
        {
            rawSppOrder.Remove(record.OrderNode);
            rawSppById.Remove(record.Id);
        }

        private IEnumerable<RawSppRecord> RawSppRecords(string packetId, bool newestFirst, out int total)
        {
            if(packetId.Length==0)
            {
                total=rawSppOrder.Count;
                return newestFirst ? rawSppOrder : rawSppOrder.Reverse();
            }
            Queue<RawSppRecord> buffer;
            if(!rawSppBuffers.TryGetValue(packetId,out buffer))
            {
                total=0;
                return Enumerable.Empty<RawSppRecord>();
            }
            total=buffer.Count;
            return newestFirst ? buffer.Reverse() : buffer;
        }
        private static object RawSppSummary(RawSppRecord r)
        {return new{id=r.Id,packetId=r.PacketId,packet=r.PacketName,outerSourceId=r.OuterSourceId,apid=r.Apid,sequence=r.Sequence,time=BeijingTime.Format(r.Time,"yyyy-MM-dd HH:mm:ss.fff"),
            source=r.Source,mode=r.Mode,captureFrame=r.CaptureFrame,udpLength=r.Bytes.Length,sppLength=r.Bytes.Length-r.Offset,declaredLength=r.DeclaredLength,
            status=r.Valid?"通过":"异常",reason=r.Reason,checksum=r.Checksum,hexPreview=BitConverter.ToString(r.Bytes,r.Offset,Math.Min(24,r.Bytes.Length-r.Offset)).Replace('-', ' ')};}
        public object RawSppList(string packetId,int page)
        {
            long revision;
            string configurationRevision;
            int retained;
            object options;
            int total,pages;
            RawSppRecord[] visible;
            lock(Gate)
            {
                if(packetId.Length>64)throw new ArgumentException("SPP包筛选无效");if(page<1||page>10000)throw new ArgumentException("页码应为1至10000");
                var configured=NativeDecoder?decoder.Packets:new List<PacketDefinition>();
                var ids=configured.Where(p=>p.MonitorRaw).Select(p=>p.Id).Concat(rawSppBuffers.Keys).Distinct().ToArray();
                var definitions=configured.ToDictionary(p=>p.Id);
                options=ids.Select(id=>{
                    PacketDefinition definition;definitions.TryGetValue(id,out definition);
                    Queue<RawSppRecord> buffer;rawSppBuffers.TryGetValue(id,out buffer);
                    var latest=definition==null&&buffer!=null?buffer.LastOrDefault():null;
                    return new{id,name=definition==null?latest.PacketName:definition.Name,enabled=definition!=null&&definition.MonitorRaw,
                        outerSourceId=definition==null?latest.OuterSourceId:definition.OuterSourceId,apid=definition==null?latest.Apid:definition.Apid,retained=buffer==null?0:buffer.Count};
                }).ToArray();
                var ordered=RawSppRecords(packetId,true,out total);
                pages=Math.Max(1,(total+49)/50);
                page=Math.Min(page,pages);
                visible=ordered.Skip((page-1)*50).Take(50).ToArray();
                revision=rawSppRevision;configurationRevision=DecoderRevision;retained=RawSppRetained;
            }
            return new{revision,configurationRevision,capacity=RawSppCapacity,retained,total,page,pages,packets=options,
                records=visible.Select(RawSppSummary).ToArray()};
        }
        public object RawSppDetail(long id)
        {
            RawSppRecord record;
            lock(Gate) rawSppById.TryGetValue(id,out record);
            if(record==null)throw new ArgumentException("该原包已清除或超出所属SPP包的最近200条");
            return new{record=RawSppSummary(record),sppHex=BitConverter.ToString(record.Bytes,record.Offset).Replace('-', ' '),udpHex=BitConverter.ToString(record.Bytes).Replace('-', ' '),
                note="保留从SPP起点到UDP末尾的全部实际字节，包含包头和校验；长度异常时不补齐或裁剪。完整UDP另含六字节外层头。重复帧与校验异常包同样留存；原包监测本身不执行参数解码。"};
        }
        public void ClearRawSpp(string packetId)
        {
            lock(Gate)
            {
                if(packetId.Length>64)throw new ArgumentException("SPP包筛选无效");
                if (packetId.Length == 0) { rawSppBuffers.Clear(); rawSppOrder.Clear(); rawSppById.Clear(); }
                else
                {
                    Queue<RawSppRecord> buffer;
                    if (rawSppBuffers.TryGetValue(packetId, out buffer))
                    {
                        foreach(var record in buffer) RemoveRawSppRecord(record);
                        rawSppBuffers.Remove(packetId);
                    }
                }
                rawSppRevision++;
            }
        }
        public void WriteRawSppCsv(TextWriter writer,string packetId)
        {
            RawSppRecord[] rows;
            lock(Gate)
            {
                if(packetId.Length>64)throw new ArgumentException("SPP包筛选无效");
                int total;
                rows=RawSppRecords(packetId,false,out total).ToArray();
            }
            writer.WriteLine("记录号,接收时间（北京时间）,SPP包ID,SPP包名称,外层来源,APID,帧计数,来源IP及端口,模式,抓包帧号,UDP字节数,SPP起始偏移,SPP实际字节数,SPP声明字节数,校验状态,格式状态,原因,SPP完整内容HEX,完整UDP载荷HEX");
            foreach(var r in rows)writer.WriteLine(String.Join(",",new[]{r.Id.ToString(),BeijingTime.Format(r.Time,"yyyy-MM-dd HH:mm:ss.fff"),r.PacketId,r.PacketName,r.OuterSourceId<0?"/":"0x"+r.OuterSourceId.ToString("X4"),r.Apid.ToString(),r.Sequence.ToString(),r.Source,r.Mode,r.CaptureFrame>0?r.CaptureFrame.ToString():"",r.Bytes.Length.ToString(),r.Offset.ToString(),(r.Bytes.Length-r.Offset).ToString(),r.DeclaredLength.ToString(),r.Checksum,r.Valid?"通过":"异常",r.Reason,BitConverter.ToString(r.Bytes,r.Offset).Replace('-', ' '),BitConverter.ToString(r.Bytes).Replace('-', ' ')}.Select(CsvCell)));
        }
    }
}
