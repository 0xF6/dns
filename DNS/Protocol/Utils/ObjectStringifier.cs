namespace DNS.Protocol.Utils
{
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using System.Text;

    public class ObjectStringifier
    {
        public static ObjectStringifier New(object obj) => new ObjectStringifier(obj);
        public static string Stringify(object obj) => StringifyObject(obj);
        private static string StringifyObject(object obj)
        {
            switch (obj)
            {
                case string s:
                    return s;
                case IDictionary dictionary:
                    return StringifyDictionary(dictionary);
                case IEnumerable enumerable:
                    return StringifyList(enumerable);
                default:
                    return obj == null ? "null" : obj.ToString();
            }
        }
        private static string StringifyList(IEnumerable enumerable) 
            => "[" + string.Join(", ", enumerable.Cast<object>().Select(StringifyObject).ToArray()) + "]";
        private static string StringifyDictionary(IEnumerable dict)
        {
            var result = new StringBuilder();

            result.Append("{");

            foreach (DictionaryEntry pair in dict)
            {
                result
                    .Append(pair.Key)
                    .Append("=")
                    .Append(StringifyObject(pair.Value))
                    .Append(", ");
            }

            if (result.Length > 1)
            {
                result.Remove(result.Length - 2, 2);
            }

            return result.Append("}").ToString();
        }

        private readonly object obj;
        private readonly Dictionary<string, string> pairs;

        public ObjectStringifier(object obj)
        {
            this.obj = obj;
            this.pairs = new Dictionary<string, string>();
        }

        public ObjectStringifier Remove(params string[] names)
        {
            foreach (var name in names) pairs.Remove(name);
            return this;
        }

        public ObjectStringifier Add(params string[] names)
        {
            var type = obj.GetType();

            foreach (var name in names)
            {
                var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (property is null) continue;
                var value = property.GetValue(obj, new object[] { });

                pairs.Add(name, StringifyObject(value));
            }

            return this;
        }

        public ObjectStringifier Add(string name, object value)
        {
            pairs.Add(name, StringifyObject(value));
            return this;
        }

        public ObjectStringifier AddAll()
        {
            var properties = obj.GetType().GetProperties(
                BindingFlags.Public | BindingFlags.Instance);

            foreach (var property in properties)
            {
                var value = property.GetValue(obj, new object[] { });
                pairs.Add(property.Name, StringifyObject(value));
            }

            return this;
        }

        public override string ToString() => StringifyDictionary(pairs);
    }
}
