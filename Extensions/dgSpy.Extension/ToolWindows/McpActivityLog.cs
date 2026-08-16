using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using dgSpy.Protocol;

namespace dgSpy.Extension.ToolWindows {
	/// <summary>One dispatched MCP operation: what the agent asked for, and what it got back.</summary>
	sealed class McpActivityEntry {
		public long Sequence { get; set; }
		public DateTime TimestampUtc { get; set; }
		public string Operation { get; set; }="";
		public string RequestId { get; set; }="";
		/// <summary>Error code on failure, "ok" otherwise. This is the wire code, not a display string.</summary>
		public string Status { get; set; }="ok";
		public bool Failed { get; set; }
		public string? ErrorMessage { get; set; }
		public string ErrorDetails { get; set; }="";
		public double DurationMs { get; set; }
		public string Arguments { get; set; }="{}";
		public string Result { get; set; }="";
	}

	/// <summary>
	/// Bounded, in-memory history of every RPC operation the extension dispatched, feeding the
	/// "dgSpy MCP Activity" tool window.
	///
	/// This is a static singleton rather than a MEF export on purpose. <see cref="RpcHost"/> is
	/// constructed directly by <see cref="ExtensionEntryPoint"/>, so wiring the log through MEF would
	/// mean adding an import to the entry point -- and a MEF import that cannot be satisfied deletes
	/// the importing part silently (see AGENTS.md). A visibility feature must not be able to take the
	/// RPC host down with it.
	/// </summary>
	sealed class McpActivityLog {
		public static McpActivityLog Instance { get; }=new McpActivityLog();

		/// <summary>Kept small enough that a long agent session cannot grow the dnSpy process without bound.</summary>
		public const int DefaultCapacity=1000;
		/// <summary>Per-payload serialization budget, in UTF-8 bytes. get_raw_module and get_disassembly
		/// can return megabytes; serializing them in full for a grid cell is not worth it, so the writer
		/// is cut off at this point and the entry is marked truncated.</summary>
		const int PayloadLimit=16*1024;

		static readonly JsonSerializerOptions prettyOptions=new JsonSerializerOptions(ProtocolJson.Options) { WriteIndented=true };

		readonly object sync=new object();
		readonly Queue<McpActivityEntry> entries;
		readonly int capacity;
		long lastSequence;

		public McpActivityLog(int capacity=DefaultCapacity) {
			this.capacity=capacity;
			entries=new Queue<McpActivityEntry>(capacity);
		}

		public int Capacity => capacity;

		/// <summary>Raised on the RPC thread that completed the operation, never on the UI thread.</summary>
		public event Action<McpActivityEntry>? Recorded;
		public event Action? Cleared;

		public McpActivityEntry[] Snapshot() { lock(sync) return entries.ToArray(); }

		public void Clear() { lock(sync) entries.Clear(); Cleared?.Invoke(); }

		/// <summary>
		/// Transport chatter, not agent actions. The Gateway heartbeats every two seconds and probes
		/// with ping on connect, so recording these would push every real call out of the buffer
		/// within half an hour and leave the window useless.
		/// </summary>
		public static bool IsRecordable(string operation) =>
			operation!="gateway_heartbeat" && operation!="ping";

		public void Record(RpcRequest request,RpcResponse response,TimeSpan elapsed) {
			if(!IsRecordable(request.Operation)) return;
			// Serialize outside the lock: a 16 KB payload is not worth blocking another RPC thread for.
			var entry=new McpActivityEntry {
				TimestampUtc=DateTime.UtcNow,
				Operation=request.Operation,
				RequestId=request.RequestId,
				// The shared secret travels in RpcRequest.AuthenticationToken, never in Arguments,
				// so recording the arguments verbatim cannot leak it.
				Arguments=Describe(request.Arguments),
				DurationMs=elapsed.TotalMilliseconds,
				Failed=response.Error is not null,
				Status=response.Error?.Code ?? "ok",
				ErrorMessage=response.Error?.Message,
				ErrorDetails=response.Error is null ? "" : Describe(response.Error)+(string.IsNullOrEmpty(response.Error.LocalDiagnostic) ? "" : Environment.NewLine+Environment.NewLine+response.Error.LocalDiagnostic),
				Result=response.Error is not null ? "" : Describe(response.Result),
			};
			lock(sync) {
				entry.Sequence=++lastSequence;
				entries.Enqueue(entry);
				while(entries.Count>capacity) entries.Dequeue();
			}
			Recorded?.Invoke(entry);
		}

		static string Describe(object? value) {
			if(value is null) return "null";
			using var stream=new BudgetStream(PayloadLimit);
			try { JsonSerializer.Serialize(stream,value,prettyOptions); }
			catch(BudgetExceededException) { }
			// A serializer aborted mid-document leaves unbalanced JSON. That is fine to show as long
			// as it says so; parsing it back is never attempted.
			catch(Exception ex) { return "<not serializable: "+ex.Message+">"; }
			var text=stream.Text();
			return stream.Exceeded ? text+Environment.NewLine+"... (truncated at "+PayloadLimit+" bytes)" : text;
		}

		sealed class BudgetExceededException : Exception { }

		/// <summary>Write-only sink that stops the serializer once the entry has cost enough.</summary>
		sealed class BudgetStream : Stream {
			readonly MemoryStream inner=new MemoryStream();
			readonly int budget;
			public BudgetStream(int budget) { this.budget=budget; }
			public bool Exceeded { get; private set; }
			public string Text()=>new UTF8Encoding(false).GetString(inner.ToArray());
			public override void Write(byte[] buffer,int offset,int count) {
				var room=budget-(int)inner.Length;
				if(count<=room) { inner.Write(buffer,offset,count); return; }
				Exceeded=true;
				if(room>0) inner.Write(buffer,offset,room);
				throw new BudgetExceededException();
			}
			public override bool CanRead=>false;
			public override bool CanSeek=>false;
			public override bool CanWrite=>true;
			public override long Length=>inner.Length;
			public override long Position { get=>inner.Position; set=>throw new NotSupportedException(); }
			public override void Flush() { }
			public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
			public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
			public override void SetLength(long value)=>throw new NotSupportedException();
			protected override void Dispose(bool disposing) { if(disposing) inner.Dispose(); base.Dispose(disposing); }
		}
	}
}
