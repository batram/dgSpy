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
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using dnlib.DotNet.MD;
using dnlib.PE;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.DotNet.Metadata.Internal;

namespace dnSpy.Debugger.DotNet.Metadata.Internal {
	sealed class DbgRawMetadataImpl : DbgRawMetadata {
		public override bool IsFileLayout {
			get {
				if (disposed)
					throw new ObjectDisposedException(nameof(DbgRawMetadataImpl));
				return isFileLayout;
			}
		}

		public override IntPtr Address {
			get {
				if (disposed)
					throw new ObjectDisposedException(nameof(DbgRawMetadataImpl));
				return address;
			}
		}

		public override int Size {
			get {
				if (disposed)
					throw new ObjectDisposedException(nameof(DbgRawMetadataImpl));
				return size;
			}
		}

		public override IntPtr MetadataAddress {
			get {
				if (disposed)
					throw new ObjectDisposedException(nameof(DbgRawMetadataImpl));
				return metadataAddress;
			}
		}

		public override int MetadataSize {
			get {
				if (disposed)
					throw new ObjectDisposedException(nameof(DbgRawMetadataImpl));
				return metadataSize;
			}
		}

		readonly bool isFileLayout;
		readonly IntPtr address;
		readonly int size;
		readonly IntPtr metadataAddress;
		readonly int metadataSize;
		readonly object lockObj;
		GCHandle moduleBytesHandle;
		readonly DbgProcess? process;
		readonly ulong moduleAddress;
		volatile int referenceCounter;
		volatile bool disposed;
		volatile int freedAddress;
		volatile int freedHandle;

		public DbgRawMetadataImpl(byte[] moduleBytes, bool isFileLayout) {
			lockObj = new object();
			referenceCounter = 1;
			this.isFileLayout = isFileLayout;
			size = moduleBytes.Length;
			moduleBytesHandle = GCHandle.Alloc(moduleBytes, GCHandleType.Pinned);
			address = moduleBytesHandle.AddrOfPinnedObject();
			(metadataAddress, metadataSize) = GetMetadataInfo();
		}

		public unsafe DbgRawMetadataImpl(DbgProcess process, bool isFileLayout, ulong moduleAddress, int moduleSize) {
			lockObj = new object();
			referenceCounter = 1;
			this.isFileLayout = isFileLayout;
			size = moduleSize;
			this.process = process;
			this.moduleAddress = moduleAddress;

			try {
				// Prevent allocation on the LOH. We'll also be able to free the memory as soon as it's not needed.
				address = NativeMethods.VirtualAlloc(IntPtr.Zero, new IntPtr(moduleSize), NativeMethods.MEM_COMMIT, NativeMethods.PAGE_READWRITE);
				if (address == IntPtr.Zero)
					throw new OutOfMemoryException();
				process.ReadMemory(moduleAddress, address.ToPointer(), size);
				(metadataAddress, metadataSize) = GetMetadataInfo();
			}
			catch {
				Dispose();
				throw;
			}
		}

		unsafe (IntPtr metadataAddress, int metadataSize) GetMetadataInfo() {
			try {
				var peImage = new PEImage(address, (uint)size, isFileLayout ? ImageLayout.File : ImageLayout.Memory, true);
				var dotNetDir = peImage.ImageNTHeaders.OptionalHeader.DataDirectories[14];
				// Mono doesn't check that the Size field is >= 0x48
				if (dotNetDir.VirtualAddress != 0 /*&& dotNetDir.Size >= 0x48*/) {
					var cor20Reader = peImage.CreateReader(dotNetDir.VirtualAddress, 0x48);
					var cor20 = new ImageCor20Header(ref cor20Reader, true);
					var mdStart = (long)peImage.ToFileOffset(cor20.Metadata.VirtualAddress);
					var mdAddr = new IntPtr((byte*)address + mdStart);
					var mdSize = (int)cor20.Metadata.Size;
					return (mdAddr, mdSize);
				}
			}
			catch (Exception ex) when (ex is IOException || ex is BadImageFormatException) {
				Debug.Fail("Couldn't read .NET metadata");
			}
			return (IntPtr.Zero, 0);
		}

		// A teardown object suppresses this finalizer in ForceDispose: if its engine-thread free is
		// dropped during dispatcher shutdown, freeing here could race the engine thread's last read.
		// That rare allocation is deliberately left for process exit instead.
		~DbgRawMetadataImpl() => Dispose();

