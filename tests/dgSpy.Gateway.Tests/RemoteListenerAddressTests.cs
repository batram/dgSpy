using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Xunit;

namespace dgSpy.Gateway.Tests;

/// <summary>The listener's address set. The rules here exist because a Gateway is routinely reachable on
/// more than one address — a LAN adapter and a Hyper-V or WSL virtual switch — and each remote host can
/// dial only the one it was provisioned with.</summary>
public sealed class RemoteListenerAddressTests {
	[Fact]
	public void Addresses_accumulate_in_order_and_de_duplicate() =>
		Assert.Equal(new[]{"192.168.2.115","172.31.224.1"},ListenerAddresses.Normalize(new[]{"192.168.2.115"," 172.31.224.1 ","","192.168.2.115"}));

	/// <summary>A wildcard beside a specific address is not two listeners: on one port they collide, and
	/// the wildcard already covers the specific address.</summary>
	[Theory]
	[InlineData("0.0.0.0")]
	[InlineData("any")]
	[InlineData("ALL")]
	[InlineData("*")]
	[InlineData("::")]
	public void A_wildcard_absorbs_every_address_listed_beside_it(string wildcard) =>
		Assert.Equal(new[]{"0.0.0.0"},ListenerAddresses.Normalize(new[]{"192.168.2.115",wildcard,"172.31.224.1"}));

	[Fact]
	public void Configured_addresses_each_bind_separately() =>
		Assert.Equal(new[]{IPAddress.Parse("192.168.2.115"),IPAddress.Parse("172.31.224.1")},ListenerAddresses.Bindable(new[]{"192.168.2.115","172.31.224.1"}));

	/// <summary>A DNS name is what a host dials from outside; it names the Gateway rather than any one of
	/// its interfaces, and nothing here can bind it. It used to abort listener activation outright even
	/// though the packaging tool advertises hostnames, so it widens the bind instead.</summary>
	[Fact]
	public void A_dns_name_widens_the_bind_instead_of_failing() =>
		Assert.Contains(IPAddress.Any,ListenerAddresses.Bindable(new[]{"gateway.example"}));

	[Fact]
	public void A_wildcard_covers_both_stacks_where_the_machine_has_both() =>
		Assert.Equal(Socket.OSSupportsIPv6 ? new[]{IPAddress.Any,IPAddress.IPv6Any} : new[]{IPAddress.Any},ListenerAddresses.Bindable(new[]{"any"}));

	[Fact]
	public void An_address_less_configuration_is_rejected_rather_than_silently_bound() =>
		Assert.Throws<InvalidOperationException>(()=>ListenerAddresses.Bindable(Array.Empty<string>()));

	/// <summary>A registry written before the listener held more than one address carries only the scalar
	/// field, and must keep working untouched.</summary>
	[Fact]
	public void A_registry_predating_the_address_set_still_reads() =>
		Assert.Equal(new[]{"192.168.2.115"},ListenerAddresses.Read(new JsonObject{["address"]="192.168.2.115",["plaintext_port"]=7352}));

	[Fact]
	public void The_address_set_wins_over_the_scalar_field_when_both_are_present() =>
		Assert.Equal(new[]{"192.168.2.115","172.31.224.1"},ListenerAddresses.Read(new JsonObject{["address"]="192.168.2.115",["addresses"]=new JsonArray("192.168.2.115","172.31.224.1")}));

	[Fact]
	public void An_environment_override_accepts_a_list() =>
		Assert.Equal(new[]{"192.168.2.115","172.31.224.1"},ListenerAddresses.Split("192.168.2.115, 172.31.224.1").ToArray());
}
