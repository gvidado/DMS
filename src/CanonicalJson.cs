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
        {
            if (ReferenceEquals(left, right)) return true;
            var serializer = Serializer();
            string a = serializer.Serialize(left), b = serializer.Serialize(right);
            return String.Equals(a, b, StringComparison.Ordinal)
                || EqualParsed(serializer.DeserializeObject(a), serializer.DeserializeObject(b));
        }
        // Compare deserialized JSON trees without repeatedly sorting and serializing subtrees.
        internal static bool EqualParsed(object left, object right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null) return false;
            var map = left as IDictionary<string, object>;
            if (map != null)
            {
                var other = right as IDictionary<string, object>;
                if (other == null || other.Count != map.Count) return false;
                foreach (var item in map)
                {
                    object value;
                    if (!other.TryGetValue(item.Key, out value) || !EqualParsed(item.Value, value)) return false;
                }
                return true;
            }
            var rows = left as object[];
            if (rows != null)
            {
                var other = right as object[];
                if (other == null || other.Length != rows.Length) return false;
                for (int i = 0; i < rows.Length; i++)
                    if (!EqualParsed(rows[i], other[i])) return false;
                return true;
            }
            if (left is string || left is bool || right is string || right is bool)
                return left.Equals(right);
            if (left.Equals(right)) return true;
            // Preserve the existing canonical number rules across CLR numeric types.
            if (right is IDictionary<string, object> || right is object[]) return false;
            var serializer = Serializer();
            return serializer.Serialize(Normalize(left)) == serializer.Serialize(Normalize(right));
        }
        internal static string Hash(object value)
        {using(var digest=SHA256.Create())return Convert.ToBase64String(digest.ComputeHash(Encoding.UTF8.GetBytes(Encode(value))));}
    }
}
