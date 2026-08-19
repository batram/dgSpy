// Road 1 subslice 6 prototype: can native initialization enter a CHOSEN AppDomain?
//
// The shipped bootstrap enters through ICLRRuntimeHost::ExecuteInDefaultAppDomain, which by
// construction can only ever reach the default domain. Every IIS worker runs application code in a
// secondary domain, so the resident lands where the application's assemblies are not.
//
// This prototype takes the other hosting interface. ICorRuntimeHost predates ICLRRuntimeHost and is
// still available on CLR v4 through CLSID_CorRuntimeHost. It can enumerate live AppDomains and return
// each one as an _AppDomain - the managed System.AppDomain seen through COM - so anything AppDomain
// offers managed code is reachable here, including creating an object inside a specific domain.
//
// CreateInstanceFrom is used rather than a method invoke because the constructor runs in the target
// domain, which is the whole question. Product code would want a real entry point and a failure path.
//
// Everything is written to a file: this runs on an injected thread inside someone else's process,
// where there is no console and an exception has nowhere to go.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <metahost.h>
#include <mscoree.h>
#include <string>

// mscorlib's type library declares _AppDomain. raw_interfaces_only keeps the smart-pointer wrappers
// out; this code manages lifetimes explicitly so every failure path is visible.
#import "mscorlib.tlb" raw_interfaces_only rename("ReportEvent","mscorlib_ReportEvent")

#pragma comment(lib, "mscoree.lib")

static HMODULE g_module;

// The domain to enter. A prototype hardcodes it; the product would carry it in initialize.params,
// which already threads an appdomain_id everywhere except the native path.
static const wchar_t* const TargetDomainName=L"dgspy-appdomain-proof";

static std::wstring ModuleDirectory() {
	wchar_t path[32768];
	const DWORD length=GetModuleFileNameW(g_module,path,static_cast<DWORD>(_countof(path)));
	if(length==0||length>=_countof(path)) return L"";
	std::wstring value(path,length);
	const size_t slash=value.find_last_of(L"\\/");
	return slash==std::wstring::npos?L"":value.substr(0,slash);
}

static void Write(const std::wstring& directory,const wchar_t* name,const std::wstring& text) {
	const std::wstring path=directory+L"\\"+name;
	HANDLE file=CreateFileW(path.c_str(),GENERIC_WRITE,FILE_SHARE_READ,nullptr,CREATE_ALWAYS,FILE_ATTRIBUTE_NORMAL,nullptr);
	if(file==INVALID_HANDLE_VALUE) return;
	DWORD written=0;
	WriteFile(file,text.c_str(),static_cast<DWORD>(text.size()*sizeof(wchar_t)),&written,nullptr);
	CloseHandle(file);
}

static std::wstring Hex(const wchar_t* label,HRESULT hr) {
	wchar_t buffer[64];
	swprintf_s(buffer,L"%s=0x%08X\r\n",label,static_cast<unsigned int>(hr));
	return buffer;
}

static DWORD WINAPI Worker(void*) {
	const std::wstring directory=ModuleDirectory();
	const std::wstring assembly=directory+L"\\AppDomainProof.Payload.dll";
	std::wstring log;

	ICLRMetaHost* metaHost=nullptr;
	ICLRRuntimeInfo* runtimeInfo=nullptr;
	ICorRuntimeHost* corHost=nullptr;

	HRESULT hr=CLRCreateInstance(CLSID_CLRMetaHost,IID_ICLRMetaHost,reinterpret_cast<void**>(&metaHost));
	log+=Hex(L"create_metahost",hr);
	if(SUCCEEDED(hr)) { hr=metaHost->GetRuntime(L"v4.0.30319",IID_ICLRRuntimeInfo,reinterpret_cast<void**>(&runtimeInfo)); log+=Hex(L"get_runtime",hr); }
	BOOL loaded=FALSE;
	if(SUCCEEDED(hr)) { hr=runtimeInfo->IsLoaded(GetCurrentProcess(),&loaded); log+=Hex(L"is_loaded",hr); }
	if(SUCCEEDED(hr)&&!loaded) hr=HRESULT_FROM_WIN32(ERROR_NOT_READY);

	// The one line that differs from the shipped bootstrap: CorRuntimeHost, not CLRRuntimeHost.
	if(SUCCEEDED(hr)) { hr=runtimeInfo->GetInterface(CLSID_CorRuntimeHost,IID_ICorRuntimeHost,reinterpret_cast<void**>(&corHost)); log+=Hex(L"get_corruntimehost",hr); }

	bool entered=false;
	if(SUCCEEDED(hr)) {
		HDOMAINENUM handle=nullptr;
		hr=corHost->EnumDomains(&handle);
		log+=Hex(L"enum_domains",hr);
		if(SUCCEEDED(hr)) {
			int index=0;
			for(;;) {
				IUnknown* unknown=nullptr;
				if(corHost->NextDomain(handle,&unknown)!=S_OK||unknown==nullptr) break;
				mscorlib::_AppDomain* domain=nullptr;
				if(SUCCEEDED(unknown->QueryInterface(__uuidof(mscorlib::_AppDomain),reinterpret_cast<void**>(&domain)))&&domain!=nullptr) {
					BSTR name=nullptr;
					if(SUCCEEDED(domain->get_FriendlyName(&name))&&name!=nullptr) {
						wchar_t line[512];
						swprintf_s(line,L"domain[%d]=%s\r\n",index,name);
						log+=line;
						if(wcscmp(name,TargetDomainName)==0) {
							BSTR assemblyPath=SysAllocString(assembly.c_str());
							BSTR typeName=SysAllocString(L"AppDomainProof.Payload.DomainEntry");
							mscorlib::_ObjectHandle* handleOut=nullptr;
							const HRESULT created=domain->CreateInstanceFrom(assemblyPath,typeName,&handleOut);
							log+=Hex(L"create_instance_from",created);
							if(SUCCEEDED(created)) entered=true;
							if(handleOut) handleOut->Release();
							SysFreeString(typeName);
							SysFreeString(assemblyPath);
						}
						SysFreeString(name);
					}
					domain->Release();
				}
				unknown->Release();
				index++;
			}
			corHost->CloseEnum(handle);
		}
	}

	log+=std::wstring(L"entered_target_domain=")+(entered?L"true":L"false")+L"\r\n";
	log+=std::wstring(L"target_domain=")+TargetDomainName+L"\r\n";
	Write(directory,L"native-domain-proof.txt",log);

	if(corHost) corHost->Release();
	if(runtimeInfo) runtimeInfo->Release();
	if(metaHost) metaHost->Release();
	FreeLibraryAndExitThread(g_module,entered?0u:1u);
}

BOOL WINAPI DllMain(HINSTANCE instance,DWORD reason,LPVOID) {
	if(reason==DLL_PROCESS_ATTACH) {
		g_module=instance;
		DisableThreadLibraryCalls(instance);
		const HANDLE thread=CreateThread(nullptr,0,Worker,nullptr,0,nullptr);
		if(thread) CloseHandle(thread);
	}
	return TRUE;
}
