using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace dgSpy.Extension {
	/// <summary>Persistent endpoint identity and gateway credential. Environment variables are useful for
	/// managed or tunneled hosts; the local default is generated once below LocalApplicationData.</summary>
	sealed class RpcSecuritySettings {
		public string HostId { get; }
		public string Token { get; }
		RpcSecuritySettings(string hostId,string token) { HostId=hostId; Token=token; }

		public static RpcSecuritySettings Load() => Load(RemoteHostPackage.FindBundleRoot());
		internal static RpcSecuritySettings Load(string? bundleRoot) {
			var root=Environment.GetEnvironmentVariable("DGSPY_STATE_ROOT") ?? (bundleRoot is null ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"dgSpy") : Path.Combine(bundleRoot,"state"));
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
		public static bool TryLoad(out RemoteGatewaySettings settings) => TryLoad(RemoteHostPackage.FindBundleRoot(),out settings);
		internal static bool TryLoad(string? bundleRoot,out RemoteGatewaySettings settings) {
			var address=Environment.GetEnvironmentVariable("DGSPY_GATEWAY_ADDRESS");
			JsonObject? package=null;
			if (string.IsNullOrWhiteSpace(address) && bundleRoot is not null) {
				package=RemoteHostPackage.ReadConfiguration(bundleRoot);
				address=(string?)package["gateway_address"];
			}
			if (string.IsNullOrWhiteSpace(address)) { settings=null!; return false; }
			var configuredPort=Environment.GetEnvironmentVariable("DGSPY_GATEWAY_PORT");
			var port=string.IsNullOrWhiteSpace(configuredPort) ? (int?)package?["gateway_port"] ?? 7352 : int.TryParse(configuredPort,out var parsedPort) ? parsedPort : -1;
			if (port<1 || port>65535) throw new InvalidOperationException("DGSPY_GATEWAY_PORT is invalid.");
			var transport=Environment.GetEnvironmentVariable("DGSPY_GATEWAY_TRANSPORT") ?? (string?)package?["transport"];
			var useTls=string.Equals(transport,"tls",StringComparison.OrdinalIgnoreCase);
			var clientCertificateFile=Environment.GetEnvironmentVariable("DGSPY_CLIENT_CERTIFICATE_FILE") ?? RemoteHostPackage.Resolve(bundleRoot,(string?)package?["client_certificate_file"]); var clientCertificatePasswordFile=Environment.GetEnvironmentVariable("DGSPY_CLIENT_CERTIFICATE_PASSWORD_FILE") ?? RemoteHostPackage.Resolve(bundleRoot,(string?)package?["client_certificate_password_file"]); var gatewayCertificateFile=Environment.GetEnvironmentVariable("DGSPY_GATEWAY_CERTIFICATE_FILE") ?? RemoteHostPackage.Resolve(bundleRoot,(string?)package?["gateway_certificate_file"]);
			if (useTls && (string.IsNullOrWhiteSpace(clientCertificateFile) || string.IsNullOrWhiteSpace(clientCertificatePasswordFile) || string.IsNullOrWhiteSpace(gatewayCertificateFile))) throw new InvalidOperationException("TLS requires client certificate, password, and pinned Gateway certificate files.");
			settings=new RemoteGatewaySettings(address.Trim(),port,useTls,clientCertificateFile,clientCertificatePasswordFile,gatewayCertificateFile); return true;
		}
	}

	static class RemoteHostPackage {
		const string ConfigurationFile="remote-host.json";
		internal static string? FindBundleRoot() {
			var directory=Path.GetDirectoryName(typeof(RemoteHostPackage).Assembly.Location);
			while (!string.IsNullOrEmpty(directory)) {
				if (File.Exists(Path.Combine(directory,ConfigurationFile)) && File.Exists(Path.Combine(directory,"dnSpy.exe"))) return directory;
				var parent=Path.GetDirectoryName(directory);
				if (string.Equals(parent,directory,StringComparison.OrdinalIgnoreCase)) break;
				directory=parent;
			}
			return null;
		}
		internal static JsonObject ReadConfiguration(string bundleRoot) {
			var path=Path.Combine(bundleRoot,ConfigurationFile);
			try { return JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new InvalidOperationException($"{ConfigurationFile} is empty."); }
			catch (Exception ex) when (!(ex is InvalidOperationException)) { throw new InvalidOperationException($"The remote host configuration '{path}' is invalid: {ex.Message}",ex); }
		}
		internal static string? Resolve(string? bundleRoot,string? path) => string.IsNullOrWhiteSpace(path) || bundleRoot is null ? path : Path.GetFullPath(Path.Combine(bundleRoot,path));
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
