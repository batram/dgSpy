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

		// Reaching the finalizer means either process shutdown or a holder that never called Release --
		// including the deliberate case where ForceDispose deferred the free because references were
		// still outstanding. Both are recoveries rather than bugs to assert on, and the buffers must be
		// handed back either way.
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

		// Called on runtime teardown. It must mark the object disposed so no new reader can obtain the
		// addresses -- every reader above guards on `disposed`, and freeing without setting it left
		// those guards passing while the addresses were gone. Readers then dereferenced freed memory
		// through Roslyn MetadataBlock pointers and the process died of an AccessViolationException on
		// the engine thread -- TypeDefTableReader.GetName under CompileGetLocals, with no managed
		// exception anywhere to explain it. That is not catchable and takes the whole host down, which
		// is why a host would simply vanish a minute or so after a detach with nothing in any log.
		//
		// So the buffers are freed here only when nothing holds a reference any more. While references
		// are outstanding the free is deferred to the last Release, and the finalizer is deliberately
		// left armed as the backstop for a holder that never releases: the cost of deferring is at
		// worst a module-sized buffer living until the next GC, and the cost of not deferring is the
		// process. A racing reader that is already past its guard now reads memory that is still
		// mapped instead of memory that was handed back to the OS.
		internal void ForceDispose() {
			bool free;
			lock (lockObj) {
				disposed = true;
				free = referenceCounter <= 0;
			}
			if (free) {
				GC.SuppressFinalize(this);
				FreeBuffers();
			}
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
