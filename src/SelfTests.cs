using System;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;

namespace TelemetryMonitor
{
    public static class SelfTests
    {
        private static int count;
        private static void Check(bool ok, string label)
        { count++; if (!ok) throw new Exception("FAIL: " + label); Console.WriteLine("PASS " + label); }
        private static Settings Config()
        { return new Settings { UdpPort = 41001, SppOffset = -1, MappingConfirmed = true, BitOrder = "lsb0" }; }
        private static byte[] Packet(int sequence)
        {
            byte[] data = new byte[1030]; int o = 6;
            data[0]=8;data[1]=2;data[4]=3;data[5]=255;
            data[o] = 8; data[o + 1] = 8; data[o + 2] = (byte)(192 | (sequence >> 8)); data[o + 3] = (byte)sequence;
            data[o + 4] = 3; data[o + 5] = 249; data[o + 6] = 48; data[o + 7] = 3; data[o + 8] = 25;
            data[o + 12] = 0x10; data[o + 13] = 4;
            Sum(data); return data;
        }
        private static void Sum(byte[] data)
        { SppChecksum.Write(data, 6, data.Length - 6); }
        private static void Reject(Action action, string label)
        { try { action(); } catch (ArgumentException) { Check(true, label); return; } Check(false, label); }
        private static Reading Read(byte[] data, Parameter parameter, Settings cfg)
        { Sum(data); return Protocol.Decode(data, Protocol.Parse(data, cfg), parameter, cfg, DateTime.UtcNow); }
        public static int Run()
        {
            try
            {
                Settings cfg = Config(); byte[] data = Packet(16383); Packet p = Protocol.Parse(data, cfg);
                Check(p.Offset == 6 && p.Length == 1024 && p.Apid == 8 && p.Sequence == 16383 && p.ChecksumVerified, "SPP auto offset, APID, 14-bit sequence, checksum");
                data[100] ^= 1; Reject(delegate { Protocol.Parse(data, cfg); }, "reject bad checksum");
                data = Packet(1); data[11] = 248; Reject(delegate { Protocol.Parse(data, cfg); }, "reject inconsistent length");
                data = Packet(1); data[7] = 20; Reject(delegate { Protocol.Parse(data, cfg); }, "reject unknown APID");
                data = Packet(1); data[8] = 0; Reject(delegate { Protocol.Parse(data, cfg); }, "reject segmented SPP");
                data = Packet(1); foreach (int i in new[] { 12, 15, 16, 17, 20, 21 }) data[i] = (byte)(0xF0 + i - 12); Sum(data);
                Check(Protocol.Parse(data, cfg).Sequence == 1, "informational secondary fields do not gate legacy receiver");
                foreach (int i in new[] { 13, 14, 18, 19 })
                { data = Packet(1); data[i] ^= 1; Sum(data); Reject(delegate { Protocol.Parse(data, cfg); }, "legacy receiver requires configured routing field at byte " + i); }
                Reject(delegate { Protocol.Parse(new byte[12], cfg); }, "reject short datagram");
                data = Packet(1); cfg.ChecksumMode = "unverified"; cfg.ChecksumStart = 16;
                Check(Protocol.Parse(data, cfg).ChecksumVerified, "old unverified fields cannot bypass the fixed Fletcher check");
                data[100] ^= 1; Reject(delegate { Protocol.Parse(data, cfg); }, "old unverified fields still reject damaged data");
                cfg.Validate(); Check(cfg.ChecksumMode == SppChecksum.Mode && cfg.ChecksumStart == 0, "legacy settings normalize to Fletcher-16 at SPP start"); cfg = Config();
                data = Packet(1); data[0] = 0xAA; data[5] = 0xBB;
                Check(Protocol.Parse(data, cfg).ChecksumVerified, "UDP prefix does not participate in Fletcher check");
                byte[] vector = new byte[] { 97, 98, 99, 100, 101, 0, 0 }; SppChecksum.Write(vector, 0, vector.Length);
                Check(vector[5] == 0x46 && vector[6] == 0xC8 && SppChecksum.Verify(vector, 0, vector.Length), "independent abcde Fletcher check-byte vector, odd packet length");
                SequenceTracker tracker = new SequenceTracker();
                Check(tracker.Accept(16382) == 0 && tracker.Lost == 0, "first sequence");
                Check(tracker.Accept(16382) == 1, "duplicate rejected");
                Check(tracker.Accept(16383) == 0 && tracker.Accept(0) == 0 && tracker.Lost == 0 && !tracker.Reset, "14-bit natural wrap");
                Check(tracker.Accept(3) == 0 && tracker.Lost == 2, "missing frame count");
                Check(tracker.Accept(2) == 0 && tracker.Reset && tracker.Lost == 0, "backward counter outside recent five is accepted and rebased");
                Check(tracker.Accept(0) == 1 && !tracker.Reset, "zero within the recent-five window is duplicate rather than a restart");
                tracker = new SequenceTracker(); tracker.Accept(500);
                Check(tracker.Accept(0) == 0 && tracker.Reset, "restart at zero outside recent-five window rebases the counter");
                Check(tracker.Accept(0) == 1, "repeated reset frame is duplicate");
                tracker = new SequenceTracker(); tracker.Accept(500); Check(tracker.Accept(1) == 0 && tracker.Reset, "restart at one");
                tracker = new SequenceTracker(); for (int i = 100; i <= 104; i++) tracker.Accept(i);
                Check(tracker.Accept(100) == 1, "sequence at the fifth received position is duplicate");
                tracker = new SequenceTracker(); for (int i = 100; i <= 105; i++) tracker.Accept(i);
                Check(tracker.Accept(100) == 0 && tracker.Reset, "sequence outside recent five is accepted without a catch-up wait");
                Check(tracker.Accept(102) == 1, "rebasing does not erase other sequence numbers still in the five-reception window");
                Check(tracker.Accept(100) == 1, "an accepted rebased frame enters the five-frame duplicate window");
                tracker = new SequenceTracker(); tracker.Accept(5000);
                Check(tracker.Accept(3125) == 0 && tracker.Reset && tracker.Previous == 5000 && tracker.Accept(3126) == 0 && !tracker.Reset, "five-minute counter rollback resumes on its first frame and continues normally");
                tracker = new SequenceTracker(); tracker.Accept(5000);
                Check(tracker.Accept(1250) == 0 && tracker.Reset && tracker.Accept(1251) == 0, "ten-minute counter rollback never waits for the prior sequence");
                tracker = new SequenceTracker(); tracker.Accept(100);
                Check(tracker.Accept(10000) == 0 && tracker.Reset && tracker.Lost == 0, "an ambiguous large forward jump is accepted without inventing missing frames");
                tracker = new SequenceTracker(); tracker.Accept(100); for (int i = 0; i < 5; i++) tracker.Accept(100);
                tracker.Accept(101); tracker.Accept(102);
                Check(tracker.Accept(100) == 1, "duplicate receptions occupy window positions without premature hash eviction");
                data = Packet(1); data[22] = 255; data[23] = 254;
                var q = new Parameter { Id = "signed", Type = "int", Offset = 0, Length = 2, Scale = 0.5 };
                Check(Read(data, q, cfg).Value == "-1" && Read(data, q, cfg).HexSource == "0xFFFE", "signed int16 and scaling, hex preserves two's-complement source before scaling");
                q = new Parameter { Id = "u40", Type = "uint", Offset = 0, Length = 5 };
                data[22] = 1; data[23] = 2; data[24] = 3; data[25] = 4; data[26] = 5;
                Check(Read(data, q, cfg).Raw == "4328719365" && Read(data, q, cfg).HexSource == "0x0102030405", "big-endian 40-bit integer and leading-zero hex source");
                q = new Parameter { Id = "f32", Type = "float32", Offset = 0, Length = 4 };
                data[22] = 0xC0; data[23] = 0x20; data[24] = 0; data[25] = 0;
                Check(Read(data, q, cfg).Value == "-2.5" && Read(data, q, cfg).HexSource == "0xC0200000", "big-endian IEEE754 float32 and original bit pattern");
                data[22] = 0x7F; data[23] = 0xC0; Check(Read(data, q, cfg).Status == "解码无效" && Read(data, q, cfg).HexSource == "0x7FC00000", "NaN invalid quality retains available hex source");
                q = new Parameter { Id = "bits", Type = "uint", Offset = 0, Length = 1, BitStart = 0, BitCount = 1 };
                data[22] = 0x80; Check(Read(data, q, cfg).Raw == "0", "B0 least significant bit");
                cfg.BitOrder = new Settings().BitOrder; Check(Read(data, q, cfg).Raw == "1", "default B0 most significant bit");
                q.Enums["1"] = "工作"; Check(Read(data, q, cfg).Value == "工作", "enum rendering");
                q.Offset = 8000; Check(Read(data, q, cfg).Status == "解码无效", "out-of-range field invalid quality");
                cfg = Config(); data = Packet(1); data[22] = 0x80; data[23] = 9;
                q = new Parameter { Id = "cross", Type = "uint", Offset = 0, Length = 2, BitStart = 7, BitCount = 5, CrossByte = true };
                Check(Read(data, q, cfg).Raw == "25" && Read(data, q, cfg).HexSource == "0x19", "confirmed default first bit is highest, descending significance and extracted hex");
                cfg.CrossByteOrder = "unverified";
                Check(Read(data, q, cfg).Status == "解码无效", "cross-byte order is never guessed");
                cfg.CrossByteOrder = "firstLsb"; Check(Read(data, q, cfg).Raw == "19", "cross-byte first bit as least significant");
                cfg.CrossByteOrder = "firstMsb"; Check(Read(data, q, cfg).Raw == "25", "cross-byte first bit as most significant");
                cfg.ByteOrder = "little"; q = new Parameter { Id = "u24", Type = "uint", Offset = 0, Length = 3 };
                data[22] = 1; data[23] = 2; data[24] = 3; Check(Read(data, q, cfg).Raw == "197121" && Read(data, q, cfg).HexSource == "0x030201", "little-endian uint24 and interpreted hex source");
                cfg.ByteOrder = "big"; q.Type = "int"; q.Scale = .001; data[22] = 255; data[23] = 255; data[24] = 254;
                Check(Read(data, q, cfg).Value == "-0.002", "signed 24-bit scaled field");
                q = new Parameter { Id = "double", Type = "float64", Offset = 0, Length = 8 };
                byte[] doubleBytes = BitConverter.GetBytes(12.75); if (BitConverter.IsLittleEndian) Array.Reverse(doubleBytes); Array.Copy(doubleBytes, 0, data, 22, 8);
                Check(Read(data, q, cfg).Value == "12.75", "big-endian float64");
                for (int i = 22; i < 30; i++) data[i] = 255;
                q = new Parameter { Id = "u64hex", Type = "uint", Offset = 0, Length = 8 };
                Check(Read(data, q, cfg).HexSource == "0xFFFFFFFFFFFFFFFF", "64-bit hex source preserves every bit without browser-number rounding");
                q = new Parameter { Id = "range", Type = "uint", Offset = 0, Length = 1 }; q.EnumRanges.Add(new EnumRange { Min = 2, Max = 7, Text = "区间含义" }); data[22] = 5;
                Check(Read(data, q, cfg).Value == "区间含义", "range enumeration");
                data = Packet(3); data[18] = 0x10; data[19] = 4; Sum(data);
                var definition = new PacketDefinition { Id = "test", OuterSourceId=0x0802,ChecksumMode = "sum16be", ChecksumStart = 16, Groups = new List<string> { "test" } };
                definition.Validate(new HashSet<string> { "test" });
                Check(definition.ChecksumMode == SppChecksum.Mode && definition.ChecksumStart == 0, "legacy packet settings normalize to fixed Fletcher checksum");
                Check(PacketCodec.Parse(data, definition).Sequence == 3, "manual SPP information parses valid packet");
                definition.ExpectedLengthField=7;
                Check(PacketCodec.Parse(data,definition).ChecksumVerified,"configured SPP data length is informational and does not reject a valid packet");
                var differentLength=new byte[data.Length+4];Array.Copy(data,0,differentLength,0,data.Length-2);
                int outerLength=differentLength.Length-7,sppLength=differentLength.Length-13;
                differentLength[4]=(byte)(outerLength>>8);differentLength[5]=(byte)outerLength;differentLength[10]=(byte)(sppLength>>8);differentLength[11]=(byte)sppLength;Sum(differentLength);
                Check(PacketCodec.Parse(differentLength,definition).ChecksumVerified,"one SPP configuration accepts another structurally valid payload length");
                differentLength[11]--;Sum(differentLength);Reject(delegate{PacketCodec.Parse(differentLength,definition);},"SPP actual length consistency still rejects a malformed packet");
                var bDefinition=JsonFile.Decode<PacketDefinition>(JsonFile.Encode(definition));bDefinition.Id="test-b";bDefinition.OuterSourceId=0x0202;
                var bData=(byte[])data.Clone();bData[0]=2;bData[1]=2;
                Check(PacketCodec.Resolve(data,null,new[]{definition,bDefinition}).Definition.Id=="test" && PacketCodec.Resolve(bData,null,new[]{definition,bDefinition}).Definition.Id=="test-b","outer source separates identical inner SPP routing fields");
                Check(PacketCodec.Parse(bData,bDefinition).ChecksumVerified,"six-byte source envelope is excluded from SPP Fletcher checksum");
                bData[5]--;Reject(delegate{PacketCodec.Parse(bData,bDefinition);},"outer length uses field plus one independently of SPP length");
                Check(TelemetryReferences.NamespaceFor(0x0202)=="TMB" && TelemetryReferences.NamespaceFor(0x0422)=="TMB" && TelemetryReferences.NamespaceFor(0x0802)=="TME","source namespace classification");
                Check(new Parameter{Code="a1",TelemetryNamespace="TMB"}.ValueReference=="TMB.a1.value","source-qualified references preserve Excel code case");
                definition.SourceAddress = 4101; Reject(delegate { PacketCodec.Parse(data, definition); }, "source address mismatch rejects a packet"); definition.SourceAddress = 4100;
                foreach (int i in new[] { 12, 15, 16, 17, 20, 21 }) data[i] = (byte)(0xE0 + i - 12); Sum(data);
                Check(PacketCodec.Parse(data, definition).Sequence == 3, "PUS flags, counters, reserved fields and destination fields remain informational");
                foreach (int i in new[] { 13, 14, 18, 19 })
                { byte[] changed = Packet(3); changed[i] ^= 1; Sum(changed); Reject(delegate { PacketCodec.Parse(changed, definition); }, "native parser requires configured routing field at byte " + i); }
                data[20] ^= 1; Reject(delegate { PacketCodec.Parse(data, definition); }, "checksum still covers changed secondary-header bytes");
                Check(PacketCodec.Hex("0x08, 08\nC0 01").Length == 4, "manual hex input separators");
                Reject(delegate { PacketCodec.Hex("08 XX"); }, "invalid manual hex rejected");
                Check(Protocol.InProfile(new Parameter { Group = "工仪RVD200外" }, "RVD200外") && !Protocol.InProfile(new Parameter { Group = "工仪RVD200外" }, "RVD200内"), "overlapping layouts isolated");
                cfg.UdpPort = 70000; Reject(delegate { cfg.Validate(); }, "invalid UDP port");
                var referenceParameter = JsonFile.Decode<Parameter>("{\"Id\":\"table/a1\",\"Code\":\"a1\",\"Reference\":\"OLD.a1\",\"ValueReference\":\"OLD.a1.raw\"}");
                Check(referenceParameter.Reference == "TM.a1" && referenceParameter.ValueReference == "TM.a1.value", "old or forged reference metadata is derived again from the parameter code");
                string referenceJson = JsonFile.Encode(referenceParameter);
                Check(referenceJson.Contains("\"Reference\":\"TM.a1\"") && JsonFile.Decode<Parameter>(referenceJson).Id == "table/a1", "TM reference export and re-import retain stable IDs");
                referenceParameter.Code = "b1";
                Check(referenceParameter.ValueReference == "TM.b1.value" && TelemetryReferences.Name("A1") == "TM.A1", "references track code changes and preserve letter case");
                Check(TelemetryReferences.Name("TM.a1") == "TM.a1", "existing TM prefix is not duplicated");
                var referenceCatalog = new TelemetryCatalog { Parameters = new List<Parameter> { new Parameter { Id="one", Code="a1", Group="one" }, new Parameter { Id="two", Code="a1", Group="two" } } };
                Check(referenceCatalog.ReferenceIssues.Count == 2 && referenceCatalog.Parameters.Count == 2, "cross-sheet duplicate TM codes remain visible and produce reference diagnostics");
                Check(JsonFile.Decode<TelemetryCatalog>(JsonFile.Encode(referenceCatalog)).ReferenceIssues.Count == 2, "derived reference diagnostics survive JSON cloning without duplication");
                referenceCatalog.Parameters[1].Code = "b1";
                Check(referenceCatalog.ReferenceIssues.Count == 0, "correcting a duplicate code removes its derived diagnostic");
                referenceCatalog.Parameters[1].Code = "bad-code";
                Check(referenceCatalog.ReferenceIssues.Count == 1, "codes incompatible with dot notation receive an explicit diagnostic");
                data = Packet(1); for (int i = 0; i < 8; i++) data[22+i] = 255;
                Reading exactUnsigned = Read(data, new Parameter { Id="exact", Type="uint", Offset=0, Length=8 }, cfg);
                Check(exactUnsigned.EngineeringValue is ulong && (ulong)exactUnsigned.EngineeringValue == UInt64.MaxValue && exactUnsigned.EngineeringText == "18446744073709551615", "unscaled uint64 engineering value retains every integer bit and exact text");
                Reading exactSigned = Read(data, new Parameter { Id="exact", Type="int", Offset=0, Length=8 }, cfg);
                Check(exactSigned.EngineeringValue is long && (long)exactSigned.EngineeringValue == -1, "signed integer engineering values retain signed integer type");
                byte[] shortInterface=CommandEnvelope.BuildSTC(222,92,new byte[]{0xAA,0x39});
                Check(shortInterface.Length==32&&shortInterface[4]==24&&shortInterface[22]==92&&shortInterface[23]==0&&shortInterface[24]==0&&shortInterface[25]==0&&shortInterface[26]==0xAA&&shortInterface[27]==0x39,"non-AA function IDs use four interface bytes with data at byte 27");
                Check(CommandEnvelope.Valid(shortInterface)&&CommandEnvelope.DataOffset(shortInterface)==26&&CommandEnvelope.DataContent(shortInterface).SequenceEqual(new byte[]{0xAA,0x39}),"uniform interface length, checksum and data extraction agree");
                byte[] longInterface=CommandEnvelope.BuildSTC(0x1553,0x12345678,new byte[]{0xAA,0x39});
                Check(longInterface.Length==32&&longInterface.Skip(22).Take(4).SequenceEqual(new byte[]{0x78,0x56,0x34,0x12})&&CommandEnvelope.DataOffset(longInterface)==26,"1553 interface remains four-byte little endian with data at byte 27");
                Check(CommandEnvelope.Valid(longInterface)&&CommandEnvelope.DataContent(longInterface).SequenceEqual(new byte[]{0xAA,0x39}),"four-byte interface length, checksum and data extraction agree");
                Check(CommandEnvelope.ObservedIdentity(shortInterface).SequenceEqual(CommandEnvelope.ObservedIdentity(longInterface)),"UDP data identity spans both interface widths without comparing function ID");
                Check(CommandEnvelope.Valid(CommandEnvelope.BuildSTC(222,UInt32.MaxValue,new byte[]{1})),"other function IDs support the complete unsigned 32-bit interface range");
                Check(CommandEnvelope.Valid(CommandEnvelope.BuildSTC(0x1553,UInt32.MaxValue,new byte[]{1})),"1553 supports the complete unsigned 32-bit interface range");
                Check(CommandEnvelope.Valid(CommandEnvelope.BuildSTC(65535,65535,new byte[0])),"uniform interface supports 65535 and an empty data area");
                byte[] incorrectOuter=(byte[])shortInterface.Clone();incorrectOuter[4]^=0xFF;incorrectOuter[incorrectOuter.Length-1]^=0x55;
                Check(!CommandEnvelope.Valid(incorrectOuter)&&CommandEnvelope.ObservedIdentity(incorrectOuter).SequenceEqual(CommandEnvelope.ObservedIdentity(shortInterface)),"UDP data identity still ignores outer length/checksum for short interfaces");
                Check(CommandEnvelope.DataOffset(longInterface.Take(29).ToArray())<0,"truncated four-byte layout cannot be mistaken for a short interface");
                byte[] maximumShort=CommandEnvelope.BuildSTC(222,92,new byte[65507-30]);
                Check(maximumShort.Length==65507&&CommandEnvelope.Valid(maximumShort),"uniform interface admits the full IPv4 UDP payload limit");
                bool tooLong=false;try{CommandEnvelope.BuildSTC(0x1553,92,new byte[65507-28]);}catch(ArgumentException){tooLong=true;}
                Check(tooLong,"four-byte interface applies its smaller maximum data size");
                Check(CommandEnvelope.Valid(CommandEnvelope.BuildSTC(0xDD,UInt32.MaxValue,new byte[]{0xAA}))&&CommandEnvelope.DataOffset(CommandEnvelope.BuildSTC(0xDD,92,new byte[]{0xAA}))==26,"0xDD also uses the complete four-byte interface and data offset");
                byte[] aa=CommandEnvelope.BuildSTC(0xAA,0x1234,new byte[]{0,0,0xA1,0x39});
                Check(aa.Length==32&&aa[22]==0x34&&aa[23]==0x12&&CommandEnvelope.DataOffset(aa)==24&&CommandEnvelope.DataContent(aa).SequenceEqual(new byte[]{0,0,0xA1,0x39}),"0xAA uses a two-byte little-endian interface and preserves leading zero data from byte 25");
                bool aaOverflow=false;try{CommandEnvelope.BuildSTC(0xAA,65536,new byte[]{1});}catch(ArgumentException){aaOverflow=true;}
                Check(aaOverflow,"0xAA rejects interface values above 65535 instead of truncating");
                Check(CommandEnvelope.Valid(CommandEnvelope.BuildSTC(0xAA,65535,new byte[65507-28]))&&CommandEnvelope.InterfaceTypeBytes(0xAB)==4,"0xAA admits its larger data area and neighboring function 0xAB remains four bytes");
                var commandPatterns=new CommandBytePatterns(new[]{"ABC","BC","C","ABCD","\0A",""});
                Check(commandPatterns.Find("xABCD\0A").SequenceEqual(new[]{0,1,2,3,4}),"BC byte matcher finds overlapping, suffix and binary-zero patterns");
                Check(commandPatterns.Find("").Length==0&&commandPatterns.Find("ab").Length==0,"empty patterns cannot match all incoming commands and byte matching is case-sensitive");
                Check(new CommandBytePatterns(new[]{"ABCD"}).Find("ABC").Length==0,"partial BC data never counts as a complete command");
                Check(new CommandBytePatterns(new[]{"ABC","ABC"}).Find("xABC").Length==2,"all BC records with identical content are retained as matches");
                Check(new CommandBytePatterns(new[]{"abc","bc","bca","caa"}).Find("xxbcaaabc").SequenceEqual(new[]{0,1,2,3}),"BC matcher follows failure transitions across long and overlapping inputs");
                var utc=new DateTime(2026,1,31,18,22,33,DateTimeKind.Utc);
                Check(BeijingTime.Format(utc)=="2026-02-01 02:22:33.000","Beijing display crosses date boundaries correctly");
                Check(DateTimeOffset.Parse(BeijingTime.Iso(utc)).UtcDateTime==utc&&BeijingTime.Iso(utc).EndsWith("+08:00"),"explicit Beijing offset preserves the original instant");
                Check(BeijingTime.Format(BeijingTime.At(utc))==BeijingTime.Format(utc),"formatting an already-converted Beijing value never adds eight hours twice");
                Console.WriteLine("All " + count + " protocol checks passed."); return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        }
    }
}
