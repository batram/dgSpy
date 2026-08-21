using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.DotNet;
using dnSpy.Contracts.Debugger.DotNet.Code;

namespace dgSpy.Extension.Debugger.OwnedBreakpoints {
	public enum OwnedBreakpointState { Pending, Bound, Failed, Released }

	public readonly struct OwnedBreakpointHit {
		public DbgProcess Process { get; }
		public DbgThread? Thread { get; }
		public DbgDotNetCodeLocation Location { get; }
		public Guid OwnerToken { get; }
		internal OwnedBreakpointHit(DbgRuntime runtime, DbgThread? thread, DbgDotNetCodeLocation location, Guid ownerToken) {
			Process=runtime.Process; Thread=thread; Location=location; OwnerToken=ownerToken;
		}
	}

	public delegate bool OwnedBreakpointHitHandler(in OwnedBreakpointHit hit);

	/// <summary>Internal breakpoint ownership across every debugger engine that offers it. It never
	/// consults the public breakpoint collection.
	///
	/// <para>Engine-neutral by import shape, not by branching: the providers arrive through
	/// <c>ImportMany</c> and each one answers for its own runtimes, so adding an engine is exporting a
	/// part rather than editing this file. It was a singleton import while CorDebug was the only
	/// implementation, and that import - not any property of the soft debugger - is what made HookLab
	/// arrival CorDebug-only.</para></summary>
	[Export(typeof(OwnedBreakpointService))]
	[PartCreationPolicy(CreationPolicy.Shared)]
	public sealed class OwnedBreakpointService : IDisposable {
		readonly object sync=new object();
		readonly IDgSpyOwnedBreakpointProvider[] providers;
		readonly List<OwnedBreakpoint> owners=new List<OwnedBreakpoint>();
		readonly Dictionary<string,Guid> pendingStops=new Dictionary<string,Guid>(StringComparer.Ordinal);
		bool disposed;
		[ImportingConstructor]
		OwnedBreakpointService([ImportMany] IEnumerable<IDgSpyOwnedBreakpointProvider> providers) => this.providers=providers.ToArray();

		/// <summary>The one provider whose engine controls this runtime, or null.
		///
		/// <para>Two providers claiming one runtime is a composition mistake rather than a preference to
		/// resolve, and it would show up as an intermittently wrong engine. It is refused by name.</para></summary>
		internal IDgSpyOwnedBreakpointProvider? ProviderFor(DbgRuntime runtime) {
			if (runtime is null) throw new ArgumentNullException(nameof(runtime));
			IDgSpyOwnedBreakpointProvider? found=null;
			foreach (var provider in providers) {
				if (!provider.IsSupported(runtime)) continue;
				if (found is not null)
					throw new InvalidOperationException("Two debugger engines claim the same runtime for owned breakpoints: "+
						found.EngineName+" and "+provider.EngineName+".");
				found=provider;
			}
			return found;
		}

		/// <summary>The engines that could own a breakpoint here at all, for refusal text and diagnostics.</summary>
		public IReadOnlyList<string> EngineNames => providers.Select(provider=>provider.EngineName).OrderBy(name=>name,StringComparer.Ordinal).ToArray();

		public bool IsSupported(DbgRuntime runtime) => ProviderFor(runtime) is not null;
		public Task ReconcileRunAsync(DbgRuntime runtime,CancellationToken cancellationToken) {
			var provider=ProviderFor(runtime);
			return EngineRunReconciliation.RunAsync(provider is not null,
				completed=>provider!.ReconcileRun(runtime,completed),cancellationToken);
		}
		public IReadOnlyList<OwnedBreakpoint> Owners { get { lock(sync) return owners.ToArray(); } }

		public async Task<OwnedBreakpoint> AddOwnerAsync(DbgRuntime runtime, DbgDotNetCodeLocation location,
			OwnedBreakpointHitHandler onHit, CancellationToken cancellationToken) {
			if (runtime is null) throw new ArgumentNullException(nameof(runtime));
			if (location is null) throw new ArgumentNullException(nameof(location));
			if (onHit is null) throw new ArgumentNullException(nameof(onHit));
			var provider=ProviderFor(runtime) ?? throw new NotSupportedException(
				"Owned internal breakpoints are unavailable for this debugger engine. Engines that offer them: "+
				(EngineNames.Count==0?"none":String.Join(", ",EngineNames))+".");
			cancellationToken.ThrowIfCancellationRequested();
			var owner=new OwnedBreakpoint(this,runtime,location,Guid.NewGuid());
			lock(sync) {
				if (disposed) throw new ObjectDisposedException(nameof(OwnedBreakpointService));
				owners.Add(owner);
			}
			var created=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			provider.Create(runtime,location.Module,location.Token,location.Offset,thread => {
				owner.RecordHit();
				var hit=new OwnedBreakpointHit(runtime,thread,location,owner.OwnerToken);
				bool pause;
				try { pause=onHit(in hit); }
				catch { pause=false; }
				if (pause) lock(sync) pendingStops[StopKey(runtime.Process,thread)]=owner.OwnerToken;
				return pause;
			},(handle,error) => {
				if (handle is null) owner.SetFailed(error ?? ("The "+provider.EngineName+" engine did not create the owned breakpoint."));
				else owner.SetHandle(handle);
				created.TrySetResult(true);
			});
			try {
				await WaitWithCancellationAsync(created.Task,cancellationToken).ConfigureAwait(false);
				return owner;
			}
			catch {
				await owner.ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
				throw;
			}
		}

