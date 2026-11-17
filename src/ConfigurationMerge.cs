using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace TelemetryMonitor
{
    internal static class ConfigurationMerge
    {
        private static bool Same(object a,object b){return CanonicalJson.Encode(a)==CanonicalJson.Encode(b);}
        private static string KeyName(string path)
        {
            string field=path.Substring(path.LastIndexOf('.')+1);
            if(field=="Commands")return "Number";if(field=="Categories")return "Kind";if(field=="Rows")return "Row";
            if(new[]{"Rules","Sequences","Parameters","Packets"}.Contains(field))return "Id";
            if(field=="Bindings")return "Number";if(field=="Routes")return "DestinationIP";return "";
        }
        internal static object Merge(object original,object current,object pending,string path)
        {
            if(Same(original,pending))return current;if(Same(original,current)||Same(current,pending))return pending;
            var b=original as Dictionary<string,object>;var c=current as Dictionary<string,object>;var p=pending as Dictionary<string,object>;
            if(c!=null&&p!=null&&(b!=null||original==null))
            {
                b=b??new Dictionary<string,object>();var result=new Dictionary<string,object>();
                foreach(string key in b.Keys.Concat(c.Keys).Concat(p.Keys).Distinct())
                {
                    object bv,cv,pv;b.TryGetValue(key,out bv);c.TryGetValue(key,out cv);p.TryGetValue(key,out pv);
                    if(key=="Revision"||key=="GeneratedAt"||key=="ModifiedAt")result[key]=pv??cv;
                    else if((key=="NextNumber"||key.StartsWith("Next",StringComparison.Ordinal))&&cv is IConvertible&&pv is IConvertible&&!(cv is string)&&!(pv is string))result[key]=Math.Max(Convert.ToDecimal(cv),Convert.ToDecimal(pv));
                    else
                    {
                        object value=Merge(bv,cv,pv,path+"."+key);
                        if(value!=null||c.ContainsKey(key)||p.ContainsKey(key))result[key]=value;
                    }
                }
                return result;
            }
            var ba=original as object[];var ca=current as object[];var pa=pending as object[];
            if(ca!=null&&pa!=null&&(ba!=null||original==null))
            {
                ba=ba??new object[0];string key=KeyName(path);
                Func<object,string> identity=value=>
                {
                    var row=value as Dictionary<string,object>;object id;
                    if(row!=null){if(key.Length==0||!row.TryGetValue(key,out id)||id==null)throw new InvalidOperationException("并发配置无法自动合并："+path);return Convert.ToString(id,System.Globalization.CultureInfo.InvariantCulture);}
                    return CanonicalJson.Encode(value);
                };
                var baseline=ba.ToDictionary(identity);var live=ca.ToDictionary(identity);var edits=pa.ToDictionary(identity);var output=new List<object>();
                foreach(string id in live.Keys.Concat(edits.Keys).Concat(baseline.Keys).Distinct())
                {
                    object bv,cv,pv;baseline.TryGetValue(id,out bv);live.TryGetValue(id,out cv);edits.TryGetValue(id,out pv);
                    if(!edits.ContainsKey(id)&&baseline.ContainsKey(id)){if(!Same(bv,cv)&&cv!=null)throw new InvalidOperationException("待删除项接收到了新修改："+path+" / "+id);continue;}
                    if(!live.ContainsKey(id)&&baseline.ContainsKey(id)){if(!Same(bv,pv)&&pv!=null)throw new InvalidOperationException("配置项已被删除："+path+" / "+id);continue;}
                    output.Add(Merge(bv,cv,pv,path+"["+id+"]"));
                }
                return output.ToArray();
            }
            throw new InvalidOperationException("当前数据与待生效修改发生冲突："+path+"；已保留两者，请调整或取消待生效修改后重试");
        }
    }
}
