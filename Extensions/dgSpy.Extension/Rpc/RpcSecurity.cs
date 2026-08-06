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
			var root=Environment.GetEnvironmentVariable("DGSPY_STATE_ROOT") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"dgSpy");
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

	sealed class RemoteGatewaySettings {
		public string Address { get; } public int Port { get; } public bool UseTls { get; } public string? ClientCertificateFile { get; } public string? ClientCertificatePasswordFile { get; } public string? GatewayCertificateFile { get; }
		RemoteGatewaySettings(string address,int port,bool useTls,string? clientCertificateFile,string? clientCertificatePasswordFile,string? gatewayCertificateFile) { Address=address; Port=port; UseTls=useTls; ClientCertificateFile=clientCertificateFile; ClientCertificatePasswordFile=clientCertificatePasswordFile; GatewayCertificateFile=gatewayCertificateFile; }
		public static bool TryLoad(out RemoteGatewaySettings settings) {
			var address=Environment.GetEnvironmentVariable("DGSPY_GATEWAY_ADDRESS");
			if (string.IsNullOrWhiteSpace(address)) { settings=null!; return false; }
			if (!int.TryParse(Environment.GetEnvironmentVariable("DGSPY_GATEWAY_PORT"),out var port)) port=7352;
			if (port<1 || port>65535) throw new InvalidOperationException("DGSPY_GATEWAY_PORT is invalid.");
			var useTls=string.Equals(Environment.GetEnvironmentVariable("DGSPY_GATEWAY_TRANSPORT"),"tls",StringComparison.OrdinalIgnoreCase);
			var clientCertificateFile=Environment.GetEnvironmentVariable("DGSPY_CLIENT_CERTIFICATE_FILE"); var clientCertificatePasswordFile=Environment.GetEnvironmentVariable("DGSPY_CLIENT_CERTIFICATE_PASSWORD_FILE"); var gatewayCertificateFile=Environment.GetEnvironmentVariable("DGSPY_GATEWAY_CERTIFICATE_FILE");
			if (useTls && (string.IsNullOrWhiteSpace(clientCertificateFile) || string.IsNullOrWhiteSpace(clientCertificatePasswordFile) || string.IsNullOrWhiteSpace(gatewayCertificateFile))) throw new InvalidOperationException("TLS requires client certificate, password, and pinned Gateway certificate files.");
			settings=new RemoteGatewaySettings(address.Trim(),port,useTls,clientCertificateFile,clientCertificatePasswordFile,gatewayCertificateFile); return true;
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
