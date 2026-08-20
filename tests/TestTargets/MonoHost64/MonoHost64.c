/* A minimal x64 console host for Unity's embedded Mono runtime.
 *
 * The compatibility probe's Mono leg needs an x64 process running the Mono a Unity player runs, and
 * Unity does not ship one: MonoBleedingEdge\bin\mono.exe is x86 only, and dgSpy is x64. What is x64 is
 * the runtime the player embeds, MonoBleedingEdge\EmbedRuntime\mono-2.0-bdwgc.dll, which exports
 * mono_main - so loading that DLL and calling mono_main gives exactly the runtime under test, in the
 * right architecture, with no game, editor project, or mod loader involved.
 *
 * That standalone mono.exe is also not merely the wrong architecture. Its class libraries are a
 * CoreFX-derived set whose named-pipe servers P/Invoke a System.Native shim absent on Windows, while a
 * player's are classic Mono; a leg run against it measures a runtime no player has. Point MONO_PATH at
 * the editor's unityjit-win32 profile, which is byte-identical to a shipped player's Managed directory.
 *
 * usage: MonoHost64.exe <mono-2.0-*.dll> <assembly.exe> [args...]
 */
#include <windows.h>
#include <stdio.h>

typedef int(__cdecl *mono_main_t)(int argc, char **argv);

int main(int argc, char **argv) {
	HMODULE runtime;
	mono_main_t mono_main;
	if (argc < 3) {
		fprintf(stderr, "usage: MonoHost64 <mono-2.0-*.dll> <assembly.exe> [args...]\n");
		return 2;
	}
	runtime = LoadLibraryA(argv[1]);
	if (!runtime) {
		fprintf(stderr, "MonoHost64: LoadLibrary('%s') failed with %lu\n", argv[1], GetLastError());
		return 3;
	}
	mono_main = (mono_main_t)(void *)GetProcAddress(runtime, "mono_main");
	if (!mono_main) {
		fprintf(stderr, "MonoHost64: '%s' exports no mono_main\n", argv[1]);
		return 4;
	}
	/* mono_main expects mono.exe's own argv: argv[0] the runtime, then the assembly and its arguments.
	   Overwriting argv[1] and passing argv + 1 keeps that shape without copying the vector. */
	argv[1] = argv[0];
	return mono_main(argc - 1, argv + 1);
}
