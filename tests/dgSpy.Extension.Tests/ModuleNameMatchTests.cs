using dgSpy.Extension;
using Xunit;

namespace dgSpy.Extension.Tests;

/// <summary>The measured failures that produced <see cref="ModuleNameMatch"/>, written down as tests.
/// Each one was observed live: the first two against a Unity player, the last against powershell.exe
/// under CorDebug.</summary>
public sealed class ModuleNameMatchTests {
	const string UnityName="Assembly-CSharp.dll";
	const string UnityPath=@"C:\Games\UCH\UltimateChickenHorse_Data\Managed\Assembly-CSharp.dll";

	[Fact]
	public void The_reported_bug_a_stem_without_its_extension_resolves() {
		// get_csharp(module: "Assembly-CSharp") answered module_not_found while search(module:
		// "Assembly-CSharp") searched the same module. Equality missed because Name and Filename both
		// carry the ".dll", and EndsWith("\\Assembly-CSharp") missed because the path ends in it too.
		Assert.Equal(ModuleNameMatch.Stemmed,ModuleNameMatch.Rank(UnityName,UnityPath,"Assembly-CSharp"));
		Assert.True(ModuleNameMatch.Matches(UnityName,UnityPath,"Assembly-CSharp"));
	}

	[Theory]
	[InlineData("Assembly-CSharp.dll")]
	[InlineData(UnityPath)]
	public void An_exact_name_or_path_still_ranks_first(string query) =>
		Assert.Equal(ModuleNameMatch.Exact,ModuleNameMatch.Rank(UnityName,UnityPath,query));

	[Fact]
	public void A_substring_matches_but_ranks_below_the_two_exact_forms() {
		Assert.Equal(ModuleNameMatch.Substring,ModuleNameMatch.Rank(UnityName,UnityPath,"CSharp"));
		Assert.Equal(ModuleNameMatch.Substring,ModuleNameMatch.Rank(UnityName,UnityPath,"Managed"));
	}

	[Fact]
	public void Ranking_is_what_keeps_a_stem_unambiguous_against_a_longer_neighbour() {
		// The reason the rule is ranked and not a flat substring. Both modules contain the query, so a
		// flat substring rule would answer ambiguous_target for the one name a caller is most likely to
		// type -- swapping one wrong answer for another.
		var wanted=ModuleNameMatch.Rank("Assembly-CSharp.dll",UnityPath,"Assembly-CSharp");
		var neighbour=ModuleNameMatch.Rank("Assembly-CSharp-firstpass.dll",
			@"C:\Games\UCH\UltimateChickenHorse_Data\Managed\Assembly-CSharp-firstpass.dll","Assembly-CSharp");
		Assert.True(wanted<neighbour,$"stem rank {wanted} must beat neighbour rank {neighbour}");
	}

	[Fact]
	public void Case_is_never_the_defect() {
		// The file on disk is Microsoft.PowerShell.PSReadLine.dll; list_modules reports
		// Microsoft.Powershell.PSReadline.dll. That transcription already resolved before this change --
		// every arm was OrdinalIgnoreCase -- and it must keep resolving.
		Assert.Equal(ModuleNameMatch.Exact,ModuleNameMatch.Rank("Microsoft.Powershell.PSReadline.dll","",
			"Microsoft.PowerShell.PSReadLine.dll"));
		Assert.Equal(ModuleNameMatch.Stemmed,ModuleNameMatch.Rank("Microsoft.Powershell.PSReadline.dll","",
			"Microsoft.PowerShell.PSReadLine"));
	}

	[Fact]
	public void An_unrelated_query_does_not_match() =>
		Assert.Equal(ModuleNameMatch.None,ModuleNameMatch.Rank(UnityName,UnityPath,"System.Xml"));

	[Fact]
	public void An_empty_filter_matches_everything_but_ranks_nothing() {
		Assert.True(ModuleNameMatch.Matches(UnityName,UnityPath,null));
		Assert.True(ModuleNameMatch.Matches(UnityName,UnityPath,""));
		Assert.Equal(ModuleNameMatch.None,ModuleNameMatch.Rank(UnityName,UnityPath,""));
	}

	[Fact]
	public void A_module_with_no_filename_still_matches_on_its_name() =>
		// In-memory and dynamic modules report a bare assembly name and no path.
		Assert.Equal(ModuleNameMatch.Exact,ModuleNameMatch.Rank("eval-1","","eval-1"));

	[Fact]
	public void An_in_memory_name_without_an_extension_accepts_the_metadata_filename() =>
		Assert.Equal(ModuleNameMatch.Stemmed,ModuleNameMatch.Rank("5wje15qw","","5wje15qw.dll"));

	[Fact]
	public void Near_misses_name_candidates_instead_of_sending_the_caller_to_list_modules() {
		var loaded=new (string?,string?)[] {
			("Assembly-CSharp.dll",UnityPath),
			("Assembly-CSharp-firstpass.dll",""),
			("UnityEngine.CoreModule.dll",""),
			("mscorlib.dll",""),
		};
		var candidates=ModuleNameMatch.NearMisses(loaded,"AssemblyCSharp.dll");
		Assert.Contains("Assembly-CSharp.dll",candidates);
		Assert.DoesNotContain("mscorlib.dll",candidates);
	}

	[Fact]
	public void A_query_longer_than_the_module_name_is_still_a_near_miss() {
		// The mirror case: "UnityEngine.CoreModule.Extras" contains a loaded module's whole stem.
		var loaded=new (string?,string?)[] { ("UnityEngine.CoreModule.dll",""),("mscorlib.dll","") };
		Assert.Contains("UnityEngine.CoreModule.dll",ModuleNameMatch.NearMisses(loaded,"UnityEngine.CoreModule.Extras"));
	}

	[Fact]
	public void Near_misses_are_bounded_and_can_be_empty() {
		var loaded=new (string?,string?)[] { ("mscorlib.dll",""),("System.dll","") };
		Assert.Empty(ModuleNameMatch.NearMisses(loaded,"Assembly-CSharp"));
	}
}
