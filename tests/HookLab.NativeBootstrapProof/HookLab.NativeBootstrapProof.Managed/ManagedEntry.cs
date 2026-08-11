using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace HookLab.NativeBootstrapProof {
	public static class ManagedEntry {
		public static int Initialize(string directory) {
			var resultPath=Path.Combine(directory,"managed-proof.txt");
			File.WriteAllText(resultPath,$"pid={Process.GetCurrentProcess().Id}\r\nruntime={Environment.Version}\r\nutc={DateTime.UtcNow:O}\r\n",new UTF8Encoding(false));
			return 73;
		}
	}
}
