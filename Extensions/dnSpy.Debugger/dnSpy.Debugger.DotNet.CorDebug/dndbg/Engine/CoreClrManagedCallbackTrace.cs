/*
    Copyright (C) 2014-2019 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.
*/

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;

namespace dndbg.Engine {
	/// <summary>
	/// Opt-in diagnostic trace for managed callback terminal-disposition investigations. The debugger
	/// thread only enqueues bounded text; a background writer owns all file I/O.
	/// </summary>
	static class CoreClrManagedCallbackTrace {
		const int MaxQueuedRecords = 4096;
		static readonly string? path = Environment.GetEnvironmentVariable("DGSPY_CORECLR_CALLBACK_TRACE");
		static readonly ConcurrentQueue<string> records = new ConcurrentQueue<string>();
		static readonly AutoResetEvent available = new AutoResetEvent(false);
		static int queuedCount;
		static int started;

		public static bool IsEnabled => !string.IsNullOrWhiteSpace(path);

		public static void Record(DebugCallbackKind kind, string stage, int callbackCounter, Exception? exception = null, string? detail = null) {
			if (!IsEnabled)
				return;
			if (Interlocked.Increment(ref queuedCount) > MaxQueuedRecords) {
				Interlocked.Decrement(ref queuedCount);
				return;
			}
			var exceptionText = exception is null ? string.Empty : Convert.ToBase64String(Encoding.UTF8.GetBytes(exception.ToString()));
			records.Enqueue(string.Join("	",
				DateTime.UtcNow.ToString("O"),
				Environment.CurrentManagedThreadId.ToString(),
				kind.ToString(),
				stage,
				callbackCounter.ToString(),
				detail ?? string.Empty,
				exceptionText));
			EnsureWriter();
			available.Set();
		}

		static void EnsureWriter() {
			if (Interlocked.CompareExchange(ref started, 1, 0) != 0)
				return;
			var thread = new Thread(WriteLoop) {
				IsBackground = true,
				Name = "CoreCLR managed callback trace",
			};
			thread.Start();
		}

		static void WriteLoop() {
			try {
				var directory = Path.GetDirectoryName(path!);
				if (!string.IsNullOrEmpty(directory))
					Directory.CreateDirectory(directory);
				using (var writer = new StreamWriter(new FileStream(path!, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))) {
					writer.AutoFlush = true;
					for (;;) {
						while (records.TryDequeue(out var record)) {
							Interlocked.Decrement(ref queuedCount);
							writer.WriteLine(record);
						}
						available.WaitOne();
					}
				}
			}
			catch {
				// Diagnostic tracing must never affect debugger callback disposition.
			}
		}
	}
}
