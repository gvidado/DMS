using System;
using System.Collections.Generic;
using System.Linq;

namespace TelemetryMonitor
{
    internal sealed class DisplaySelectionCache
    {internal Account Account;internal List<string> Selected,Resolved;internal ParameterDisplayConfiguration Layout;}
    public sealed partial class Auth
    {
        private List<Parameter> selectionCatalog;
        private HashSet<string> selectionValid;
        private Dictionary<string,string> selectionLegacy;
        private readonly Dictionary<string,DisplaySelectionCache> selectionCache=new Dictionary<string,DisplaySelectionCache>();
        private List<string> CachedSelection(Account account,List<Parameter> catalog)
        {
            if(!Object.ReferenceEquals(selectionCatalog,catalog))
            {
                selectionCatalog=catalog;selectionValid=new HashSet<string>(catalog.Select(p=>p.Id));selectionLegacy=new Dictionary<string,string>();selectionCache.Clear();
                foreach(var field in catalog)if(field.PacketEnabled&&field.SourceId!=null&&!selectionLegacy.ContainsKey(field.SourceId))selectionLegacy[field.SourceId]=field.Id;
            }
            DisplaySelectionCache cached;
            if(selectionCache.TryGetValue(account.Username,out cached)&&Object.ReferenceEquals(cached.Account,account)&&Object.ReferenceEquals(cached.Selected,account.Selected)&&Object.ReferenceEquals(cached.Layout,account.DisplayLayout))return new List<string>(cached.Resolved);
            var result=new List<string>();var seen=new HashSet<string>();
            var selected=account.DisplayLayout==null?account.Selected:account.DisplayLayout.Screens.SelectMany(s=>s.Boxes).SelectMany(b=>b.Parameters);
            foreach(string id in selected)
            {
                string resolved=id;
                if(!selectionValid.Contains(id))
                {
                    if(account.DisplayLayout!=null)continue;
                    string source=id.Contains("::")?id.Substring(id.IndexOf("::",StringComparison.Ordinal)+2):id;
                    if(!selectionLegacy.TryGetValue(source,out resolved))continue;
                }
                if(seen.Add(resolved))result.Add(resolved);
            }
            selectionCache[account.Username]=new DisplaySelectionCache{Account=account,Selected=account.Selected,Layout=account.DisplayLayout,Resolved=result};return new List<string>(result);
        }
    }
}
