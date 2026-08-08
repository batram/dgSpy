using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace dgSpy.Composition.Tests;

public class RawMetadataLifetimeTests {
	[Fact]
	public void Forced_teardown_does_not_free_from_the_finalizer_thread() {
		var type = PublishedHost.Instance.Assemblies
			.Select(a => a.GetType("dnSpy.Debugger.DotNet.Metadata.Internal.DbgRawMetadataImpl", throwOnError: false))
			.Single(t => t is not null)!;
		var references = CreateAndForceDispose(type);

		for (int i = 0; i < 3 && references.Metadata.IsAlive; i++) {
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
		}

		Assert.False(references.Metadata.IsAlive);
		Assert.True(references.ModuleBytes.IsAlive,
			"ForceDispose allowed the finalizer to release the pinned metadata buffer. " +
			"A dropped engine-dispatcher post could therefore free it while the engine thread still reads it.");
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static (WeakReference Metadata, WeakReference ModuleBytes) CreateAndForceDispose(Type type) {
		var moduleBytes = new byte[64];
		var metadata = Activator.CreateInstance(type,
			BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
			binder: null, args: new object[] { moduleBytes, true }, culture: null)
			?? throw new InvalidOperationException("Could not create DbgRawMetadataImpl");
		type.GetMethod("ForceDispose", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(metadata, null);
		return (new WeakReference(metadata), new WeakReference(moduleBytes));
	}
}
