using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using HookLab.Contracts;

namespace HookLab.Probe.CorDebug.Patching {
	public sealed class CaptureResult {
		internal CaptureResult(string json, bool truncated) { Json = json; Truncated = truncated; }
		public string Json { get; }
		public bool Truncated { get; }
	}

	public static class BoundedCapture {
		public static CaptureResult Serialize(object? value, HookLimits limits) {
			if (limits == null) throw new ArgumentNullException(nameof(limits));
			var writer = new Writer(limits); writer.Value(value, 0);
			var json = writer.Text;
			if (Encoding.UTF8.GetByteCount(json) > limits.MaximumEventBytes) {
				const string marker = "{\"truncated\":true}";
				json = Encoding.UTF8.GetByteCount(marker) <= limits.MaximumEventBytes ? marker : "0";
				writer.Truncated = true;
			}
			return new CaptureResult(json, writer.Truncated);
		}

		sealed class Writer {
			readonly HookLimits limits; readonly StringBuilder text = new StringBuilder();
			internal Writer(HookLimits limits) { this.limits = limits; }
			internal bool Truncated { get; set; }
			internal string Text => text.ToString();
			internal void Value(object? value, int depth) {
				if (value == null) { text.Append("null"); return; }
				if (depth >= limits.MaximumSerializationDepth) { Marker("depth"); return; }
				if (value is string str) { String(str); return; }
				if (value is bool flag) { text.Append(flag ? "true" : "false"); return; }
				if (value is char character) { String(character.ToString()); return; }
				if (value.GetType().IsPrimitive || value is decimal) { text.Append(Convert.ToString(value, CultureInfo.InvariantCulture)); return; }
				if (value is IDictionary dictionary) { Dictionary(dictionary, depth); return; }
				if (value is IEnumerable enumerable) { Enumerable(enumerable, depth); return; }
				Object(value, depth);
			}
			void Object(object value, int depth) {
				text.Append('{'); String("$type"); text.Append(':'); String(value.GetType().FullName ?? value.GetType().Name);
				var count = 0;
				foreach (var field in value.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public)) {
					if (count++ >= limits.MaximumCollectionCount) { text.Append(','); MarkerProperty("collection"); break; }
					text.Append(','); String(field.Name); text.Append(':');
					try { Value(field.GetValue(value), depth + 1); } catch { Marker("field"); }
				}
				text.Append('}');
			}
			void Dictionary(IDictionary values, int depth) {
				text.Append('['); var count = 0;
				foreach (DictionaryEntry entry in values) {
					if (count != 0) text.Append(','); if (count++ >= limits.MaximumCollectionCount) { Marker("collection"); break; }
					text.Append('{'); String("key"); text.Append(':'); Value(entry.Key, depth + 1); text.Append(','); String("value"); text.Append(':'); Value(entry.Value, depth + 1); text.Append('}');
				}
				text.Append(']');
			}
			void Enumerable(IEnumerable values, int depth) {
				text.Append('['); var count = 0;
				foreach (var value in values) { if (count != 0) text.Append(','); if (count++ >= limits.MaximumCollectionCount) { Marker("collection"); break; } Value(value, depth + 1); }
				text.Append(']');
			}
			void String(string value) { if (value.Length > limits.MaximumStringLength) { value = value.Substring(0, limits.MaximumStringLength); Truncated = true; } text.Append('"'); foreach (var c in value) { if (c == '"' || c == '\\') { text.Append('\\').Append(c); } else if (c == '\n') text.Append("\\n"); else if (c == '\r') text.Append("\\r"); else text.Append(c); } text.Append('"'); }
			void Marker(string reason) { Truncated = true; text.Append("{\"truncated\":"); String(reason); text.Append('}'); }
			void MarkerProperty(string reason) { Truncated = true; String("$truncated"); text.Append(':'); String(reason); }
		}
	}
}
