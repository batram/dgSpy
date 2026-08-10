using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace HookLab.Bootstrap.Tests {
	/// <summary>Runs the bootstrap in a child AppDomain.
	///
	/// A successful Start is one-shot per AppDomain and the resolver it installs is process-visible for the
	/// life of that domain, so sharing one domain across tests would make every result depend on execution
	/// order. Each case gets a clean slate instead.</summary>
	public sealed class BootstrapRunner : MarshalByRefObject, IDisposable {
		readonly AppDomain domain;
		readonly BootstrapRunner? inner;

		public BootstrapRunner() { domain = AppDomain.CurrentDomain; }

		BootstrapRunner(AppDomain domain, BootstrapRunner inner) { this.domain = domain; this.inner = inner; }

		public static BootstrapRunner Create(string name) {
			var setup = new AppDomainSetup { ApplicationBase = AppDomain.CurrentDomain.SetupInformation.ApplicationBase };
			var child = AppDomain.CreateDomain(name, null, setup);
			var proxy = (BootstrapRunner)child.CreateInstanceAndUnwrap(typeof(BootstrapRunner).Assembly.FullName, typeof(BootstrapRunner).FullName!);
			return new BootstrapRunner(child, proxy);
		}

		public override object? InitializeLifetimeService() => null;

		public string AppDomainId => inner != null ? inner.AppDomainId : AppDomain.CurrentDomain.Id.ToString(CultureInfo.InvariantCulture);

		public string Start(string parameters) => inner != null ? inner.Start(parameters) : HookLabBootstrap.Start(parameters);

		public string Shutdown() => inner != null ? inner.Shutdown() : HookLabBootstrap.Shutdown();

		/// <summary>Payload assemblies that are actually resident, and whether each came from disk. Empty
		/// Location means byte-loaded, which is the only provenance the bundle permits.</summary>
		public string[] ResidentPayloads() {
			if (inner != null) return inner.ResidentPayloads();
			return AppDomain.CurrentDomain.GetAssemblies()
				.Select(a => new { Name = SafeName(a), Location = SafeLocation(a) })
				.Where(a => a.Name == "HookLab.Contracts" || a.Name == "HookLab.Probe.CorDebug" || a.Name == "0Harmony")
				.Select(a => a.Name + "|" + (a.Location.Length == 0 ? "byte-loaded" : a.Location))
				.OrderBy(x => x, StringComparer.Ordinal)
				.ToArray();
		}

		/// <summary>Calls the fixture method, then drains the probe event buffer. Everything here is
		/// reflection because this assembly deliberately has no reference to the probe.</summary>
		public string[] InvokeFixtureAndDrain(int calls) {
			if (inner != null) return inner.InvokeFixtureAndDrain(calls);
			IFixtureWorker worker = new FixtureWorker();
			var results = new List<int>();
			for (var index = 0; index < calls; index++) results.Add(worker.Run(index + 1));
			var runtime = HookLabBootstrap.Runtime ?? throw new InvalidOperationException("No probe runtime.");
			var source = runtime.GetType().GetProperty("Events")!.GetValue(runtime)!;
			var drained = (IEnumerable)source.GetType().GetMethod("Drain")!.Invoke(source, new object[] { 64 })!;
			var payloads = new List<string>();
			foreach (var item in drained) {
				var type = item.GetType();
				payloads.Add((string)type.GetProperty("PatchId")!.GetValue(item)! + "|" + (string)type.GetProperty("PayloadJson")!.GetValue(item)!);
			}
			payloads.Insert(0, "results=" + string.Join(",", results.Select(r => r.ToString(CultureInfo.InvariantCulture))));
			return payloads.ToArray();
		}

		/// <summary>Byte-loads the bootstrap and drives it through reflection alone - the shape a debugger
		/// func-eval has to use. Nothing here names a bootstrap type, so a signature reflection cannot reach
		/// fails this even though the compiler was happy.</summary>
		public string StartByteLoaded(string parameters) {
			if (inner != null) return inner.StartByteLoaded(parameters);
			var path = System.IO.Path.Combine(AppDomain.CurrentDomain.SetupInformation.ApplicationBase!, "HookLab.Bootstrap.dll");
			var assembly = Assembly.Load(System.IO.File.ReadAllBytes(path));
			if (assembly.Location.Length != 0) throw new InvalidOperationException("The bootstrap was not byte-loaded.");
			var entry = assembly.GetType("HookLab.Bootstrap.HookLabBootstrap", true)!;
			return (string)entry.GetMethod("Start", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, new object[] { parameters })!;
		}

		/// <summary>Replaces the two shared cleanup handles with counting ones and disposes the live pair.
		/// mode: runtime-fails-always, runtime-fails-once, server-fails-once, both-fail-always.</summary>
		public string InstallFakeHandles(string mode) {
			if (inner != null) return inner.InstallFakeHandles(mode);
			FakeHandles.Install(mode);
			return FakeHandles.State();
		}

		public string FakeHandleState() => inner != null ? inner.FakeHandleState() : FakeHandles.State();

		/// <summary>Starts with the startup rollback's runtime disposal rigged to throw - a probe whose
		/// unpatch fails. The report has to carry both that and the failure that refused the bootstrap.</summary>
		public string StartWithFailingRollback(string parameters) {
			if (inner != null) return inner.StartWithFailingRollback(parameters);
			ProbeStartup.StartupRollbackFaultForTest = () => throw new InvalidOperationException("unpatch-failed-on-purpose");
			try { return HookLabBootstrap.Start(parameters); }
			finally { ProbeStartup.StartupRollbackFaultForTest = null; }
		}

		public string LoadDuplicateHookTargets(string mode) =>
			inner != null ? inner.LoadDuplicateHookTargets(mode) : DuplicateHookTargets.Load(mode);

		/// <summary>Starts against the duplicate-loaded target copy. An empty mvidOverride uses that copy's
		/// real MVID; anything else asks for a module the caller names instead.</summary>
		public string StartWithDuplicateHookTarget(string parameters, string hookId, string mvidOverride) {
			if (inner != null) return inner.StartWithDuplicateHookTarget(parameters, hookId, mvidOverride);
			return HookLabBootstrap.Start(parameters + DuplicateHookTargets.HookLines(hookId, mvidOverride));
		}

		public string InvokeCopiesAndDrain(int decoyCalls, int targetCalls) =>
			inner != null ? inner.InvokeCopiesAndDrain(decoyCalls, targetCalls) : DuplicateHookTargets.InvokeCopies(decoyCalls, targetCalls);

		/// <summary>Reads back a property of the live ProbeRuntime by name.</summary>
		public string RuntimeProperty(string name) {
			if (inner != null) return inner.RuntimeProperty(name);
			var runtime = HookLabBootstrap.Runtime ?? throw new InvalidOperationException("No probe runtime.");
			var value = runtime.GetType().GetProperty(name)!.GetValue(runtime);
			return value?.ToString() ?? "";
		}

		static string SafeName(Assembly assembly) { try { return assembly.GetName().Name ?? ""; } catch (Exception) { return ""; } }
		static string SafeLocation(Assembly assembly) { try { return assembly.IsDynamic ? "" : assembly.Location; } catch (Exception) { return ""; } }

		public void Dispose() {
			if (inner == null) return;
			try { inner.Shutdown(); } catch (Exception) { }
			// Unload can fail while the probe's background pipe listener is still winding down. A leaked
			// domain in a test process is noise; masking a real Shutdown failure would not be.
			try { AppDomain.Unload(domain); } catch (Exception) { }
		}
	}
}
