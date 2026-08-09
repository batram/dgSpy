using System;
using System.IO;
using System.Text;
using HookLab.Contracts;

namespace HookLab.Probe.CorDebug.Transport {
	public static class ProbeWireProtocol {
		public const int ProtocolVersion = 1;
		public const int MaximumFrameBytes = 1024 * 1024;
		public const int MaximumStringBytes = 768 * 1024;
		public const int MaximumCorrelationIdBytes = 256;
		public const int MaximumOperationBytes = 256;
		static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

		public static byte[] Encode(ProbeMessage message) {
			if (message == null) throw new ArgumentNullException(nameof(message));
			Validate(message);
			using (var stream = new MemoryStream())
			using (var writer = new BinaryWriter(stream, StrictUtf8, true)) {
				writer.Write(message.ProtocolVersion);
				writer.Write((byte)message.Kind);
				WriteString(writer, message.CorrelationId, MaximumCorrelationIdBytes);
				WriteString(writer, message.Operation, MaximumOperationBytes);
				WriteString(writer, message.PayloadJson, MaximumStringBytes);
				writer.Write(message.ExpectedHooksVersion.HasValue);
				if (message.ExpectedHooksVersion.HasValue) writer.Write(message.ExpectedHooksVersion.Value);
				writer.Flush();
				if (stream.Length > MaximumFrameBytes) throw new InvalidDataException("Probe message exceeds the frame limit.");
				return stream.ToArray();
			}
		}

		public static ProbeMessage Decode(byte[] body) {
			if (body == null) throw new ArgumentNullException(nameof(body));
			if (body.Length == 0 || body.Length > MaximumFrameBytes) throw new InvalidDataException("Probe message frame length is invalid.");
			try {
				using (var stream = new MemoryStream(body, false))
				using (var reader = new BinaryReader(stream, StrictUtf8, true)) {
					var version = reader.ReadInt32();
					var kindValue = reader.ReadByte();
					if (!Enum.IsDefined(typeof(ProbeMessageKind), (int)kindValue)) throw new InvalidDataException("Unknown probe message kind.");
					var correlationId = ReadString(reader, MaximumCorrelationIdBytes);
					var operation = ReadString(reader, MaximumOperationBytes);
					var payloadJson = ReadString(reader, MaximumStringBytes);
					var marker = reader.ReadByte();
					if (marker > 1) throw new InvalidDataException("Invalid expected-hooks-version marker.");
					long? expected = marker == 1 ? reader.ReadInt64() : (long?)null;
					if (stream.Position != stream.Length) throw new InvalidDataException("Probe message contains trailing bytes.");
					var message = new ProbeMessage(version, (ProbeMessageKind)kindValue, correlationId, operation, payloadJson, expected);
					Validate(message);
					return message;
				}
			}
			catch (InvalidDataException) { throw; }
			catch (Exception ex) when (ex is EndOfStreamException || ex is DecoderFallbackException || ex is ArgumentException) {
				throw new InvalidDataException("Malformed probe message.", ex);
			}
		}

		public static void WriteFrame(Stream stream, byte[] body) {
			if (stream == null) throw new ArgumentNullException(nameof(stream));
			if (body == null || body.Length == 0 || body.Length > MaximumFrameBytes) throw new InvalidDataException("Probe message frame length is invalid.");
			var length = BitConverter.GetBytes(body.Length);
			stream.Write(length, 0, length.Length);
			stream.Write(body, 0, body.Length);
			stream.Flush();
		}

		public static byte[] ReadFrame(Stream stream) {
			if (stream == null) throw new ArgumentNullException(nameof(stream));
			var prefix = ReadExactly(stream, sizeof(int));
			var length = BitConverter.ToInt32(prefix, 0);
			if (length <= 0 || length > MaximumFrameBytes) throw new InvalidDataException("Probe message frame length is invalid.");
			return ReadExactly(stream, length);
		}

		static void Validate(ProbeMessage message) {
			if (message.ProtocolVersion != ProtocolVersion) throw new InvalidDataException("Unsupported probe protocol version.");
			if (!Enum.IsDefined(typeof(ProbeMessageKind), message.Kind)) throw new InvalidDataException("Unknown probe message kind.");
			ValidateString(message.CorrelationId, MaximumCorrelationIdBytes, "correlation ID");
			ValidateString(message.Operation, MaximumOperationBytes, "operation");
			ValidateString(message.PayloadJson, MaximumStringBytes, "payload");
		}

		static void ValidateString(string value, int maximumBytes, string field) {
			if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("Probe message " + field + " is required.");
			if (StrictUtf8.GetByteCount(value) > maximumBytes) throw new InvalidDataException("Probe message " + field + " exceeds its limit.");
		}

		static void WriteString(BinaryWriter writer, string value, int maximumBytes) {
			var bytes = StrictUtf8.GetBytes(value);
			if (bytes.Length > maximumBytes) throw new InvalidDataException("Probe message string exceeds its limit.");
			writer.Write(bytes.Length); writer.Write(bytes);
		}

		static string ReadString(BinaryReader reader, int maximumBytes) {
			var length = reader.ReadInt32();
			if (length < 0 || length > maximumBytes) throw new InvalidDataException("Probe message string length is invalid.");
			var bytes = reader.ReadBytes(length);
			if (bytes.Length != length) throw new EndOfStreamException();
			return StrictUtf8.GetString(bytes);
		}

		static byte[] ReadExactly(Stream stream, int count) {
			var result = new byte[count]; var offset = 0;
			while (offset < count) { var read = stream.Read(result, offset, count - offset); if (read == 0) throw new EndOfStreamException(); offset += read; }
			return result;
		}
	}
}
