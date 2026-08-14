using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace dgSpy.Gateway.Tests;

public sealed class GatewayTokenStoreTests {
	[Fact]
	public async Task Concurrent_gateway_starters_share_one_token() {
		var root=Path.Combine(Path.GetTempPath(),"dgspy-token-test-"+Guid.NewGuid().ToString("N"));
		var path=Path.Combine(root,"gateway.token");
		try {
			var starters=Enumerable.Range(0,32).Select(_=>Task.Run(()=>global::GatewayTokenStore.ReadOrCreate(path))).ToArray();
			var tokens=await Task.WhenAll(starters);
			Assert.Single(tokens.Distinct(StringComparer.Ordinal));
			Assert.Equal(tokens[0],File.ReadAllText(path));
			Assert.Equal(64,tokens[0].Length);
		}
		finally { if(Directory.Exists(root)) Directory.Delete(root,true); }
	}
}
