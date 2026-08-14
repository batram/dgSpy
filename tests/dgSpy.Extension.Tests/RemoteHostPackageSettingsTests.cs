using System.Text.Json;
using Xunit;

namespace dgSpy.Extension.Tests;

[CollectionDefinition("remote-package-environment",DisableParallelization=true)]
public sealed class RemotePackageEnvironmentCollection { }

[Collection("remote-package-environment")]
public sealed class RemoteHostPackageSettingsTests : IDisposable {
	readonly string root=Path.Combine(Path.GetTempPath(),"dgspy-remote-settings-"+Guid.NewGuid().ToString("N"));
	readonly Dictionary<string,string?> environment=new();
	static readonly string[] Variables={ "DGSPY_STATE_ROOT","DGSPY_HOST_ID","DGSPY_RPC_TOKEN","DGSPY_GATEWAY_ADDRESS","DGSPY_GATEWAY_PORT","DGSPY_GATEWAY_TRANSPORT","DGSPY_CLIENT_CERTIFICATE_FILE","DGSPY_CLIENT_CERTIFICATE_PASSWORD_FILE","DGSPY_GATEWAY_CERTIFICATE_FILE" };

	public RemoteHostPackageSettingsTests() {
		Directory.CreateDirectory(Path.Combine(root,"state"));
		foreach(var variable in Variables) { environment[variable]=Environment.GetEnvironmentVariable(variable); Environment.SetEnvironmentVariable(variable,null); }
		File.WriteAllText(Path.Combine(root,"state","host.id"),"winagain-iis");
		File.WriteAllText(Path.Combine(root,"state","rpc.token"),"package-token");
	}

	[Fact]
	public void Direct_dnspy_launch_uses_personalized_plaintext_package() {
		WriteConfiguration(new { gateway_address="192.168.2.115",gateway_port=7352,transport="plaintext" });
		var identity=RpcSecuritySettings.Load(root);
		Assert.Equal("winagain-iis",identity.HostId);
		Assert.Equal("package-token",identity.Token);
		Assert.True(RemoteGatewaySettings.TryLoad(root,out var gateway));
		Assert.Equal("192.168.2.115",gateway.Address);
		Assert.Equal(7352,gateway.Port);
		Assert.False(gateway.UseTls);
	}

	[Fact]
	public void Direct_dnspy_launch_resolves_package_relative_tls_credentials() {
		WriteConfiguration(new { gateway_address="gateway.test",gateway_port=7353,transport="tls",client_certificate_file="certificates/client.pfx",client_certificate_password_file="certificates/client.password",gateway_certificate_file="certificates/gateway-server.cer" });
		Assert.True(RemoteGatewaySettings.TryLoad(root,out var gateway));
		Assert.True(gateway.UseTls);
		Assert.Equal(Path.Combine(root,"certificates","client.pfx"),gateway.ClientCertificateFile);
		Assert.Equal(Path.Combine(root,"certificates","client.password"),gateway.ClientCertificatePasswordFile);
		Assert.Equal(Path.Combine(root,"certificates","gateway-server.cer"),gateway.GatewayCertificateFile);
	}

	[Fact]
	public void Explicit_environment_still_overrides_the_package() {
		WriteConfiguration(new { gateway_address="package.test",gateway_port=7353,transport="tls" });
		Environment.SetEnvironmentVariable("DGSPY_GATEWAY_ADDRESS","override.test");
		Environment.SetEnvironmentVariable("DGSPY_GATEWAY_PORT","7444");
		Environment.SetEnvironmentVariable("DGSPY_GATEWAY_TRANSPORT","plaintext");
		Environment.SetEnvironmentVariable("DGSPY_HOST_ID","override-host");
		Environment.SetEnvironmentVariable("DGSPY_RPC_TOKEN","override-token");
		var identity=RpcSecuritySettings.Load(root);
		Assert.Equal("override-host",identity.HostId);
		Assert.Equal("override-token",identity.Token);
		Assert.True(RemoteGatewaySettings.TryLoad(root,out var gateway));
		Assert.Equal("override.test",gateway.Address);
		Assert.Equal(7444,gateway.Port);
		Assert.False(gateway.UseTls);
	}

	void WriteConfiguration(object value) => File.WriteAllText(Path.Combine(root,"remote-host.json"),JsonSerializer.Serialize(value));
	public void Dispose() {
		foreach(var item in environment) Environment.SetEnvironmentVariable(item.Key,item.Value);
		if(Directory.Exists(root)) Directory.Delete(root,true);
	}
}
