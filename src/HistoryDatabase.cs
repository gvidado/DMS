using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace TelemetryMonitor
{
    internal sealed partial class HistoryStorage
    {
        private HistoryArchiveCursor databaseCursor=new HistoryArchiveCursor();
        private volatile bool historyMetadataPreparing=true;
        private DateTime historyMetadataAttempt=DateTime.MinValue;
        private bool historyMaintenanceRequested;
        private readonly object maintenanceClientGate=new object();
        private HistoryPostgresClient maintenanceClient;
        private static string S(object value,string key)
        {var map=value as Dictionary<string,object>;object result;return map!=null&&map.TryGetValue(key,out result)&&result!=null?Convert.ToString(result,CultureInfo.InvariantCulture):"";}
        private static object V(object value,string key,object fallback=null)
        {var map=value as Dictionary<string,object>;object result;return map!=null&&map.TryGetValue(key,out result)&&result!=null?result:fallback;}
        private string CursorPath(HistoryStorageConfiguration settings)
        {return Path.Combine(stateDirectory,"database-"+HistoryText.Hash(settings.Host+":"+settings.Port+"/"+settings.Database+"/"+settings.Username)+".json");}
        private void InitializeDatabase(HistoryPostgresClient database)
        {
            database.Query("BEGIN");try
            {
                database.Query("SELECT pg_advisory_xact_lock(762382922)");
                var check=database.Query("SELECT to_regclass('td_history.schema_version')::text AS name");
                if(check[0]["name"]!=null){var versions=database.Query("SELECT version::text FROM td_history.schema_version");if(versions.Any(r=>r["version"]!="1"))throw new InvalidOperationException("历史数据库版本不匹配，原表保留");}
                string schema=Path.Combine(root,"database","history-schema-v1.sql");if(!File.Exists(schema))throw new InvalidOperationException("缺少数据库建表文件，请完整覆盖升级包中的database目录");
                database.Query(File.ReadAllText(schema,Encoding.UTF8));database.Query("COMMIT");
            }
            catch{try{database.Query("ROLLBACK");}catch{}throw;}
        }
        private static string CompleteLine(Stream stream)
        {
            using(var bytes=new MemoryStream()){int c;while((c=stream.ReadByte())>=0){if(c==10)return Encoding.UTF8.GetString(bytes.ToArray());bytes.WriteByte((byte)c);if(bytes.Length>1024*1024)throw new InvalidDataException("历史分段索引单行过大");}return null;}
        }
        private bool NextSegment(HistoryArchiveCursor cursor,out HistoryArchiveSegment segment,out string index,out long next)
        {
            foreach(string directory in new[]{stateDirectory})
            {
                string file=Path.Combine(directory,"archive-index.jsonl");if(!File.Exists(file))continue;long offset;cursor.Offsets.TryGetValue(file,out offset);
                using(var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite))
                {
                    if(offset>stream.Length)throw new InvalidDataException("历史分段索引比补写位置短，原文件保留："+file);
                    stream.Position=offset;string line=CompleteLine(stream);if(line==null)continue;
                    segment=HistoryJson.Decode<HistoryArchiveSegment>(line);index=file;next=stream.Position;return true;
                }
            }
            segment=null;index="";next=0;return false;
        }
        private void DatabaseLoop()
        {
            HistoryPostgresClient database=null;string cursorPath="";
            try
            {
                while(!closed)
                {
                    HistoryStorageConfiguration settings;lock(gate)settings=configuration;
                    if(!settings.DatabaseEnabled){if(database!=null){database.Dispose();database=null;}databaseWake.WaitOne(1000);continue;}
                    try
                    {
                        if(database==null||lastDatabaseRevision!=settings.Revision)
                        {
                            if(database!=null)database.Dispose();database=new HistoryPostgresClient(root,settings);InitializeDatabase(database);historyMetadataPreparing=true;cursorPath=CursorPath(settings);
                            var restoredCursor=File.Exists(cursorPath)?JsonFile.Load<HistoryArchiveCursor>(cursorPath):new HistoryArchiveCursor();
                            lock(gate){databaseCursor=restoredCursor;historyMaintenanceRequested=false;lastDatabaseRevision=settings.Revision;databaseError="";lastCommit=HistoryText.Utc(DateTime.UtcNow);}
                        }
                        HistoryArchiveSegment segment;string index;long next;
                        if(!NextSegment(databaseCursor,out segment,out index,out next)){lock(gate)historyMaintenanceRequested=true;maintenanceWake.Set();databaseWake.WaitOne(500);continue;}
                        bool existing=database.Query("SELECT id::text FROM td_history.segment WHERE id=$1::uuid",segment.Id).Count>0;
                        HistoryRecord[] records=existing?new HistoryRecord[0]:ReadSegment(segment);
                        if(!existing)Ingest(database,segment,records);
                        HistoryArchiveCursor checkpoint;
                        lock(gate){databaseCursor.Offsets[index]=next;databaseCursor.Segments++;databaseCursor.Events+=segment.Count;databaseCursor.Samples+=records.Sum(r=>r.Samples==null?0:r.Samples.Length);checkpoint=new HistoryArchiveCursor{Offsets=new Dictionary<string,long>(databaseCursor.Offsets,StringComparer.OrdinalIgnoreCase),Segments=databaseCursor.Segments,Events=databaseCursor.Events,Samples=databaseCursor.Samples};databaseError="";lastCommit=HistoryText.Utc(DateTime.UtcNow);}
                        // The transaction is already committed. A slow checkpoint filesystem must
                        // never hold the producer lock that the live receive path needs.
                        SaveHistoryCheckpoint(cursorPath,checkpoint);
                    }
                    catch(Exception ex)
                    {lock(gate)databaseError=ex.Message;lastDatabaseRevision="";if(database!=null){database.Dispose();database=null;}databaseWake.WaitOne(3000);}
                }
            }
            finally{if(database!=null)database.Dispose();}
        }
        private static void SaveHistoryCheckpoint(string path,HistoryArchiveCursor checkpoint)
        {JsonFile.Save(path,checkpoint);}
        private bool HistoryMaintenanceCurrent(string revision)
        {if(closed)return false;lock(gate)return configuration.DatabaseEnabled&&configuration.Revision==revision;}
        private void CancelHistoryMaintenance()
        {HistoryPostgresClient client;lock(maintenanceClientGate)client=maintenanceClient;if(client!=null)try{client.Cancel(2000);}catch(Exception ex){Console.Error.WriteLine("History maintenance cancellation: "+ex.Message);}}
        private void HistoryMaintenanceLoop()
        {
            string revision="";
            while(!closed)
            {
                HistoryStorageConfiguration settings;bool ready;lock(gate){settings=configuration;ready=settings.DatabaseEnabled&&historyMaintenanceRequested&&lastDatabaseRevision==settings.Revision;}
                if(!ready||!historyMetadataPreparing){maintenanceWake.WaitOne(1000);continue;}
                if(revision!=settings.Revision){revision=settings.Revision;historyMetadataAttempt=DateTime.MinValue;}
                if((DateTime.UtcNow-historyMetadataAttempt).TotalSeconds<30){maintenanceWake.WaitOne(1000);continue;}
                HistoryPostgresClient client=null;
                try
                {
                    client=new HistoryPostgresClient(root,settings,true);lock(maintenanceClientGate){if(closed)continue;maintenanceClient=client;}
                    MaintainHistoryQueryMetadata(client,revision);
                }
                catch(Exception ex){if(!closed){historyMetadataAttempt=DateTime.UtcNow;historyMetadataError="历史索引维护暂未完成，采集和入库继续运行："+ex.Message;}}
                finally{lock(maintenanceClientGate){if(Object.ReferenceEquals(maintenanceClient,client))maintenanceClient=null;}if(client!=null)client.Dispose();}
                maintenanceWake.WaitOne(1000);
            }
        }
        private void Ingest(HistoryPostgresClient database,HistoryArchiveSegment segment,HistoryRecord[] records)
        {
            database.Query("BEGIN");try
            {
                if(database.Query("INSERT INTO td_history.segment(id,archive_file,sha256,event_count) VALUES($1::uuid,$2,$3,$4::integer) ON CONFLICT DO NOTHING RETURNING id::text",segment.Id,segment.File,segment.Hash,segment.Count.ToString()).Count==0){database.Query("COMMIT");return;}
                var copies=records.Select(r=>r.DatabaseCopy()).ToArray();string events=HistoryJson.Encode(copies);
                var kinds=new HashSet<string>(records.Select(record=>record.Kind),StringComparer.Ordinal);
                if(kinds.Contains("CONFIG"))
                {
                    database.Query(@"INSERT INTO td_history.configuration(id,applied_at,sha256,content)
SELECT (e->>'Id')::uuid,(e->>'Time')::timestamptz,e->>'Reason',e->'Data'
FROM jsonb_array_elements($1::jsonb) e WHERE e->>'Kind'='CONFIG' ON CONFLICT DO NOTHING",events);
                    database.Query(@"INSERT INTO td_history.parameter_definition(configuration_id,runtime_id,reference,namespace,code,name,unit,packet_id,packet_name,definition)
SELECT (e->>'Id')::uuid,p->>'Id',COALESCE(p->>'Reference',''),COALESCE(p->>'TelemetryNamespace',''),COALESCE(p->>'Code',''),COALESCE(p->>'Name',''),COALESCE(p->>'Unit',''),COALESCE(p->>'PacketId',''),COALESCE(p->>'PacketName',''),p
FROM jsonb_array_elements($1::jsonb) e CROSS JOIN LATERAL jsonb_array_elements(e->'Data'->'Parameters') p
WHERE e->>'Kind'='CONFIG' ON CONFLICT DO NOTHING",events);
                }
                var addedEvents=database.Query(@"INSERT INTO td_history.event(id,kind,experiment_id,session_id,boot_id,observed_at,received_at,observed_ticks,received_ticks,monotonic_ticks,ordinal,time_basis,configuration_id,source_ip,destination_ip,source_port,destination_port,direction,mode,packet_id,packet_name,outer_source_id,apid,frame_sequence,status,reason,payload_sha256,payload_length,segment_id,archive_line,data,capture_time_confirmed,capture_ticks,capture_event_id)
SELECT (e->>'Id')::uuid,e->>'Kind',NULLIF(e->>'Experiment','')::uuid,NULLIF(e->>'Session','')::uuid,(e->>'Boot')::uuid,
CASE WHEN confirmed AND e->>'TimeBasis'='PCAP_CAPTURE' THEN COALESCE(NULLIF(e->>'CaptureTime',''),e->>'Time') ELSE e->>'Received' END::timestamptz,(e->>'Received')::timestamptz,
CASE WHEN confirmed AND e->>'TimeBasis'='PCAP_CAPTURE' THEN COALESCE(NULLIF((e->>'CaptureTicks')::bigint,0),(e->>'ObservedTicks')::bigint) ELSE (e->>'ReceivedTicks')::bigint END,
(e->>'ReceivedTicks')::bigint,(e->>'MonotonicTicks')::bigint,(e->>'Ordinal')::bigint,e->>'TimeBasis',NULLIF(e->>'Configuration','')::uuid,NULLIF(e->>'SourceIP','')::inet,NULLIF(e->>'DestinationIP','')::inet,
(e->>'SourcePort')::integer,(e->>'DestinationPort')::integer,e->>'Direction',e->>'Mode',e->>'PacketId',e->>'PacketName',(e->>'OuterSourceId')::integer,(e->>'Apid')::integer,(e->>'Sequence')::integer,e->>'Status',e->>'Reason',NULLIF(e->>'PayloadSha',''),COALESCE((e->'Data'->>'payloadLength')::integer,0),$2::uuid,n::integer,e,confirmed,
CASE WHEN confirmed THEN COALESCE(NULLIF((e->>'CaptureTicks')::bigint,0),(e->>'ObservedTicks')::bigint) END,
COALESCE(NULLIF(e->>'CaptureEventId',''),NULLIF(e->'Data'->>'captureEventId',''))::uuid
FROM jsonb_array_elements($1::jsonb) WITH ORDINALITY a(e,n)
CROSS JOIN LATERAL (SELECT COALESCE((e->>'CaptureTimeConfirmed')::boolean,e->>'TimeBasis' IN ('NPCAP_RECEIVE','NPCAP_CAPTURE','PCAP_CAPTURE')) AS confirmed) capture WHERE e->>'Kind'<>'JUDGMENT' ON CONFLICT DO NOTHING RETURNING 1 AS inserted",events,segment.Id);
                var samplePackets=records.Where(r=>r.Kind!="JUDGMENT"&&r.Samples!=null&&r.Samples.Length>0).ToArray();
                if(samplePackets.Length>0)StoreHistorySamples(database,samplePackets,addedEvents.Count==records.Length);
                if(kinds.Contains("EXPERIMENT"))database.Query(@"INSERT INTO td_history.experiment(id,name,started_at,ended_at,notes)
SELECT (e->'Data'->>'Id')::uuid,e->'Data'->>'Name',(e->'Data'->>'StartedAt')::timestamptz,NULLIF(e->'Data'->>'EndedAt','')::timestamptz,COALESCE(e->'Data'->>'Notes','')
FROM jsonb_array_elements($1::jsonb) e WHERE e->>'Kind'='EXPERIMENT'
ON CONFLICT(id) DO UPDATE SET name=EXCLUDED.name,ended_at=EXCLUDED.ended_at,notes=EXCLUDED.notes",events);
                if(kinds.Contains("COMMAND_CAPTURE")||kinds.Contains("COMMAND_SEND"))database.Query(@"INSERT INTO td_history.command_event(event_id,evidence,verified,codes,names,success,actor)
SELECT (e->>'Id')::uuid,COALESCE(e->'Data'->>'evidence',''),COALESCE((e->'Data'->>'verified')::boolean,false),ARRAY(SELECT jsonb_array_elements_text(e->'Data'->'codes')),ARRAY(SELECT jsonb_array_elements_text(e->'Data'->'names')),(e->'Data'->>'success')::boolean,COALESCE(e->'Data'->>'actor','')
FROM jsonb_array_elements($1::jsonb) e WHERE e->>'Kind' IN ('COMMAND_CAPTURE','COMMAND_SEND') ON CONFLICT DO NOTHING",events);
                if(kinds.Contains("INCIDENT"))database.Query(@"INSERT INTO td_history.incident(event_id,incident_key,source_kind,condition,value_text)
SELECT (e->>'Id')::uuid,COALESCE(e->'Data'->>'key',''),COALESCE(e->'Data'->>'sourceKind',''),COALESCE(e->'Data'->>'condition',''),COALESCE(e->'Data'->>'value','')
FROM jsonb_array_elements($1::jsonb) e WHERE e->>'Kind'='INCIDENT' ON CONFLICT DO NOTHING",events);
                foreach(var release in records.Where(r=>r.Kind=="INCIDENT_RELEASE"))database.Query("UPDATE td_history.incident SET released=true,release_reason=$2,released_at=$3::timestamptz,actor=$4 WHERE incident_key=$1",S(release.Data,"key"),S(release.Data,"reason"),release.Time,S(release.Data,"actor"));
                // Keep original ordinality above: archive_line must still point into the
                // untouched legacy segment after retired judgments have been skipped.
                string choices=kinds.Contains("JUDGMENT")?HistoryJson.Encode(copies.Where(r=>r.Kind!="JUDGMENT").ToArray()):events;
                StoreHistoryQueryChoices(database,choices,samplePackets);
                database.Query("COMMIT");
                if(kinds.Contains("CONFIG"))System.Threading.Interlocked.Increment(ref historyDefinitionGeneration);
            }
            catch{try{database.Query("ROLLBACK");}catch{}throw;}
        }
        private static void StoreHistorySamples(HistoryPostgresClient database,HistoryRecord[] packets,bool allEventsNew)
        {
            // Each packet's ID and configuration travel once; PostgreSQL reads its timestamp
            // from the persisted event. Use the archive's typed values directly.
            string batch=HistoryJson.Encode(packets.Select(packet=>new{packet=packet.Id,configuration=packet.Configuration,samples=packet.Samples}).ToArray());
            long expected=packets.Sum(packet=>(long)packet.Samples.Length);
            var result=database.Query(@"WITH inserted AS (
INSERT INTO td_history.sample(packet_event_id,parameter_id,observed_at,value_text,value_kind,value_float,value_exact,raw_text,hex_source,quality,diagnostic)
SELECT a.packet,p.id,e.observed_at,s.""Value"",s.""ValueKind"",CASE WHEN s.""ValueKind""='number' THEN NULLIF(s.""Value"",'')::double precision END,CASE WHEN s.""ValueKind""='integer' THEN NULLIF(s.""Value"",'')::numeric END,s.""Raw"",s.""Hex"",COALESCE(s.""Quality"",'未知'),s.""Diagnostic""
FROM jsonb_to_recordset($1::jsonb) a(packet uuid,configuration uuid,samples jsonb)
JOIN td_history.event e ON e.id=a.packet
CROSS JOIN LATERAL jsonb_to_recordset(a.samples) s(""ParameterId"" text,""Value"" text,""ValueKind"" text,""Raw"" text,""Hex"" text,""Quality"" text,""Diagnostic"" text)
JOIN td_history.parameter_definition p ON p.configuration_id=a.configuration AND p.runtime_id=s.""ParameterId""
ON CONFLICT DO NOTHING RETURNING 1)
SELECT count(*)::text AS count FROM inserted",batch);
            if(allEventsNew&&Int64.Parse(result[0]["count"],CultureInfo.InvariantCulture)==expected)return;

            // Retries can contain existing event IDs. Keep the complete-count check so an
            // absent parameter mapping rolls back the whole batch and remains recoverable.
            var persisted=database.Query("SELECT count(*)::text AS count FROM td_history.sample WHERE packet_event_id=ANY($1::uuid[])","{"+String.Join(",",packets.Select(packet=>packet.Id))+"}");
            if(Int64.Parse(persisted[0]["count"],CultureInfo.InvariantCulture)!=expected)throw new InvalidDataException("历史样本数与配置映射不一致，整批保留待补写");
        }
        internal object DatabaseProgress()
        {lock(gate)return new{segments=databaseCursor.Segments,events=databaseCursor.Events,samples=databaseCursor.Samples};}
        private const string HistoryChoiceUpsert=@" ON CONFLICT(category,value) DO UPDATE SET
names=(SELECT COALESCE(array_agg(DISTINCT name ORDER BY name),'{}'::text[]) FROM unnest(td_history.query_choice.names||EXCLUDED.names) name WHERE name<>''),
first_at=LEAST(td_history.query_choice.first_at,EXCLUDED.first_at),last_at=GREATEST(td_history.query_choice.last_at,EXCLUDED.last_at),metadata=td_history.query_choice.metadata||EXCLUDED.metadata";
        private void StoreHistoryQueryChoices(HistoryPostgresClient database,string events,HistoryRecord[] packets)
        {
            // For the quality filter only the observed time bounds per value matter.
            // Repeated samples in one packet have the same time and add no information.
            string qualities=HistoryJson.Encode(packets.SelectMany(packet=>packet.Samples
                .Select(sample=>sample.Quality).Where(quality=>!String.IsNullOrEmpty(quality))
                .Distinct(StringComparer.Ordinal).Select(quality=>new{quality,time=packet.Time})).ToArray());
            database.Query(@"INSERT INTO td_history.query_choice(category,value,names,first_at,last_at,metadata)
WITH inputs AS (
SELECT v.category,v.value,v.name,(e->>'Time')::timestamptz AS at,COALESCE(v.metadata,'{}'::jsonb) AS metadata
FROM jsonb_array_elements($1::jsonb) e CROSS JOIN LATERAL (VALUES
('source',e->>'SourceIP',NULL::text,NULL::jsonb),('destination',e->>'DestinationIP',NULL::text,NULL::jsonb),
('packet',e->>'PacketId',e->>'PacketName',NULL::jsonb),('status',e->>'Status',NULL::text,NULL::jsonb),
('timeBasis',e->>'TimeBasis',NULL::text,NULL::jsonb),('direction',e->>'Direction',NULL::text,NULL::jsonb),
('session',e->>'Session',NULL::text,jsonb_strip_nulls(jsonb_build_object('mode',NULLIF(e->>'Mode',''),'experimentId',NULLIF(e->>'Experiment',''))))) v(category,value,name,metadata)
UNION ALL
SELECT 'command',code,name,(e->>'Time')::timestamptz,'{}'::jsonb FROM jsonb_array_elements($1::jsonb) e
CROSS JOIN LATERAL jsonb_array_elements_text(e->'Data'->'codes') code
LEFT JOIN LATERAL jsonb_array_elements_text(e->'Data'->'names') name ON true WHERE e->>'Kind' IN ('COMMAND_CAPTURE','COMMAND_SEND')
UNION ALL
SELECT 'namespace',p->>'TelemetryNamespace',NULL::text,(e->>'Time')::timestamptz,'{}'::jsonb FROM jsonb_array_elements($1::jsonb) e CROSS JOIN LATERAL jsonb_array_elements(e->'Data'->'Parameters') p WHERE e->>'Kind'='CONFIG'
UNION ALL
SELECT 'quality',s->>'quality',NULL::text,(s->>'time')::timestamptz,'{}'::jsonb FROM jsonb_array_elements($2::jsonb) s)
SELECT category,value,COALESCE(array_agg(DISTINCT name ORDER BY name) FILTER(WHERE name IS NOT NULL AND name<>''),'{}'::text[]),min(at),max(at),(array_agg(metadata ORDER BY (metadata='{}'::jsonb) ASC))[1]
FROM inputs WHERE value IS NOT NULL AND value<>'' GROUP BY category,value"+HistoryChoiceUpsert,events,qualities);
        }
        private void MaintainHistoryQueryMetadata(HistoryPostgresClient database,string revision)
        {
            if(!HistoryMaintenanceCurrent(revision)||!historyMetadataPreparing||(DateTime.UtcNow-historyMetadataAttempt).TotalSeconds<30)return;historyMetadataAttempt=DateTime.UtcNow;
            try
            {
                // A separate connection starts maintenance after the durable backlog catches up.
                // New segments continue ingesting even if an index waits on an older transaction.
                database.Query("SET statement_timeout=300000");
                if(database.Query("SELECT name FROM td_history.query_metadata_state WHERE name='capture-receipt-window-v103'").Count==0)
                {
                    database.Query(@"WITH changed AS (UPDATE td_history.event SET observed_at=received_at,observed_ticks=received_ticks WHERE NOT COALESCE(capture_time_confirmed,time_basis IN ('NPCAP_RECEIVE','NPCAP_CAPTURE','PCAP_CAPTURE')) AND (observed_at<>received_at OR observed_ticks<>received_ticks) RETURNING id,observed_at) UPDATE td_history.sample s SET observed_at=c.observed_at FROM changed c WHERE s.packet_event_id=c.id");
                    database.Query("INSERT INTO td_history.query_metadata_state(name) VALUES('capture-receipt-window-v103') ON CONFLICT DO NOTHING");
                }
                foreach(var index in new[]{
                    new[]{"history_event_query_order_v103","CREATE INDEX CONCURRENTLY IF NOT EXISTS history_event_query_order_v103 ON td_history.event(kind,observed_at DESC,observed_ticks DESC,ordinal DESC,id DESC)"},
                    new[]{"history_event_query_trial_v103","CREATE INDEX CONCURRENTLY IF NOT EXISTS history_event_query_trial_v103 ON td_history.event(experiment_id,kind,observed_at DESC,observed_ticks DESC,ordinal DESC,id DESC)"},
                    new[]{"history_event_query_session_v103","CREATE INDEX CONCURRENTLY IF NOT EXISTS history_event_query_session_v103 ON td_history.event(session_id,kind,observed_at DESC,observed_ticks DESC,ordinal DESC,id DESC)"},
                    new[]{"history_server_time_v107","CREATE INDEX CONCURRENTLY IF NOT EXISTS history_server_time_v107 ON td_history.event(kind,"+HistoryEventTimeSql.Replace("e.","")+" DESC,"+HistoryEventTicksSql.Replace("e.","")+" DESC,ordinal DESC,id DESC)"},
                    new[]{"history_server_trial_time_v107","CREATE INDEX CONCURRENTLY IF NOT EXISTS history_server_trial_time_v107 ON td_history.event(experiment_id,kind,"+HistoryEventTimeSql.Replace("e.","")+" DESC,"+HistoryEventTicksSql.Replace("e.","")+" DESC,ordinal DESC,id DESC)"},
                    new[]{"history_server_session_time_v107","CREATE INDEX CONCURRENTLY IF NOT EXISTS history_server_session_time_v107 ON td_history.event(session_id,kind,"+HistoryEventTimeSql.Replace("e.","")+" DESC,"+HistoryEventTicksSql.Replace("e.","")+" DESC,ordinal DESC,id DESC)"}})
                {
                    if(!HistoryMaintenanceCurrent(revision))return;var state=database.Query("SELECT i.indisvalid::text AS valid FROM pg_index i JOIN pg_class c ON c.oid=i.indexrelid JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='td_history' AND c.relname=$1",index[0]);
                    if(state.Count>0&&state[0]["valid"]!="true")database.Query("DROP INDEX CONCURRENTLY td_history."+index[0]);
                    if(state.Count==0||state[0]["valid"]!="true")database.Query(index[1]);
                }
                string[] simple={"source","destination","packet","status","timeBasis","direction","session"};
                foreach(string category in simple)
                {
                    if(!HistoryMaintenanceCurrent(revision))return;string marker="choice-v103-"+category;if(database.Query("SELECT name FROM td_history.query_metadata_state WHERE name=$1",marker).Count>0)continue;
                    string value=category=="source"?"host(source_ip)":category=="destination"?"host(destination_ip)":category=="packet"?"packet_id":category=="status"?"status":category=="timeBasis"?"time_basis":category=="direction"?"direction":"session_id::text";
                    string names=category=="packet"?"COALESCE(array_agg(DISTINCT packet_name ORDER BY packet_name) FILTER(WHERE packet_name<>''),'{}'::text[])":"'{}'::text[]";
                    database.Query("INSERT INTO td_history.query_choice(category,value,names,first_at,last_at,metadata) SELECT $1,"+value+","+names+",min(observed_at),max(observed_at),'{}'::jsonb FROM td_history.event WHERE "+value+" IS NOT NULL AND "+value+"<>'' GROUP BY "+value+HistoryChoiceUpsert,category);
                    database.Query("INSERT INTO td_history.query_metadata_state(name) VALUES($1) ON CONFLICT DO NOTHING",marker);
                }
                foreach(var item in new[]{
                    new[]{"command",@"SELECT 'command',code,COALESCE(array_agg(DISTINCT name ORDER BY name) FILTER(WHERE name IS NOT NULL AND name<>''),'{}'::text[]),min(e.observed_at),max(e.observed_at),'{}'::jsonb FROM td_history.command_event c JOIN td_history.event e ON e.id=c.event_id CROSS JOIN LATERAL unnest(c.codes) code LEFT JOIN LATERAL unnest(c.names) name ON true WHERE code<>'' GROUP BY code"},
                    new[]{"quality",@"SELECT 'quality',quality,'{}'::text[],min(observed_at),max(observed_at),'{}'::jsonb FROM td_history.sample WHERE quality<>'' GROUP BY quality"},
                    new[]{"namespace",@"SELECT 'namespace',namespace,'{}'::text[],now(),now(),'{}'::jsonb FROM td_history.parameter_definition WHERE namespace<>'' GROUP BY namespace"}})
                {
                    if(!HistoryMaintenanceCurrent(revision))return;string marker="choice-v103-"+item[0];if(database.Query("SELECT name FROM td_history.query_metadata_state WHERE name=$1",marker).Count>0)continue;
                    database.Query("INSERT INTO td_history.query_choice(category,value,names,first_at,last_at,metadata) "+item[1]+HistoryChoiceUpsert);database.Query("INSERT INTO td_history.query_metadata_state(name) VALUES($1) ON CONFLICT DO NOTHING",marker);
                }
                if(HistoryMaintenanceCurrent(revision)){historyMetadataPreparing=false;historyMetadataError="";}
            }
            catch(Exception ex){if(!closed&&HistoryMaintenanceCurrent(revision))historyMetadataError="历史选择项正在补齐，已有查询与采集继续运行："+ex.Message;}
        }
        internal object TestConnection(HistoryStorageConfiguration settings,bool initialize)
        {settings.Validate();using(var database=new HistoryPostgresClient(root,settings)){var rows=database.Query("SELECT version() AS version,current_database() AS database,pg_is_in_recovery()::text AS recovery");if(initialize)InitializeDatabase(database);return new{ok=true,server=rows[0],initialized=initialize};}}
    }
}