		public unsafe override void UpdateMemory() {
			if (disposed)
				throw new ObjectDisposedException(nameof(DbgRawMetadataImpl));
			process?.ReadMemory(moduleAddress, address.ToPointer(), size);
		}

		internal DbgRawMetadata? TryAddRef() {
			lock (lockObj) {
				if (disposed)
					return null;
				referenceCounter++;
				return this;
			}
		}

		public override DbgRawMetadata AddRef() {
			if (disposed)
				throw new ObjectDisposedException(nameof(DbgRawMetadataImpl));
			lock (lockObj)
				referenceCounter++;
			return this;
		}

		// Release is the cleanup half of AddRef and must never throw. It used to reject an already
		// disposed object, which made routine teardown fatal: ForceDispose below marks every raw
		// metadata disposed when the runtime goes away, and only afterwards does the dispatcher close
		// the DbgModuleReferenceImpl objects that still hold references. Each of those Release calls
		// threw ObjectDisposedException out of DbgManagerImpl.CloseObjects_DbgThread, which abandoned
		// the remaining objects in that batch and recorded a dispatcher fault, leaving the host
		// registered but unable to serve execution control. Releasing something that is already gone is
		// exactly what the caller intends, so it is a no-op.
		public override void Release() {
			bool dispose;
			lock (lockObj) {
				if (referenceCounter <= 0)
					return;
				dispose = --referenceCounter == 0;
			}
			if (dispose)
				Dispose();
		}

		void Dispose() {
			lock (lockObj) {
				disposed = true;
				referenceCounter = 0;
			}
			GC.SuppressFinalize(this);
			FreeBuffers();
		}

		// Called on runtime teardown, on the DbgManager dispatcher thread. It only marks the object
		// disposed -- it must never free, because the Roslyn expression compiler may at this moment be
		// reading these buffers on the engine thread through MetadataBlock raw pointers captured
		// earlier. Freeing here dereferences freed memory under CompileGetLocals and kills the process
		// with an uncatchable AccessViolationException -- and a host holding an ICorDebug attachment
		// kills its debuggee when it dies, so it destroys the target too. Both freeing eagerly here
		// and deferring the free to the last Release() were measured to still crash: the reference
		// holders die in the same teardown microseconds later, and the racing reader holds no
		// reference at all, so no refcount arrangement can close the window.
		//
		// The free happens in FreeAfterQuiesce below, which DbgRawMetadataServiceImpl posts to the
		// engine's DbgDotNetDispatcher -- the one thread every metadata reader runs on -- so a free
		// cannot overlap an in-flight read, and a read that starts after it hits the `disposed`
		// guards set here. Zeroing the reference count makes every later Release() a no-op, so the
		// module references closed later in this same teardown can neither throw (which used to
		// abort DbgManagerImpl's close batch) nor trigger a free on the wrong thread. If the posted
		// callback is dropped because the engine dispatcher already shut down, ForceDispose suppresses
		// the finalizer and deliberately leaves the allocation for process exit. A finalizer-thread free
		// cannot prove the engine's last read has quiesced and would recreate the original race. See
		// docs/local/dnspy-raw-metadata-use-after-free.md.
		internal void ForceDispose() {
			lock (lockObj) {
				disposed = true;
				referenceCounter = 0;
			}
			GC.SuppressFinalize(this);
		}

		// Runs on the engine's DbgDotNetDispatcher thread, after ForceDispose, behind any in-flight
		// evaluation. This is the only place the teardown path frees.
		internal void FreeAfterQuiesce() {
			GC.SuppressFinalize(this);
			FreeBuffers();
		}

		void FreeBuffers() {
			if (process is not null && address != IntPtr.Zero && Interlocked.Exchange(ref freedAddress, 1) == 0) {
				bool b = NativeMethods.VirtualFree(address, IntPtr.Zero, NativeMethods.MEM_RELEASE);
				Debug.Assert(b);
			}
			if (process is null && Interlocked.Exchange(ref freedHandle, 1) == 0) {
				try {
					if (moduleBytesHandle.IsAllocated)
						moduleBytesHandle.Free();
				}
				catch (InvalidOperationException) {
				}
			}
		}
	}
}
