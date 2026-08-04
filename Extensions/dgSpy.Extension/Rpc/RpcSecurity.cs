using System;
using System.IO;
using System.Security.Cryptography;

namespace dgSpy.Extension {
	/// <summary>Persistent endpoint identity and gateway credential. Environment variables are useful for
	/// managed or tunneled hosts; the local default is generated once below LocalApplicationData.</summary>
	sealed class RpcSecuritySettings {
		public string HostId { get; }
		public string Token { get; }
		RpcSecuritySettings(string hostId,string token) { HostId=hostId; Token=token; }

		public static RpcSecuritySettings Load() {
			var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"dgSpy");
			Directory.CreateDirectory(root);
			var hostId=ReadOrCreate("DGSPY_HOST_ID",Path.Combine(root,"host.id"),()=>"host-"+Guid.NewGuid().ToString("N"));
			var token=ReadOrCreate("DGSPY_RPC_TOKEN",Path.Combine(root,"rpc.token"),CreateToken);
			return new RpcSecuritySettings(hostId,token);
		}

		static string ReadOrCreate(string variable,string path,Func<string> create) {
			var configured=Environment.GetEnvironmentVariable(variable);
			if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();
			if (File.Exists(path)) {
				var existing=File.ReadAllText(path).Trim();
				if (!string.IsNullOrEmpty(existing)) return existing;
			}
			var value=create();
			File.WriteAllText(path,value);
			return value;
		}

		static string CreateToken() {
			var bytes=new byte[32];
			using (var random=RandomNumberGenerator.Create()) random.GetBytes(bytes);
			return Convert.ToBase64String(bytes);
		}
	}

	static class RpcRequestAuthenticator {
		public static string? Reject(string operation,string? requestedHostId,string? presentedToken,string hostId,string expectedToken) {
			if (string.IsNullOrEmpty(expectedToken)) return "RPC authentication is not configured.";
			if (!FixedTimeEquals(presentedToken,expectedToken)) return "Missing or invalid extension RPC credential.";
			if (!string.IsNullOrEmpty(requestedHostId) && !string.Equals(requestedHostId,hostId,StringComparison.Ordinal))
				return $"Request targets host '{requestedHostId}', but this endpoint is '{hostId}'.";
			if (operation!="ping" && string.IsNullOrEmpty(requestedHostId)) return "Authenticated RPC requests must name host_id.";
			return null;
		}

		static bool FixedTimeEquals(string? presented,string expected) {
			if (presented is null) return false;
			var different=presented.Length ^ expected.Length;
			var count=Math.Max(presented.Length,expected.Length);
			for (var index=0;index<count;index++) {
				var left=index<presented.Length ? presented[index] : 0;
				var right=index<expected.Length ? expected[index] : 0;
				different |= left ^ right;
			}
			return different==0;
		}
	}
}
