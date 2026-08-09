using System;
using System.Collections.Generic;
using HookLab.Contracts;

namespace HookLab.Probe.CorDebug.Patching {
	/// <summary>T05 drains this source and delivers the returned immutable events.</summary>
	public interface IHookEventSource {
		IReadOnlyList<HookEvent> Drain(int maximumCount);
		long DroppedCount { get; }
	}

	/// <summary>T05 implements this to receive a wake-up without making a hook callback block.</summary>
	public interface IHookEventConsumer {
		void EventsAvailable(IHookEventSource source);
	}

	public interface ITargetIdentityProvider {
		TargetIdentity GetCurrentIdentity();
	}

	public sealed class ProbeInitialization {
		public ProbeInitialization(TargetIdentity expectedTarget, ITargetIdentityProvider identityProvider,
			IHookEventConsumer? consumer = null, int eventCapacity = 1024, long byteCapacity = 4 * 1024 * 1024) {
			ExpectedTarget = expectedTarget ?? throw new ArgumentNullException(nameof(expectedTarget));
			IdentityProvider = identityProvider ?? throw new ArgumentNullException(nameof(identityProvider));
			Consumer = consumer;
			if (eventCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(eventCapacity));
			if (byteCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(byteCapacity));
			EventCapacity = eventCapacity;
			ByteCapacity = byteCapacity;
		}
		public TargetIdentity ExpectedTarget { get; }
		public ITargetIdentityProvider IdentityProvider { get; }
		public IHookEventConsumer? Consumer { get; }
		public int EventCapacity { get; }
		public long ByteCapacity { get; }
	}
}
