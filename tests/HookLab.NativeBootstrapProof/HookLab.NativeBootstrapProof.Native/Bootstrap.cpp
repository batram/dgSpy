#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <metahost.h>
#include <mscoree.h>
#include <string>

#pragma comment(lib, "mscoree.lib")

static HMODULE g_module;

static std::wstring DirectoryOfModule() {
	wchar_t path[MAX_PATH];
	DWORD length=GetModuleFileNameW(g_module,path,MAX_PATH);
	if(length==0 || length>=MAX_PATH) return L"";
	std::wstring value(path,length);
	size_t slash=value.find_last_of(L"\\/");
	return slash==std::wstring::npos ? L"" : value.substr(0,slash);
}

static void WriteResult(const std::wstring& directory,HRESULT hr,DWORD managedResult) {
	std::wstring path=directory+L"\\native-proof.txt";
	wchar_t text[256];
	int count=swprintf_s(text,L"hr=0x%08X\r\nmanaged_result=%lu\r\npid=%lu\r\n",static_cast<unsigned int>(hr),managedResult,GetCurrentProcessId());
	HANDLE file=CreateFileW(path.c_str(),GENERIC_WRITE,FILE_SHARE_READ,nullptr,CREATE_ALWAYS,FILE_ATTRIBUTE_NORMAL,nullptr);
	if(file==INVALID_HANDLE_VALUE) return;
	DWORD written=0;
	WriteFile(file,text,static_cast<DWORD>(count*sizeof(wchar_t)),&written,nullptr);
	CloseHandle(file);
}

static DWORD WINAPI BootstrapWorker(void*) {
	const std::wstring directory=DirectoryOfModule();
	const std::wstring assembly=directory+L"\\HookLab.NativeBootstrapProof.Managed.dll";
	HRESULT hr=E_FAIL;
	DWORD managedResult=0;
	ICLRMetaHost* metaHost=nullptr;
	ICLRRuntimeInfo* runtimeInfo=nullptr;
	ICLRRuntimeHost* runtimeHost=nullptr;

	hr=CLRCreateInstance(CLSID_CLRMetaHost,IID_ICLRMetaHost,reinterpret_cast<void**>(&metaHost));
	if(SUCCEEDED(hr)) hr=metaHost->GetRuntime(L"v4.0.30319",IID_ICLRRuntimeInfo,reinterpret_cast<void**>(&runtimeInfo));
	BOOL loaded=FALSE;
	if(SUCCEEDED(hr)) hr=runtimeInfo->IsLoaded(GetCurrentProcess(),&loaded);
	if(SUCCEEDED(hr) && !loaded) hr=HRESULT_FROM_WIN32(ERROR_NOT_READY);
	if(SUCCEEDED(hr)) hr=runtimeInfo->GetInterface(CLSID_CLRRuntimeHost,IID_ICLRRuntimeHost,reinterpret_cast<void**>(&runtimeHost));
	if(SUCCEEDED(hr)) hr=runtimeHost->ExecuteInDefaultAppDomain(assembly.c_str(),L"HookLab.NativeBootstrapProof.ManagedEntry",L"Initialize",directory.c_str(),&managedResult);

	WriteResult(directory,hr,managedResult);
	if(runtimeHost) runtimeHost->Release();
	if(runtimeInfo) runtimeInfo->Release();
	if(metaHost) metaHost->Release();
	return SUCCEEDED(hr) && managedResult==73 ? 0 : 1;
}

BOOL WINAPI DllMain(HINSTANCE instance,DWORD reason,LPVOID) {
	if(reason==DLL_PROCESS_ATTACH) {
		g_module=instance;
		DisableThreadLibraryCalls(instance);
		HANDLE thread=CreateThread(nullptr,0,BootstrapWorker,nullptr,0,nullptr);
		if(thread) CloseHandle(thread);
	}
	return TRUE;
}
