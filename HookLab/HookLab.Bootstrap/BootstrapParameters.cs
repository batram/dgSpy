using System;
using System.Collections.Generic;
using System.Globalization;

namespace HookLab.Bootstrap {
	/// <summary>Bounded initialization data, parsed with BCL string handling only.
	///
	/// The format is deliberately not JSON. A single string argument is what a debugger func-eval can
	/// supply in one evaluation, and a line-oriented key/value form needs no parser that could itself
	/// become an attack surface inside the target. Every bound is explicit and every unknown key is an
	/// error, so a typo fails loudly instead of silently disabling a guard.</summary>
	sealed class BootstrapParameters {
		internal const int MaximumBytes = 8192;
		internal const int MaximumLines = 64;
		internal const int MaximumKeyLength = 64;
		internal const int MaximumValueLength = 1024;

		static readonly string[] KnownKeys = {
			"host_id", "image_path", "process_id", "process_creation_utc_ticks", "architecture", "runtime_id",
			"appdomain_id", "event_capacity", "byte_capacity", "endpoint", "completion_path",
			"hook_id", "hook_kind", "hook_assembly", "hook_type", "hook_method", "hook_module_mvid",
			"hook_metadata_token", "hook_declaring_type", "hook_method_signature", "hook_il_sha256",
			"maximum_events_per_second", "maximum_string_length",
		};

		static readonly string[] HookKeys = {
			"hook_id", "hook_kind", "hook_assembly", "hook_type", "hook_method", "hook_module_mvid",
			"hook_metadata_token", "hook_declaring_type", "hook_method_signature", "hook_il_sha256",
		};

		BootstrapParameters(Dictionary<string, string> values) { Values = values; }

		internal Dictionary<string, string> Values { get; }

		internal string HostId => Values["host_id"];
		internal string ImagePath => Values["image_path"];
		internal int ProcessId => int.Parse(Values["process_id"], CultureInfo.InvariantCulture);
		internal long ProcessCreationUtcTicks => long.Parse(Values["process_creation_utc_ticks"], CultureInfo.InvariantCulture);
		internal string Architecture => Values["architecture"];
		internal string RuntimeId => Values["runtime_id"];
		internal string AppDomainId => Values["appdomain_id"];
		internal string Endpoint => Values["endpoint"];
		internal string? CompletionPath => Values.TryGetValue("completion_path", out var value) ? value : null;
		internal int EventCapacity => Optional("event_capacity", 1024);
		internal long ByteCapacity => Values.TryGetValue("byte_capacity", out var value) ? long.Parse(value, CultureInfo.InvariantCulture) : 4L * 1024 * 1024;
		internal bool HasHook => Values.ContainsKey("hook_id");

		internal string Hook(string key) => Values[key];
		internal string? OptionalHook(string key) => Values.TryGetValue(key, out var value) ? value : null;

		int Optional(string key, int fallback) => Values.TryGetValue(key, out var value) ? int.Parse(value, CultureInfo.InvariantCulture) : fallback;

		internal static BootstrapParameters Parse(string text) {
			if (text == null) throw new ArgumentNullException(nameof(text));
			if (text.Length > MaximumBytes) throw new ArgumentException("Initialization data exceeds " + MaximumBytes + " characters.", nameof(text));
			var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
			if (lines.Length > MaximumLines) throw new ArgumentException("Initialization data exceeds " + MaximumLines + " lines.", nameof(text));
			var values = new Dictionary<string, string>(StringComparer.Ordinal);
			foreach (var raw in lines) {
				var line = raw.Trim();
				if (line.Length == 0 || line[0] == '#') continue;
				var separator = line.IndexOf('=');
				if (separator <= 0) throw new ArgumentException("Malformed initialization line: " + line, nameof(text));
				var key = line.Substring(0, separator).Trim();
				var value = line.Substring(separator + 1).Trim();
				if (key.Length > MaximumKeyLength) throw new ArgumentException("Initialization key is too long: " + key, nameof(text));
				if (value.Length > MaximumValueLength) throw new ArgumentException("Initialization value is too long for key: " + key, nameof(text));
				if (Array.IndexOf(KnownKeys, key) < 0) throw new ArgumentException("Unknown initialization key: " + key, nameof(text));
				if (values.ContainsKey(key)) throw new ArgumentException("Duplicate initialization key: " + key, nameof(text));
				if (value.Length == 0) throw new ArgumentException("Empty initialization value for key: " + key, nameof(text));
				values.Add(key, value);
			}
			foreach (var required in new[] { "host_id", "image_path", "process_id", "process_creation_utc_ticks", "architecture", "runtime_id", "appdomain_id", "endpoint" })
				if (!values.ContainsKey(required)) throw new ArgumentException("Missing required initialization key: " + required, nameof(text));
			if (values["endpoint"] != "none" && values["endpoint"] != "pipe")
				throw new ArgumentException("endpoint must be exactly 'none' or 'pipe'.", nameof(text));
			if (values["endpoint"] == "none" && !values.ContainsKey("completion_path"))
				throw new ArgumentException("Missing required initialization key for endpoint=none: completion_path", nameof(text));
			// All or nothing: a half-specified hook would otherwise silently install with a guard field
			// defaulted, which is the one thing a guard must never do.
			var present = 0;
			foreach (var key in HookKeys) if (values.ContainsKey(key)) present++;
			if (present != 0 && present != HookKeys.Length)
				throw new ArgumentException("A hook specification must supply all of: " + string.Join(", ", HookKeys), nameof(text));
			return new BootstrapParameters(values);
		}
	}
}
