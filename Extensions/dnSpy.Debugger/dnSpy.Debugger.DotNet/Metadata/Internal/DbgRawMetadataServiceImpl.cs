/*
    Copyright (C) 2014-2019 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.DotNet.Evaluation;
using dnSpy.Contracts.Debugger.DotNet.Metadata.Internal;

namespace dnSpy.Debugger.DotNet.Metadata.Internal {
	[Export(typeof(DbgRawMetadataService))]
	sealed class DbgRawMetadataServiceImpl : DbgRawMetadataService {
		sealed class RuntimeState : IDisposable {
			public readonly object LockObj = new object();
			public readonly Dictionary<ulong, DbgRawMetadataImpl> Dict = new Dictionary<ulong, DbgRawMetadataImpl>();
			public readonly List<DbgRawMetadataImpl> OtherMetadata = new List<DbgRawMetadataImpl>();
			// The engine's evaluation dispatcher, captured at Create time. Every metadata reader --
			// the Roslyn expression compiler included -- runs on this one thread (see
			// DbgEngineLocalsProviderImpl.GetNodes and its siblings, which all marshal onto it).
			public DbgDotNetDispatcher? Dispatcher;

			// Runs on the DbgManager dispatcher thread during DbgRuntimeImpl.CloseCore, while the
			// engine and its dispatcher thread are still alive (DbgManagerImpl closes the runtime
			// before the engine). Freeing the buffers right here raced Roslyn reading them through
			// raw MetadataBlock pointers on the engine thread and killed the process with an
			// AccessViolationException; refcount-based deferral was measured to crash the same way.
			// Instead: mark everything disposed first, so no reader that starts later can obtain the
			// addresses, then post the free onto the engine dispatcher itself. The free then runs
			// behind any in-flight evaluation on the only thread reads happen on, so read and free
			// can no longer overlap. ForceDispose suppresses finalization before the post: if the
			// dispatcher silently drops it during shutdown, the buffers remain allocated until process
			// exit rather than being freed concurrently on the finalizer thread.
			// See docs/local/dnspy-raw-metadata-use-after-free.md.
			public void Dispose() {
				DbgRawMetadataImpl[] all;
				DbgDotNetDispatcher? dispatcher;
				lock (LockObj) {
					all = new DbgRawMetadataImpl[Dict.Count + OtherMetadata.Count];
					Dict.Values.CopyTo(all, 0);
					OtherMetadata.CopyTo(all, Dict.Count);
					Dict.Clear();
					OtherMetadata.Clear();
					dispatcher = Dispatcher;
				}
				foreach (var m in all)
					m.ForceDispose();
				dispatcher?.TryBeginInvoke(() => {
					foreach (var m in all)
						m.FreeAfterQuiesce();
				});
			}
		}

		public override DbgRawMetadata Create(DbgRuntime runtime, bool isFileLayout, ulong moduleAddress, int moduleSize) {
			if (runtime is null)
				throw new ArgumentNullException(nameof(runtime));
			if (moduleAddress == 0)
				throw new ArgumentOutOfRangeException(nameof(moduleAddress));
			if (moduleSize <= 0)
				throw new ArgumentOutOfRangeException(nameof(moduleSize));

			var state = runtime.GetOrCreateData<RuntimeState>();
			lock (state.LockObj) {
				state.Dispatcher ??= (runtime.InternalRuntime as IDbgDotNetRuntime)?.Dispatcher;
				if (state.Dict.TryGetValue(moduleAddress, out var rawMd)) {
					if (rawMd.TryAddRef() is not null) {
						if (rawMd.IsFileLayout != isFileLayout || rawMd.Size != moduleSize) {
							rawMd.Release();
							throw new InvalidOperationException();
						}
						return rawMd;
					}
					state.Dict.Remove(moduleAddress);
				}

				rawMd = new DbgRawMetadataImpl(runtime.Process, isFileLayout, moduleAddress, moduleSize);
				try {
					state.Dict.Add(moduleAddress, rawMd);
				}
				catch {
					rawMd.Release();
					throw;
				}
				return rawMd;
			}
		}

		public override DbgRawMetadata Create(DbgRuntime runtime, bool isFileLayout, byte[] moduleBytes) {
			if (runtime is null)
				throw new ArgumentNullException(nameof(runtime));
			if (moduleBytes is null)
				throw new ArgumentNullException(nameof(moduleBytes));

			var state = runtime.GetOrCreateData<RuntimeState>();
			lock (state.LockObj) {
				state.Dispatcher ??= (runtime.InternalRuntime as IDbgDotNetRuntime)?.Dispatcher;
				var rawMd = new DbgRawMetadataImpl(moduleBytes, isFileLayout);
				try {
					state.OtherMetadata.Add(rawMd);
				}
				catch {
					rawMd.Release();
					throw;
				}
				return rawMd;
			}
		}
	}
}