		public async Task<int> ReclaimAbandonedAsync(CancellationToken cancellationToken) {
			OwnedBreakpoint[] snapshot;
			lock(sync) snapshot=owners.ToArray();
			foreach (var owner in snapshot)
				await owner.ReleaseAsync(cancellationToken).ConfigureAwait(false);
			return snapshot.Length;
		}

		internal void Forget(OwnedBreakpoint owner) { lock(sync) owners.Remove(owner); }
		internal bool TryConsumeStopAttribution(DbgProcess process, DbgThread? thread, out Guid ownerToken) {
			lock(sync) {
				var key=StopKey(process,thread);
				if (!pendingStops.TryGetValue(key,out ownerToken)) return false;
				pendingStops.Remove(key);
				return true;
			}
		}
		static string StopKey(DbgProcess process,DbgThread? thread) => process.Id+":"+(thread?.Id.ToString() ?? "none");

		static async Task WaitWithCancellationAsync(Task task,CancellationToken cancellationToken) {
			if (!cancellationToken.CanBeCanceled) { await task.ConfigureAwait(false); return; }
			var canceled=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			using (cancellationToken.Register(()=>canceled.TrySetCanceled()))
				await await Task.WhenAny(task,canceled.Task).ConfigureAwait(false);
		}

		public void Dispose() {
			OwnedBreakpoint[] snapshot;
			lock(sync) { if(disposed) return; disposed=true; snapshot=owners.ToArray(); }
			foreach(var owner in snapshot) owner.Dispose();
		}
	}

	public sealed class OwnedBreakpoint : IDisposable {
		readonly object sync=new object();
		readonly OwnedBreakpointService service;
		IDgSpyOwnedBreakpointHandle? handle;
		TaskCompletionSource<OwnedBreakpointState> bound=new TaskCompletionSource<OwnedBreakpointState>(TaskCreationOptions.RunContinuationsAsynchronously);
		Task? releaseTask;
		long hits;

		internal OwnedBreakpoint(OwnedBreakpointService service,DbgRuntime runtime,DbgDotNetCodeLocation location,Guid ownerToken) {
			this.service=service; Runtime=runtime; Location=location; OwnerToken=ownerToken; State=OwnedBreakpointState.Pending;
		}
		internal DbgRuntime Runtime { get; }
		public Guid OwnerToken { get; }
		public DbgDotNetCodeLocation Location { get; }
		public OwnedBreakpointState State { get; private set; }
		public string? BindError { get; private set; }
		public long Hits => Interlocked.Read(ref hits);
		internal void RecordHit() => Interlocked.Increment(ref hits);
		internal void SetHandle(IDgSpyOwnedBreakpointHandle value) {
			bool release;
			lock(sync) {
				release=State==OwnedBreakpointState.Released;
				if(!release) {
					handle=value;
					State=value.IsBound ? OwnedBreakpointState.Bound : OwnedBreakpointState.Failed;
					BindError=value.IsBound ? null : value.BindError;
					bound.TrySetResult(State);
				}
			}
			// Creation is posted and cannot be canceled once queued. If release won the race, drain the
			// late engine object immediately rather than orphaning it outside the owner registry.
			if(release) value.Remove(()=>{});
		}
		internal void SetFailed(string error) { lock(sync) { if(State==OwnedBreakpointState.Released) return; State=OwnedBreakpointState.Failed; BindError=error; bound.TrySetResult(State); } }
		public Task<OwnedBreakpointState> WaitBoundAsync(CancellationToken cancellationToken) => WaitBoundCoreAsync(cancellationToken);
		async Task<OwnedBreakpointState> WaitBoundCoreAsync(CancellationToken cancellationToken) {
			await OwnedBreakpointServiceWait(bound.Task,cancellationToken).ConfigureAwait(false);
			return State;
		}
		static async Task OwnedBreakpointServiceWait(Task task,CancellationToken cancellationToken) {
			if (!cancellationToken.CanBeCanceled) { await task.ConfigureAwait(false); return; }
			var canceled=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			using(cancellationToken.Register(()=>canceled.TrySetCanceled())) await await Task.WhenAny(task,canceled.Task).ConfigureAwait(false);
		}
		public Task ReleaseAsync(CancellationToken cancellationToken) { lock(sync) {
			if(releaseTask is null) releaseTask=ReleaseCoreAsync();
			return cancellationToken.CanBeCanceled ? AwaitReleaseAsync(releaseTask,cancellationToken) : releaseTask;
		} }
		async Task ReleaseCoreAsync() {
			IDgSpyOwnedBreakpointHandle? value;
			lock(sync) value=handle;
			if(value is not null) {
				var removed=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
				value.Remove(()=>removed.TrySetResult(true));
				await removed.Task.ConfigureAwait(false);
			}
			lock(sync) { handle=null; State=OwnedBreakpointState.Released; bound.TrySetResult(State); }
			service.Forget(this);
		}
		static async Task AwaitReleaseAsync(Task task,CancellationToken cancellationToken) {
			var canceled=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			using(cancellationToken.Register(()=>canceled.TrySetCanceled())) await await Task.WhenAny(task,canceled.Task).ConfigureAwait(false);
		}
		public void Dispose() { _=ReleaseAsync(CancellationToken.None); }
	}
}
