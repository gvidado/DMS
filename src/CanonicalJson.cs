using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace TelemetryMonitor
{
    // Only semantic comparisons use this representation; original files remain untouched.
    internal static class CanonicalJson
    {
        private static JavaScriptSerializer Serializer()
        {return new JavaScriptSerializer{MaxJsonLength=Int32.MaxValue,RecursionLimit=128};}
        private static object Normalize(object value)
        {
            var map=value as IDictionary<string,object>;
            if(map!=null)
            {
                var sorted=new SortedDictionary<string,object>(StringComparer.Ordinal);
                foreach(var pair in map)sorted.Add(pair.Key,Normalize(pair.Value));
                return sorted;
            }
            var items=value as IEnumerable;
            if(items!=null && !(value is string))return items.Cast<object>().Select(Normalize).ToArray();
            if(value is decimal)return Decimal.Parse(((decimal)value).ToString("G29",CultureInfo.InvariantCulture),NumberStyles.Float,CultureInfo.InvariantCulture);
            return value;
        }
        internal static string Encode(object value)
        {
            var serializer=Serializer();
            return serializer.Serialize(Normalize(serializer.DeserializeObject(serializer.Serialize(value))));
        }
        internal static bool Equivalent(object left,object right)
        {return String.Equals(Encode(left),Encode(right),StringComparison.Ordinal);}
        internal static string Hash(object value)
        {using(var digest=SHA256.Create())return Convert.ToBase64String(digest.ComputeHash(Encoding.UTF8.GetBytes(Encode(value))));}
    }
}
