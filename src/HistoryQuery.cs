using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace TelemetryMonitor
{
    internal sealed class HistorySelection
    {
        internal string[] Values;
        internal bool IsList;
        internal string Json {get{return HistoryJson.Encode(Values);}}
    }
    internal sealed class HistoryQuery
    {
        internal string Type="samples",Start="",End="",Experiment="",Source="",Destination="",Direction="",Namespace="",Parameter="",Packet="",Command="",Status="",Session="",TimeBasis="",Quality="",Cursor="";
        internal int Size=100;
        internal bool AllTime;
        private readonly Dictionary<string,HistorySelection> selections=new Dictionary<string,HistorySelection>();
        internal HistoryQuery Copy(){return new HistoryQuery{Type=Type,Start=Start,End=End,Experiment=Experiment,Source=Source,Destination=Destination,Direction=Direction,Namespace=Namespace,Parameter=Parameter,Packet=Packet,Command=Command,Status=Status,Session=Session,TimeBasis=TimeBasis,Quality=Quality,Cursor=Cursor,Size=Size,AllTime=AllTime};}
        internal string Fingerprint(){return HistoryText.Hash(HistoryJson.Encode(new{Type,Start,End,AllTime,Experiment,Source,Destination,Direction,Namespace,Parameter,Packet,Command,Status,Session,TimeBasis,Quality,Clock="SERVER_OBSERVATION_V107"}));}
        internal HistorySelection Selection(string key){return selections[key];}
        private string Check(string key,string value,int length,string[] allowed=null,string kind="")
        {
            value=(value??"").Trim();bool list=value.StartsWith("[",StringComparison.Ordinal);string[] items;
            try{items=list?HistoryJson.Decode<string[]>(value):(value.Length==0?new string[0]:new[]{value});}catch{throw new ArgumentException("多选查询条件格式无效："+key);}
            if(items==null||items.Length>100000)throw new ArgumentException("查询选择数量过多："+key);
            items=items.Select(v=>(v??"").Trim()).Where(v=>v.Length>0).Distinct(StringComparer.Ordinal).OrderBy(v=>v,StringComparer.Ordinal).ToArray();
            foreach(string item in items)
            {
                if(item.Length>length||(allowed!=null&&!allowed.Contains(item)))throw new ArgumentException("查询条件无效："+key);
                Guid id;System.Net.IPAddress ip;
                if(kind=="uuid"&&!Guid.TryParse(item,out id))throw new ArgumentException("试验或会话标识无效");
                if(kind=="ip"&&!System.Net.IPAddress.TryParse(item,out ip))throw new ArgumentException("来源或目的IP无效");
            }
            selections[key]=new HistorySelection{Values=items,IsList=list};return list?HistoryJson.Encode(items):(items.Length==0?"":items[0]);
        }
        private static DateTime ParseTime(string text)
        {
            if(text.Length>128)throw new ArgumentException("查询时间无效");
            bool offset=System.Text.RegularExpressions.Regex.IsMatch(text,@"(?:[zZ]|[+-][0-9]{2}:?[0-9]{2})$");
            DateTimeOffset parsed;
            if(!offset)
            {
                DateTime local;if(!DateTime.TryParse(text,CultureInfo.InvariantCulture,DateTimeStyles.AllowWhiteSpaces,out local))throw new ArgumentException("请填写有效的开始和结束时间");
                parsed=new DateTimeOffset(DateTime.SpecifyKind(local,DateTimeKind.Unspecified),TimeSpan.FromHours(8));
            }
            else if(!DateTimeOffset.TryParse(text,CultureInfo.InvariantCulture,DateTimeStyles.AllowWhiteSpaces,out parsed))throw new ArgumentException("请填写有效的开始和结束时间");
            return parsed.UtcDateTime;
        }
        internal void Validate()
        {
            if(Type=="judgments")throw new ArgumentException("历史数据库已取消判读记录查询");
            if(!new[]{"samples","wide","events","packets","commands","incidents","sessions"}.Contains(Type))throw new ArgumentException("查询类型无效");
            if(Cursor==null)Cursor="";if(Cursor.Length>2048)throw new ArgumentException("分页标识过长");
            string[] saved=null;if(Cursor.Length>0){try{saved=HistoryJson.Decode<string[]>(Encoding.UTF8.GetString(Convert.FromBase64String(Cursor)));}catch{throw new ArgumentException("分页标识无效，请重新查询");}if(saved==null||saved.Length!=9)throw new ArgumentException("分页标识无效，请重新查询");}
            if(AllTime){Start="";End="";}
            else
            {
                DateTime serverNow=EventStamp.ServerTime(System.Diagnostics.Stopwatch.GetTimestamp());
                if(String.IsNullOrEmpty(Start))Start=saved==null?HistoryText.Utc(serverNow.AddDays(-1)):saved[7];if(String.IsNullOrEmpty(End))End=saved==null?HistoryText.Utc(serverNow.AddMinutes(1)):saved[8];
                DateTime start=ParseTime(Start),end=ParseTime(End);if(end<start)throw new ArgumentException("结束时间应晚于开始时间");Start=HistoryText.Utc(start);End=HistoryText.Utc(end);
            }
            Experiment=Check("experiment",Experiment,80,kind:"uuid");Session=Check("session",Session,80,kind:"uuid");Source=Check("source",Source,80,kind:"ip");Destination=Check("destination",Destination,80,kind:"ip");
            Namespace=Check("namespace",Namespace,10,new[]{"TM","TME","TMB"});Direction=Check("direction",Direction,40,new[]{"RECEIVE","RECEIVE_CAPTURE","SEND_CAPTURE","SEND_ATTEMPT"});
            Parameter=Check("parameter",Parameter,200);Packet=Check("packet",Packet,200);Command=Check("command",Command,100);Status=Check("status",Status,100);TimeBasis=Check("timeBasis",TimeBasis,64);Quality=Check("quality",Quality,100);Size=Math.Max(1,Math.Min(500,Size));
        }
    }
    internal sealed partial class HistoryStorage
    {
        // Expressions work for both existing databases and new records, without rewriting
        // permanent archives or updating millions of historical sample rows.
        private const string HistoryEventTimeSql="(CASE WHEN e.time_basis='PCAP_CAPTURE' AND COALESCE(e.capture_time_confirmed,true) THEN e.observed_at ELSE e.received_at END)";
        private const string HistoryEventTicksSql="(CASE WHEN e.time_basis='PCAP_CAPTURE' AND COALESCE(e.capture_time_confirmed,true) THEN e.observed_ticks ELSE e.received_ticks END)";
        private readonly object historyOptionsGate=new object();
        private Dictionary<string,object> historyOptionsCache;
        private DateTime historyOptionsAt=DateTime.MinValue;
        private string historyOptionsRevision="",historyMetadataError="";
        private long historyDefinitionGeneration,historyOptionsGeneration=-1;
        private static string HistoryLocal(DateTime utc){return BeijingTime.Iso(utc);}
        private static string HistoryLocalText(string value)
        {DateTime utc;return String.IsNullOrEmpty(value)||!DateTime.TryParse(value,CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal|DateTimeStyles.AdjustToUniversal,out utc)?value:HistoryLocal(utc);}
        private static Dictionary<string,object> HistoryTimeMetadata()
        {
            var now=new DateTimeOffset(BeijingTime.At(EventStamp.ServerTime(System.Diagnostics.Stopwatch.GetTimestamp())),TimeSpan.FromHours(8));
            return new Dictionary<string,object>{{"serverTimeZone","Asia/Shanghai"},{"serverTimeZoneName","北京时间"},{"serverUtcOffsetMinutes",(int)now.Offset.TotalMinutes},{"serverNowLocal",now.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffffzzz",CultureInfo.InvariantCulture)},{"serverNowUtc",HistoryText.Utc(now.UtcDateTime)},{"eventClock","SERVER_OBSERVATION"},{"monotonicFrequency",System.Diagnostics.Stopwatch.Frequency}};
        }
        internal object QueryHistory(HistoryQuery request)
        {
            request.Validate();if(!querySlots.WaitOne(0))throw new InvalidOperationException("历史查询较多，请稍后重试");
            try
            {
                HistoryStorageConfiguration settings;lock(gate)settings=configuration;if(!settings.DatabaseEnabled)throw new InvalidOperationException("尚未启用数据库连接；采集数据继续保存在本地档案，请在历史存储配置中连接数据库后查询");
                using(var database=new HistoryPostgresClient(root,settings))return QueryHistoryCore(database,request);
            }
            finally{querySlots.Release();}
        }
        private static string HistorySelected(List<string> args,HistorySelection selected,string column,string cast="text")
        {if(selected.Values.Length==0)return "";args.Add(selected.Json);return column+" = ANY(ARRAY(SELECT jsonb_array_elements_text($"+args.Count+"::jsonb))::"+cast+"[])";}
        private static void HistoryWhere(List<string> clauses,string value){if(value.Length>0)clauses.Add(value);}
        private object QueryHistoryCore(HistoryPostgresClient database,HistoryQuery q,bool includeWideColumns=true)
        {
            var clock=System.Diagnostics.Stopwatch.StartNew();var args=new List<string>();var where=new List<string>{"e.kind<>'JUDGMENT'"};
            if(!q.AllTime){args.Add(q.Start);args.Add(q.End);where.Add(HistoryEventTimeSql+" >= $1::timestamptz");where.Add(HistoryEventTimeSql+" <= $2::timestamptz");}
            string joins="",outerJoins="",key="0::bigint",columns="",kinds="";
            if(q.Type=="samples"||q.Type=="wide")
            {
                // Walk the ordered packet index and its parameter primary key. OFFSET 0 keeps
                // the per-packet lookup lateral, so a small page never sorts the whole sample range.
                var sampleWhere=new List<string>{"s.packet_event_id=e.id"};
                HistoryWhere(sampleWhere,HistorySelected(args,q.Selection("namespace"),"p.namespace"));
                var selected=q.Selection("parameter");if(selected.Values.Length>0)
                {if(selected.IsList)HistoryWhere(sampleWhere,HistorySelected(args,selected,"p.reference"));else{args.Add(selected.Values[0]);sampleWhere.Add("position(lower($"+args.Count+") in lower(p.reference||' '||p.name))>0");}}
                HistoryWhere(sampleWhere,HistorySelected(args,q.Selection("quality"),"s.quality"));
                if(q.Type=="wide")
                {
                    joins=" JOIN LATERAL (SELECT jsonb_object_agg(p.runtime_id||chr(31)||p.reference,jsonb_build_object('value',s.value_text,'valueKind',s.value_kind,'quality',s.quality,'hex',s.hex_source,'parameterId',p.runtime_id)) AS parameter_values FROM td_history.sample s JOIN td_history.parameter_definition p ON p.id=s.parameter_id WHERE "+String.Join(" AND ",sampleWhere)+" HAVING count(*)>0) wide_page ON true ";
                    outerJoins="";columns=",'values',page.parameter_values";kinds="'PACKET'";
                }
                else if(selected.IsList&&selected.Values.Length>0&&selected.Values.Length<=8)
                {
                    // A sparse or retired exact reference should seek its parameter/time index,
                    // rather than probing every packet in a wide historical time range.
                    joins=" JOIN td_history.sample s ON s.packet_event_id=e.id JOIN td_history.parameter_definition p ON p.id=s.parameter_id ";key="p.id";where.AddRange(sampleWhere);
                }
                else{joins=" CROSS JOIN LATERAL (SELECT s.parameter_id FROM td_history.sample s JOIN td_history.parameter_definition p ON p.id=s.parameter_id WHERE "+String.Join(" AND ",sampleWhere)+" ORDER BY s.parameter_id DESC OFFSET 0) sample_page ";key="sample_page.parameter_id";}
                if(q.Type=="samples")
                {
                    outerJoins=" JOIN td_history.sample s ON s.packet_event_id=e.id AND s.parameter_id=page.sort_parameter JOIN td_history.parameter_definition p ON p.id=s.parameter_id ";
                    columns=",'parameterId',p.runtime_id,'code',p.reference,'name',p.name,'unit',p.unit,'value',s.value_text,'valueKind',s.value_kind,'hex',s.hex_source,'raw',s.raw_text,'quality',s.quality,'diagnostic',s.diagnostic";kinds="'PACKET'";
                }
            }
            else if(q.Type=="commands")
            {joins=" JOIN td_history.command_event c ON c.event_id=e.id ";outerJoins=joins;columns=",'codes',c.codes,'names',c.names,'evidence',c.evidence,'verified',c.verified,'success',c.success,'actor',c.actor";kinds="'COMMAND_CAPTURE','COMMAND_SEND'";}
            else if(q.Type=="incidents")
            {joins=" JOIN td_history.incident i ON i.event_id=e.id ";outerJoins=joins;columns=",'incidentKey',i.incident_key,'condition',i.condition,'value',i.value_text,'released',i.released,'releaseReason',i.release_reason,'releasedAt',i.released_at,'actor',i.actor";kinds="'INCIDENT'";}
            else if(q.Type=="events"){columns=",'data',e.data";kinds="";}
            else kinds=q.Type=="sessions"?"'SESSION'":"'PACKET','PACKET_CAPTURE','NETWORK_FRAME'";
            if(kinds.Length>0)where.Add("e.kind IN ("+kinds+")");
            foreach(var spec in new[]{new[]{"experiment","e.experiment_id","uuid"},new[]{"session","e.session_id","uuid"},new[]{"source","e.source_ip","inet"},new[]{"destination","e.destination_ip","inet"},new[]{"direction","e.direction","text"},new[]{"status","e.status","text"},new[]{"timeBasis","e.time_basis","text"}})HistoryWhere(where,HistorySelected(args,q.Selection(spec[0]),spec[1],spec[2]));
            var packet=q.Selection("packet");if(packet.Values.Length>0){if(packet.IsList)HistoryWhere(where,HistorySelected(args,packet,"e.packet_id"));else{args.Add(packet.Values[0]);where.Add("position(lower($"+args.Count+") in lower(e.packet_id||' '||e.packet_name))>0");}}
            var command=q.Selection("command");if(command.Values.Length>0&&q.Type=="commands")
            {string code="c.codes";args.Add(command.IsList?command.Json:command.Values[0]);where.Add(command.IsList?code+" && ARRAY(SELECT jsonb_array_elements_text($"+args.Count+"::jsonb))":"EXISTS(SELECT 1 FROM unnest("+code+") code WHERE position(upper($"+args.Count+") in upper(code))>0)");}
            if(q.Cursor.Length>0)
            {
                var cursor=HistoryJson.Decode<string[]>(Encoding.UTF8.GetString(Convert.FromBase64String(q.Cursor)));if(cursor[0]!=q.Fingerprint())throw new ArgumentException("查询条件或时间规则已改变，请重新查询");
                int at=args.Count+1;args.AddRange(cursor.Skip(1).Take(5));where.Add("("+HistoryEventTimeSql+","+HistoryEventTicksSql+",e.ordinal,e.id,"+key+") < ($"+at+"::timestamptz,$"+(at+1)+"::bigint,$"+(at+2)+"::bigint,$"+(at+3)+"::uuid,$"+(at+4)+"::bigint)");
            }
            string order=HistoryEventTimeSql+" DESC,"+HistoryEventTicksSql+" DESC,e.ordinal DESC,e.id DESC,"+key+" DESC";
            string wideColumns=q.Type=="wide"?",wide_page.parameter_values":"";
            string sql="WITH page AS MATERIALIZED (SELECT e.id,"+HistoryEventTimeSql+" AS event_at,"+HistoryEventTicksSql+" AS event_ticks,e.ordinal,"+key+" AS sort_parameter"+wideColumns+" FROM td_history.event e "+joins+" WHERE "+(where.Count>0?String.Join(" AND ",where):"true")+" ORDER BY "+order+" LIMIT "+(q.Size+1)+@")
SELECT jsonb_build_object('id',e.id::text,'kind',e.kind,'experimentId',e.experiment_id::text,'experiment',COALESCE(x.name,''),'session',e.session_id::text,'boot',e.boot_id::text,'timeBasis',e.time_basis,'originalTimeUtc',e.data->>'Time','observedTicks',e.observed_ticks::text,'eventTicks',page.event_ticks::text,'receivedTicks',e.received_ticks::text,'monotonicTicks',e.monotonic_ticks::text,'ordinal',e.ordinal::text,'sourceIP',host(e.source_ip),'sourcePort',e.source_port,'destinationIP',host(e.destination_ip),'destinationPort',e.destination_port,'direction',e.direction,'mode',e.mode,'packetId',e.packet_id,'packet',e.packet_name,'outerSourceId',e.outer_source_id,'apid',e.apid,'sequence',e.frame_sequence,'status',e.status,'reason',e.reason,'configuration',e.configuration_id::text,'captureTimeConfirmed',COALESCE(e.capture_time_confirmed,e.time_basis IN ('NPCAP_RECEIVE','NPCAP_CAPTURE','PCAP_CAPTURE')),'captureTicks',e.capture_ticks::text,'captureEventId',e.capture_event_id::text"+columns+@")::text AS item,page.event_at::text AS sort_time,page.event_ticks::text AS sort_ticks,page.ordinal::text AS sort_ordinal,page.id::text AS sort_id,page.sort_parameter::text AS sort_parameter
FROM page JOIN td_history.event e ON e.id=page.id "+outerJoins+" LEFT JOIN td_history.experiment x ON x.id=e.experiment_id ORDER BY page.event_at DESC,page.event_ticks DESC,page.ordinal DESC,page.id DESC,page.sort_parameter DESC";
            var rows=database.Query(sql,args.ToArray());bool more=rows.Count>q.Size;var kept=rows.Take(q.Size).ToArray();string next="";
            if(more&&kept.Length>0){var last=kept[kept.Length-1];next=Convert.ToBase64String(Encoding.UTF8.GetBytes(HistoryJson.Encode(new[]{q.Fingerprint(),last["sort_time"],last["sort_ticks"],last["sort_ordinal"],last["sort_id"],last["sort_parameter"],"1",q.Start,q.End})));}
            var result=HistoryTimeMetadata();result["type"]=q.Type;result["rows"]=kept.Select(r=>HistoryDisplayRow(HistoryJson.Decode<Dictionary<string,object>>(r["item"]))).ToArray();result["hasMore"]=more;result["cursor"]=next;result["start"]=q.Start;result["end"]=q.End;result["allTime"]=q.AllTime;result["localStart"]=HistoryLocalText(q.Start);result["localEnd"]=HistoryLocalText(q.End);result["queryMilliseconds"]=Math.Round(clock.Elapsed.TotalMilliseconds,1);result["timeFilterNote"]="实时记录按服务器接收或登记时间筛选；历史pcapng按文件原始时间筛选";
            if(q.Type=="wide"&&includeWideColumns)result["columns"]=HistoryWideColumns(database,q);
            return result;
        }
        private static Dictionary<string,object> HistoryDisplayRow(Dictionary<string,object> row)
        {
            long ticks,receivedTicks;Int64.TryParse(Convert.ToString(row["eventTicks"]),out ticks);Int64.TryParse(Convert.ToString(row["receivedTicks"]),out receivedTicks);
            bool confirmed=Convert.ToBoolean(row["captureTimeConfirmed"]);var original=new DateTime(ticks,DateTimeKind.Utc);var received=new DateTime(receivedTicks,DateTimeKind.Utc);
            long captureTicks;Int64.TryParse(Convert.ToString(row["captureTicks"]),out captureTicks);
            if(captureTicks==0)Int64.TryParse(Convert.ToString(row["observedTicks"]),out captureTicks);
            string basis=Convert.ToString(row["timeBasis"]);
            if(!row.ContainsKey("originalTimeUtc")||row["originalTimeUtc"]==null)row["originalTimeUtc"]=HistoryText.Utc(original);
            row["originalCaptureTimeUtc"]=confirmed?(object)HistoryText.Utc(new DateTime(captureTicks,DateTimeKind.Utc)):null;
            row["captureTimeSource"]=HistoryCaptureSource(confirmed,basis);
            row["eventTimeBasis"]=confirmed&&basis=="PCAP_CAPTURE"?"PCAP_CAPTURE":"SERVER_OBSERVATION";
            row["eventTimeSource"]=HistoryEventSource(Convert.ToString(row["kind"]),basis,confirmed,Convert.ToString(row["mode"]));
            row["eventTimeConfirmed"]=true;row["originalEventTimeUtc"]=HistoryText.Utc(original);
            row["originalReceivedUtc"]=HistoryText.Utc(received);row["time"]=HistoryLocal(original);row["received"]=HistoryLocal(received);row["receivedLocal"]=row["received"];
            object released;if(row.TryGetValue("releasedAt",out released)&&released!=null){row["originalReleasedAtUtc"]=Convert.ToString(released);row["releasedAt"]=HistoryLocalText(Convert.ToString(released));}
            return row;
        }
        private static string HistoryCaptureSource(bool confirmed,string basis)
        {return !confirmed?"时间未确认":basis=="PCAP_CAPTURE"?"历史文件原始抓包时间":"服务器网卡抓包时间";}
        private static string HistoryEventSource(string kind,string basis,bool captureConfirmed,string mode)
        {
            if(captureConfirmed&&basis=="PCAP_CAPTURE")return "历史文件原始抓包时间";
            if(mode=="PCAP_IMPORT")return "服务器导入登记时间";
            if(kind=="PACKET")return "服务器接收时间";
            if(kind=="PACKET_CAPTURE"||kind=="COMMAND_CAPTURE")return "服务器捕获回调时间";
            if(kind=="COMMAND_SEND")return "服务器发令记录时间";
            return "服务器记录时间";
        }
        internal object HistoryDetail(string id)
        {
            Guid parsed;if(!Guid.TryParse(id,out parsed))throw new ArgumentException("记录标识无效");HistoryStorageConfiguration settings;lock(gate)settings=configuration;
            if(!querySlots.WaitOne(0))throw new InvalidOperationException("历史查询较多，请稍后重试");
            try
            {
                using(var database=new HistoryPostgresClient(root,settings))
                {
                    var rows=database.Query("SELECT s.id::text,s.archive_file,s.sha256,s.event_count::text,e.archive_line::text,e.observed_ticks::text,e.received_ticks::text,"+HistoryEventTicksSql+"::text AS event_ticks,e.capture_ticks::text,COALESCE(e.capture_time_confirmed,e.time_basis IN ('NPCAP_RECEIVE','NPCAP_CAPTURE','PCAP_CAPTURE'))::text AS capture_confirmed,e.capture_event_id::text FROM td_history.event e JOIN td_history.segment s ON s.id=e.segment_id WHERE e.id=$1::uuid AND e.kind<>'JUDGMENT'",id);if(rows.Count==0)throw new ArgumentException("历史记录不存在或尚未完成入库");var row=rows[0];var segment=new HistoryArchiveSegment{Id=row["id"],File=row["archive_file"],Hash=row["sha256"],Count=Int32.Parse(row["event_count"])};
                    var records=ReadSegment(segment);int index=Int32.Parse(row["archive_line"])-1;if(index<0||index>=records.Length||Guid.Parse(records[index].Id)!=parsed)throw new InvalidDataException("历史索引与原始档案不一致，文件保留");var record=records[index];
                    byte[] payload=record.PayloadBase64.Length==0?new byte[0]:Convert.FromBase64String(record.PayloadBase64);string spp="";if(payload.Length>=12&&record.PacketId.Length>0)spp=BitConverter.ToString(payload,6).Replace('-',' ');
                    var result=HistoryTimeMetadata();bool confirmed=row["capture_confirmed"]=="true";
                    long ticks=Int64.Parse(row["event_ticks"]),received=Int64.Parse(row["received_ticks"]),captureTicks;
                    Int64.TryParse(row["capture_ticks"],out captureTicks);
                    if(captureTicks==0)captureTicks=Int64.Parse(row["observed_ticks"]);
                    result["recordJson"]=HistoryJson.Encode(record);result["time"]=HistoryLocal(new DateTime(ticks,DateTimeKind.Utc));
                    result["received"]=HistoryLocal(new DateTime(received,DateTimeKind.Utc));result["receivedLocal"]=result["received"];
                    result["originalTimeUtc"]=record.Time;result["originalEventTimeUtc"]=HistoryText.Utc(new DateTime(ticks,DateTimeKind.Utc));
                    result["originalCaptureTimeUtc"]=confirmed?(object)HistoryText.Utc(new DateTime(captureTicks,DateTimeKind.Utc)):null;
                    result["originalReceivedUtc"]=record.Received;result["captureTimeConfirmed"]=confirmed;
                    result["captureTimeSource"]=HistoryCaptureSource(confirmed,record.TimeBasis);
                    result["eventTimeBasis"]=confirmed&&record.TimeBasis=="PCAP_CAPTURE"?"PCAP_CAPTURE":"SERVER_OBSERVATION";
                    result["eventTimeSource"]=HistoryEventSource(record.Kind,record.TimeBasis,confirmed,record.Mode);result["eventTimeConfirmed"]=true;
                    result["captureEventId"]=row["capture_event_id"];result["timeBasis"]=record.TimeBasis;
                    result["observedTicks"]=row["observed_ticks"];result["eventTicks"]=ticks.ToString();result["monotonicTicks"]=record.MonotonicTicks.ToString();
                    result["udpHex"]=BitConverter.ToString(payload).Replace('-',' ');result["sppHex"]=spp;result["archive"]=segment.File;result["archiveSha256"]=segment.Hash;return result;
                }
            }
            finally{querySlots.Release();}
        }
        internal object QueryParameters(string term,string stream,int limit)
        {
            HistoryStorageConfiguration settings;lock(gate)settings=configuration;if(!settings.DatabaseEnabled)return new{rows=new object[0]};
            if(term.Length>200||!new[]{"","TM","TME","TMB"}.Contains(stream))throw new ArgumentException("参数查询条件无效");if(!querySlots.WaitOne(0))throw new InvalidOperationException("历史查询较多，请稍后重试");
            try{using(var database=new HistoryPostgresClient(root,settings)){var rows=database.Query("SELECT DISTINCT reference,name,namespace,unit,packet_name FROM td_history.parameter_definition WHERE ($1='' OR position(lower($1) in lower(reference||' '||name))>0) AND ($2='' OR namespace=$2) ORDER BY reference,name LIMIT "+Math.Max(1,Math.Min(500,limit)),term,stream);return new{rows};}}finally{querySlots.Release();}
        }
        internal object QueryHistoryOptions(bool refresh)
        {
            HistoryStorageConfiguration settings;lock(gate)settings=configuration;var result=HistoryTimeMetadata();
            if(!settings.DatabaseEnabled){foreach(string name in new[]{"parameters","packets","commands","experiments","sessions","sources","destinations","statuses","timeBases","qualities","namespaces","directions"})result[name]=new object[0];result["enabled"]=false;return result;}
            lock(historyOptionsGate)
            {
                long generation=System.Threading.Interlocked.Read(ref historyDefinitionGeneration);
                if(refresh||historyOptionsCache==null||historyOptionsRevision!=settings.Revision||historyOptionsGeneration!=generation||(DateTime.UtcNow-historyOptionsAt).TotalSeconds>30)
                {
                    if(!querySlots.WaitOne(0))throw new InvalidOperationException("历史查询较多，请稍后刷新选择项");
                    try
                    {
                        using(var database=new HistoryPostgresClient(root,settings))historyOptionsCache=LoadHistoryOptions(database);
                        historyOptionsAt=DateTime.UtcNow;historyOptionsRevision=settings.Revision;historyOptionsGeneration=generation;
                    }
                    finally{querySlots.Release();}
                }
                foreach(var item in historyOptionsCache)result[item.Key]=item.Value;result["enabled"]=true;result["definitionGeneration"]=historyOptionsGeneration;result["cachedAt"]=HistoryLocal(historyOptionsAt);result["preparing"]=historyMetadataPreparing;result["warning"]=historyMetadataError;return result;
            }
        }
        private Dictionary<string,object> LoadHistoryOptions(HistoryPostgresClient database)
        {
            var result=new Dictionary<string,object>();
            result["parameters"]=database.Query(@"SELECT jsonb_build_object('value',reference,'label',reference||' | '||string_agg(DISTINCT name,' / ' ORDER BY name),'code',reference,'reference',reference,'name',string_agg(DISTINCT name,' / ' ORDER BY name),'namespace',min(namespace),'unit',string_agg(DISTINCT unit,' / '),'packetIds',array_agg(DISTINCT packet_id),'packetName',string_agg(DISTINCT packet_name,' / '),'group',string_agg(DISTINCT COALESCE(definition->>'Group',''),' / '))::text AS item FROM td_history.parameter_definition GROUP BY reference ORDER BY reference").Select(r=>HistoryJson.Decode<Dictionary<string,object>>(r["item"])).ToArray();
            result["experiments"]=database.Query("SELECT id::text,name,started_at::text FROM td_history.experiment ORDER BY started_at DESC,id").Select(r=>(object)new{value=r["id"],label=r["name"],name=r["name"],startedAt=HistoryLocalText(r["started_at"])}).ToArray();
            var choices=database.Query("SELECT category,value,names::text,to_json(names)::text AS names_json,first_at::text,metadata::text FROM td_history.query_choice ORDER BY category,value");
            foreach(var spec in new[]{new[]{"packet","packets"},new[]{"command","commands"},new[]{"session","sessions"},new[]{"source","sources"},new[]{"destination","destinations"},new[]{"status","statuses"},new[]{"timeBasis","timeBases"},new[]{"quality","qualities"},new[]{"namespace","namespaces"},new[]{"direction","directions"}})
            {
                var list=new List<object>();foreach(var row in choices.Where(r=>r["category"]==spec[0]))
                {
                    string[] names=HistoryJson.Decode<string[]>(row["names_json"]);string name=String.Join(" / ",names.Where(n=>n.Length>0));string value=row["value"],label=name.Length>0?value+" | "+name:value;
                    var metadata=HistoryJson.Decode<Dictionary<string,object>>(row["metadata"]);if(spec[0]=="session")label=HistoryLocalText(row["first_at"])+" | "+value;
                    var item=new Dictionary<string,object>{{"value",value},{"label",label},{"name",name},{"code",value},{"id",value}};foreach(var field in metadata)item[field.Key]=field.Value;list.Add(item);
                }
                result[spec[1]]=list.ToArray();
            }
            if(((object[])result["namespaces"]).Length==0)result["namespaces"]=new[]{new{value="TME",label="TME"},new{value="TMB",label="TMB"},new{value="TM",label="TM"}};
            if(((object[])result["directions"]).Length==0)result["directions"]=new[]{new{value="RECEIVE",label="接收并解析"},new{value="RECEIVE_CAPTURE",label="网卡接收证据"},new{value="SEND_CAPTURE",label="监视到发送"},new{value="SEND_ATTEMPT",label="本系统发令结果"}};
            return result;
        }
        internal void ExportHistory(HistoryQuery query,TextWriter writer)
        {
            query.Validate();if(!querySlots.WaitOne(0))throw new InvalidOperationException("历史查询较多，请稍后重试");HistoryStorageConfiguration settings;lock(gate)settings=configuration;
            try
            {
                using(var database=new HistoryPostgresClient(root,settings))
                {
                    database.Query("BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY");
                    string[] keys={"experiment","time","eventTimeSource","code","name","value","unit","quality","hex","sourceIP","sourcePort","destinationIP","destinationPort","packet","sequence","direction","codes","names","condition","result","actualValues","status","reason","released","releaseReason","configuration","id"};
                    writer.WriteLine("试验名称,事件时间（服务器本地）,时间来源,参数代号,参数名称,工程值,单位,数据质量,十六进制源码,来源IP,来源端口,目的IP,目的端口,SPP包,帧序号,方向,指令代号,指令名称,判读条件,判读结果,实际值,状态,原因,是否解除,解除原因,配置版本,事件ID");query.Size=500;query.Cursor="";
                    while(true)
                    {
                        var page=(Dictionary<string,object>)QueryHistoryCore(database,query);var rows=(Dictionary<string,object>[])page["rows"];
                        foreach(var row in rows)
                        {
                            var cells=new List<string>();foreach(string key in keys){object item;row.TryGetValue(key,out item);string value=item==null?(key=="time"?"时间未确认":""):item is string?Convert.ToString(item):HistoryJson.Encode(item);bool number=key=="value"&&System.Text.RegularExpressions.Regex.IsMatch(value,@"^[+-]?[0-9]+(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?$");cells.Add(number?"\""+value+"\"":Monitor.CsvCell(value));}writer.WriteLine(String.Join(",",cells));
                        }
                        if(!Convert.ToBoolean(page["hasMore"]))break;query.Cursor=Convert.ToString(page["cursor"]);
                    }
                    database.Query("COMMIT");
                }
            }
            finally{querySlots.Release();}
        }
    }
}
