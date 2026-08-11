#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <metahost.h>
#include <mscoree.h>
#include <string>

#pragma comment(lib, "mscoree.lib")

static HMODULE moduleHandle;

static std::wstring ModuleDirectory() {
	wchar_t path[32768];
	const DWORD length=GetModuleFileNameW(moduleHandle,path,static_cast<DWORD>(_countof(path)));
	if(length==0 || length>=_countof(path)) return L"";
	std::wstring value(path,length);
	const size_t slash=value.find_last_of(L"\\/");
	return slash==std::wstring::npos ? L"" : value.substr(0,slash);
}

static DWORD WINAPI InitializeHookLab(void*) {
	const std::wstring directory=ModuleDirectory();
	const std::wstring assembly=directory+L"\\HookLab.Bootstrap.dll";
	const std::wstring parameters=directory+L"\\initialize.params";
	ICLRMetaHost* metaHost=nullptr;
	ICLRRuntimeInfo* runtimeInfo=nullptr;
	ICLRRuntimeHost* runtimeHost=nullptr;
	DWORD result=1;
	HRESULT hr=CLRCreateInstance(CLSID_CLRMetaHost,IID_ICLRMetaHost,reinterpret_cast<void**>(&metaHost));
	if(SUCCEEDED(hr)) hr=metaHost->GetRuntime(L"v4.0.30319",IID_ICLRRuntimeInfo,reinterpret_cast<void**>(&runtimeInfo));
	BOOL loaded=FALSE;
	if(SUCCEEDED(hr)) hr=runtimeInfo->IsLoaded(GetCurrentProcess(),&loaded);
	if(SUCCEEDED(hr) && !loaded) hr=HRESULT_FROM_WIN32(ERROR_NOT_READY);
	if(SUCCEEDED(hr)) hr=runtimeInfo->GetInterface(CLSID_CLRRuntimeHost,IID_ICLRRuntimeHost,reinterpret_cast<void**>(&runtimeHost));
	if(SUCCEEDED(hr)) hr=runtimeHost->ExecuteInDefaultAppDomain(assembly.c_str(),L"HookLab.Bootstrap.NativeEntry",L"Initialize",parameters.c_str(),&result);
	if(runtimeHost) runtimeHost->Release();
	if(runtimeInfo) runtimeInfo->Release();
	if(metaHost) metaHost->Release();
	const DWORD exitCode=SUCCEEDED(hr) ? result : static_cast<DWORD>(hr);
	FreeLibraryAndExitThread(moduleHandle,exitCode);
}

BOOL WINAPI DllMain(HINSTANCE instance,DWORD reason,LPVOID) {
	if(reason==DLL_PROCESS_ATTACH) {
		moduleHandle=instance;
		DisableThreadLibraryCalls(instance);
		const HANDLE worker=CreateThread(nullptr,0,InitializeHookLab,nullptr,0,nullptr);
		if(worker) CloseHandle(worker);
	}
	return TRUE;
}
