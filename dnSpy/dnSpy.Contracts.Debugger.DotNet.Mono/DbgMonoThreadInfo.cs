/*
    Copyright (C) 2014-2019 de4dot@gmail.com

    This file is part of dnSpy
*/

namespace dnSpy.Contracts.Debugger.DotNet.Mono {
	/// <summary>Mono soft-debugger identity information attached to a <see cref="DbgThread"/>.</summary>
	public sealed class DbgMonoThreadInfo {
		/// <summary>True when <see cref="DbgThread.Id"/> is the operating-system thread id.</summary>
		public bool HasSystemThreadId { get; }
		/// <summary>Negotiated Mono soft-debugger protocol major version.</summary>
		public int ProtocolMajor { get; }
		/// <summary>Negotiated Mono soft-debugger protocol minor version.</summary>
		public int ProtocolMinor { get; }

		/// <summary>Constructor.</summary>
		public DbgMonoThreadInfo(bool hasSystemThreadId,int protocolMajor,int protocolMinor) {
			HasSystemThreadId=hasSystemThreadId;
			ProtocolMajor=protocolMajor;
			ProtocolMinor=protocolMinor;
		}
	}
}
