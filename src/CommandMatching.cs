using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace TelemetryMonitor
{
    // Byte-preserving strings keep containment aligned to bytes, never HEX nibbles.
    internal sealed class CommandBytePatterns
    {
        private sealed class Node
        {internal readonly Dictionary<char,int> Next=new Dictionary<char,int>();internal int Fail;internal List<int> Output;}
        private readonly List<Node> nodes=new List<Node>{new Node()};
        internal CommandBytePatterns(IList<string> patterns)
        {
            for(int i=0;i<patterns.Count;i++)
            {
                if(patterns[i].Length==0)continue;int state=0;
                foreach(char value in patterns[i]){int next;if(!nodes[state].Next.TryGetValue(value,out next)){next=nodes.Count;nodes[state].Next[value]=next;nodes.Add(new Node());}state=next;}
                if(nodes[state].Output==null)nodes[state].Output=new List<int>();nodes[state].Output.Add(i);
            }
            var queue=new Queue<int>();foreach(int child in nodes[0].Next.Values)queue.Enqueue(child);
            while(queue.Count>0)
            {
                int parent=queue.Dequeue();foreach(var edge in nodes[parent].Next)
                {
                    int fail=nodes[parent].Fail,next;while(fail!=0&&!nodes[fail].Next.ContainsKey(edge.Key))fail=nodes[fail].Fail;
                    if(nodes[fail].Next.TryGetValue(edge.Key,out next))nodes[edge.Value].Fail=next;
                    var inherited=nodes[nodes[edge.Value].Fail].Output;
                    if(inherited!=null){if(nodes[edge.Value].Output==null)nodes[edge.Value].Output=new List<int>();nodes[edge.Value].Output.AddRange(inherited);}
                    queue.Enqueue(edge.Value);
                }
            }
        }
        internal int[] Find(string data)
        {
            if(nodes.Count==1||data.Length==0)return new int[0];
            var found=new HashSet<int>();int state=0;
            foreach(char value in data)
            {
                int next;while(state!=0&&!nodes[state].Next.ContainsKey(value))state=nodes[state].Fail;
                state=nodes[state].Next.TryGetValue(value,out next)?next:0;
                if(nodes[state].Output!=null)foreach(int index in nodes[state].Output)found.Add(index);
            }
            return found.OrderBy(i=>i).ToArray();
        }
    }
    internal sealed class CommandMatch
    {internal bool BCPriority;internal ObservedCommand[] Entries=new ObservedCommand[0];}

    public sealed partial class ObservedCommands
    {
        private static readonly Encoding MatchBytes=Encoding.GetEncoding(28591);
        private long matchingRevision=-1;
        private readonly Dictionary<string,List<ObservedCommand>> structuredMatches=new Dictionary<string,List<ObservedCommand>>(StringComparer.Ordinal);
        private readonly Dictionary<string,CommandMatch> matchingCache=new Dictionary<string,CommandMatch>(StringComparer.Ordinal);
        private int matchingCacheCharacters;
        private ObservedCommand[] bcPatternEntries=new ObservedCommand[0];
        private CommandBytePatterns bcPatterns;
        private static string MatchSignature(byte[] payload,int width,out string data)
        {
            payload=payload??new byte[0];int offset=CommandEnvelope.DataOffset(payload,width);
            if(offset<0){data=null;return "\0"+MatchBytes.GetString(payload);}
            data=MatchBytes.GetString(payload,offset,payload.Length-offset-4);
            return ((char)(offset-22)).ToString()+MatchBytes.GetString(payload,20,offset-20)+data;
        }
        private void EnsureMatchingIndex()
        {
            if(matchingRevision==revision)return;
            structuredMatches.Clear();matchingCache.Clear();matchingCacheCharacters=0;
            var patterns=new List<string>();var entries=new List<ObservedCommand>();
            foreach(var entry in configuration.Commands.OrderBy(e=>e.Number))
            {
                var payload=entry.Length==0?new byte[0]:PacketCodec.Hex(entry.PayloadHex);string data;
                string signature=MatchSignature(payload,entry.InterfaceBytes,out data);List<ObservedCommand> bucket;
                if(!structuredMatches.TryGetValue(signature,out bucket))structuredMatches[signature]=bucket=new List<ObservedCommand>();bucket.Add(entry);
                if(data!=null&&data.Length>0&&entry.Categories.Any(c=>c.Kind=="BC")){patterns.Add(data);entries.Add(entry);}
            }
            bcPatternEntries=entries.ToArray();bcPatterns=new CommandBytePatterns(patterns);matchingRevision=revision;
        }
        private ObservedCommand[] MatchingBCContent(byte[] data)
        {EnsureMatchingIndex();return bcPatterns.Find(MatchBytes.GetString(data)).Select(i=>bcPatternEntries[i]).ToArray();}
        internal CommandMatch MatchCommand(byte[] payload)
        {
            lock(gate)
            {
                if(!ready)throw new InvalidOperationException(error);EnsureMatchingIndex();string data,signature=MatchSignature(payload,0,out data);CommandMatch result;
                if(matchingCache.TryGetValue(signature,out result))return result;
                List<ObservedCommand> bucket;structuredMatches.TryGetValue(signature,out bucket);
                var bc=data==null?(bucket??new List<ObservedCommand>()).Where(e=>e.Categories.Any(c=>c.Kind=="BC")).ToArray():bcPatterns.Find(data).Select(i=>bcPatternEntries[i]).ToArray();
                result=bc.Length>0?new CommandMatch{BCPriority=true,Entries=bc}:new CommandMatch{Entries=bucket==null?new ObservedCommand[0]:bucket.ToArray()};
                if(matchingCache.Count>=512||matchingCacheCharacters+signature.Length>1024*1024){matchingCache.Clear();matchingCacheCharacters=0;}
                matchingCache[signature]=result;matchingCacheCharacters+=signature.Length;return result;
            }
        }
        internal static HashSet<string> MatchingCodes(CommandMatch match,string destination,IEnumerable<TCBinding> bindings)
        {
            var codes=new HashSet<string>();
            foreach(var entry in match.Entries)
            {
                foreach(var category in entry.Categories.Where(c=>match.BCPriority?c.Kind=="BC":CategoryMatches(entry,c,destination)))codes.Add(category.Code);
                foreach(var binding in bindings.Where(b=>entry.MatchesNumber(b.Number)))
                {
                    var category=entry.Categories.FirstOrDefault(c=>c.Code==binding.Code);
                    if(category==null||(match.BCPriority?category.Kind=="BC":CategoryMatches(entry,category,destination)))codes.Add(binding.Code);
                }
            }
            return codes;
        }
    }
}
