// Minimal System.Text.Json surface used by this host, backed by Json.NET on
// Windows 10 Mobile. System.Text.Json's UAP build starts after build 15063.
using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace System.Text.Json
{
    public enum JsonValueKind { Undefined, Object, Array, String, Number, True, False, Null }

    [Newtonsoft.Json.JsonConverter(typeof(ArmJsonElementConverter))]
    public struct JsonElement
    {
        internal readonly JToken Token;
        internal JsonElement(JToken token) { Token = token; }

        public JsonValueKind ValueKind
        {
            get
            {
                if (Token == null || Token.Type == JTokenType.Undefined) return JsonValueKind.Undefined;
                switch (Token.Type)
                {
                    case JTokenType.Object: return JsonValueKind.Object;
                    case JTokenType.Array: return JsonValueKind.Array;
                    case JTokenType.String: return JsonValueKind.String;
                    case JTokenType.Integer:
                    case JTokenType.Float: return JsonValueKind.Number;
                    case JTokenType.Boolean: return Token.Value<bool>() ? JsonValueKind.True : JsonValueKind.False;
                    case JTokenType.Null: return JsonValueKind.Null;
                    default: return JsonValueKind.Undefined;
                }
            }
        }

        public bool TryGetProperty(string name, out JsonElement value)
        {
            var property = (Token as JObject)?.Property(name, StringComparison.Ordinal);
            value = property == null ? default(JsonElement) : new JsonElement(property.Value);
            return property != null;
        }

        public IEnumerable<JsonElement> EnumerateArray()
        {
            if (!(Token is JArray array)) throw new InvalidOperationException("JSON value is not an array.");
            foreach (var item in array) yield return new JsonElement(item);
        }

        public string GetString()
        {
            if (ValueKind == JsonValueKind.Null) return null;
            if (ValueKind != JsonValueKind.String) throw new InvalidOperationException("JSON value is not a string.");
            return Token.Value<string>();
        }

        public bool GetBoolean()
        {
            if (ValueKind != JsonValueKind.True && ValueKind != JsonValueKind.False)
                throw new InvalidOperationException("JSON value is not a boolean.");
            return Token.Value<bool>();
        }

        public double GetDouble()
        {
            if (!TryGetDouble(out var value)) throw new InvalidOperationException("JSON value is not a number.");
            return value;
        }

        public bool TryGetInt32(out int value) =>
            int.TryParse(Token?.ToString(Formatting.None), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out value);
        public bool TryGetInt64(out long value) =>
            long.TryParse(Token?.ToString(Formatting.None), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out value);
        public bool TryGetDouble(out double value) =>
            double.TryParse(Token?.ToString(Formatting.None), NumberStyles.Float,
                CultureInfo.InvariantCulture, out value);

        public string GetRawText() => Token?.ToString(Formatting.None) ?? "null";
        public JsonElement Clone() => new JsonElement(Token?.DeepClone());
        public override string ToString() =>
            ValueKind == JsonValueKind.String ? GetString() :
            ValueKind == JsonValueKind.Null || ValueKind == JsonValueKind.Undefined ? "" :
            GetRawText();
    }

    public sealed class JsonDocument : IDisposable
    {
        public JsonElement RootElement { get; private set; }
        private JsonDocument(JToken token) { RootElement = new JsonElement(token); }
        public static JsonDocument Parse(string json) => new JsonDocument(JToken.Parse(json));
        public void Dispose() { }
    }

    public static class JsonSerializer
    {
        public static string Serialize(object value) => JsonConvert.SerializeObject(value);

        public static T Deserialize<T>(string json)
        {
            if (typeof(T) == typeof(Dictionary<string, object>) ||
                typeof(T) == typeof(Dictionary<string, JsonElement>))
            {
                var objectToken = JObject.Parse(json);
                if (typeof(T) == typeof(Dictionary<string, object>))
                {
                    var result = new Dictionary<string, object>();
                    foreach (var property in objectToken.Properties())
                        result[property.Name] = new JsonElement(property.Value);
                    return (T)(object)result;
                }
                var elements = new Dictionary<string, JsonElement>();
                foreach (var property in objectToken.Properties())
                    elements[property.Name] = new JsonElement(property.Value);
                return (T)(object)elements;
            }
            return JsonConvert.DeserializeObject<T>(json);
        }
    }

    public sealed class ArmJsonElementConverter : Newtonsoft.Json.JsonConverter
    {
        public override bool CanConvert(Type type) => type == typeof(JsonElement);
        public override void WriteJson(JsonWriter writer, object value,
            Newtonsoft.Json.JsonSerializer serializer)
        {
            var token = ((JsonElement)value).Token;
            if (token == null) writer.WriteNull();
            else token.WriteTo(writer);
        }
        public override object ReadJson(JsonReader reader, Type type, object existingValue,
            Newtonsoft.Json.JsonSerializer serializer) => new JsonElement(JToken.ReadFrom(reader));
    }
}
