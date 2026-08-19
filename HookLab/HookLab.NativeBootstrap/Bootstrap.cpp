#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <metahost.h>
#include <mscoree.h>
#include <string>
#include <vector>

// mscorlib's type library declares _AppDomain, which is how native code reaches a specific
// application domain. See EnterApplicationDomain below for why that is needed at all.
#import "mscorlib.tlb" raw_interfaces_only rename("ReportEvent","mscorlib_ReportEvent")

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

static bool ReadAllBytes(const std::wstring& path,std::vector<char>& bytes) {
	HANDLE file=CreateFileW(path.c_str(),GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr);
	if(file==INVALID_HANDLE_VALUE) return false;
	LARGE_INTEGER size{};
	bool ok=GetFileSizeEx(file,&size)!=FALSE && size.QuadPart>0 && size.QuadPart<64*1024*1024;
	if(ok) {
		bytes.resize(static_cast<size_t>(size.QuadPart));
		DWORD read=0;
		ok=ReadFile(file,bytes.data(),static_cast<DWORD>(bytes.size()),&read,nullptr)!=FALSE && read==bytes.size();
	}
	CloseHandle(file);
	return ok;
}

// One value out of initialize.params, which the controller writes as UTF-8 "key=value" lines. Only
// this key is read here; everything else stays the managed side's business, as before.
static std::wstring ReadParameter(const std::vector<char>& text,const char* key) {
	const std::string needle=std::string(key)+"=";
	const std::string body(text.data(),text.size());
	size_t at=0;
	while(at<body.size()) {
		size_t end=body.find('\n',at);
		if(end==std::string::npos) end=body.size();
		size_t lineEnd=end;
		while(lineEnd>at && (body[lineEnd-1]=='\r')) lineEnd--;
		if(lineEnd-at>needle.size() && body.compare(at,needle.size(),needle)==0) {
			const std::string value=body.substr(at+needle.size(),lineEnd-at-needle.size());
			if(value.empty()) return L"";
			const int wide=MultiByteToWideChar(CP_UTF8,0,value.c_str(),static_cast<int>(value.size()),nullptr,0);
			if(wide<=0) return L"";
			std::wstring result(static_cast<size_t>(wide),L'\0');
			MultiByteToWideChar(CP_UTF8,0,value.c_str(),static_cast<int>(value.size()),&result[0],wide);
			return result;
		}
		at=end+1;
	}
	return L"";
}

static SAFEARRAY* ToSafeArray(const std::vector<char>& bytes) {
	SAFEARRAY* array=SafeArrayCreateVector(VT_UI1,0,static_cast<ULONG>(bytes.size()));
	if(array==nullptr) return nullptr;
	void* data=nullptr;
	if(FAILED(SafeArrayAccessData(array,&data))) { SafeArrayDestroy(array); return nullptr; }
	memcpy(data,bytes.data(),bytes.size());
	SafeArrayUnaccessData(array);
	return array;
}

// Enter a named application domain and run the managed entry point there.
//
// ICLRRuntimeHost::ExecuteInDefaultAppDomain, used below when no domain is named, can only ever reach
// the default AppDomain. Every IIS worker runs application code in a secondary domain, so a resident
// that lands in the default one can never see the application's assemblies - which is exactly how this
// failed on a live worker, reporting "No loaded assembly matches hook_assembly".
//
// ICorRuntimeHost is the older hosting interface, still present on CLR v4, and it can enumerate live
// domains and hand each back as an _AppDomain. Selection is by friendly name because that is the only
// identifier available here: _AppDomain has no get_Id, being the .NET 1.x class interface, so the
// controller translates the caller's numeric app_domain_id into a name before writing it.
//
// The assembly is byte-loaded rather than bound from its path, which is also what the managed
// bootstrap does with its own embedded payloads. Nothing reads this assembly's Location, and the
// resolver's disk-provenance check covers the embedded payloads rather than the bootstrap itself.
static HRESULT EnterApplicationDomain(ICLRRuntimeInfo* runtimeInfo,const std::wstring& domainName,const std::wstring& assemblyPath,const std::wstring& parametersPath,DWORD* result) {
	ICorRuntimeHost* corHost=nullptr;
	HRESULT hr=runtimeInfo->GetInterface(CLSID_CorRuntimeHost,IID_ICorRuntimeHost,reinterpret_cast<void**>(&corHost));
	if(FAILED(hr)) return hr;

	std::vector<char> assemblyBytes;
	if(!ReadAllBytes(assemblyPath,assemblyBytes)) { corHost->Release(); return HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND); }

	HDOMAINENUM handle=nullptr;
	hr=corHost->EnumDomains(&handle);
	if(FAILED(hr)) { corHost->Release(); return hr; }

	// A domain that is never found is a distinct outcome from one that fails to run something, so it
	// gets its own error rather than a generic failure.
	HRESULT outcome=HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
	for(;;) {
		IUnknown* unknown=nullptr;
		if(corHost->NextDomain(handle,&unknown)!=S_OK || unknown==nullptr) break;
		mscorlib::_AppDomain* domain=nullptr;
		if(SUCCEEDED(unknown->QueryInterface(__uuidof(mscorlib::_AppDomain),reinterpret_cast<void**>(&domain))) && domain!=nullptr) {
			BSTR name=nullptr;
			if(SUCCEEDED(domain->get_FriendlyName(&name)) && name!=nullptr) {
				if(domainName==name) {
					SAFEARRAY* raw=ToSafeArray(assemblyBytes);
					if(raw==nullptr) outcome=E_OUTOFMEMORY;
					else {
						mscorlib::_Assembly* loaded=nullptr;
						outcome=domain->Load_3(raw,&loaded);
						SafeArrayDestroy(raw);
						if(SUCCEEDED(outcome) && loaded!=nullptr) {
							mscorlib::_Type* type=nullptr;
							BSTR typeName=SysAllocString(L"HookLab.Bootstrap.NativeEntry");
							outcome=loaded->GetType_2(typeName,&type);
							SysFreeString(typeName);
							if(SUCCEEDED(outcome) && type!=nullptr) {
								// InvokeMethod | Public | Static, spelled numerically because the generated
								// enum names vary between mscorlib.tlh revisions.
								const long bindingFlags=0x0100|0x0010|0x0008;
								SAFEARRAY* arguments=SafeArrayCreateVector(VT_VARIANT,0,1);
								if(arguments==nullptr) outcome=E_OUTOFMEMORY;
								else {
									VARIANT argument; VariantInit(&argument);
									argument.vt=VT_BSTR; argument.bstrVal=SysAllocString(parametersPath.c_str());
									LONG index=0;
									SafeArrayPutElement(arguments,&index,&argument);
									VariantClear(&argument);
									VARIANT target; VariantInit(&target); target.vt=VT_EMPTY;
									VARIANT returned; VariantInit(&returned);
									BSTR method=SysAllocString(L"Initialize");
									outcome=type->InvokeMember_3(method,static_cast<mscorlib::BindingFlags>(bindingFlags),nullptr,target,arguments,&returned);
									if(SUCCEEDED(outcome) && returned.vt==VT_I4 && result!=nullptr) *result=static_cast<DWORD>(returned.lVal);
									SysFreeString(method);
									VariantClear(&returned);
									SafeArrayDestroy(arguments);
								}
								type->Release();
							}
							if(loaded) loaded->Release();
						}
					}
					SysFreeString(name);
					domain->Release();
					unknown->Release();
					break;
				}
				SysFreeString(name);
			}
			domain->Release();
		}
		unknown->Release();
	}
	corHost->CloseEnum(handle);
	corHost->Release();
	return outcome;
}

static DWORD WINAPI InitializeHookLab(void*) {
	const std::wstring directory=ModuleDirectory();
	const std::wstring assembly=directory+L"\\HookLab.Bootstrap.dll";
	const std::wstring parameters=directory+L"\\initialize.params";

	// Absent means the default domain, which is every single-domain target and every case that worked
	// before this existed. Those keep the original path exactly, including its error behaviour.
	std::vector<char> parameterBytes;
	const std::wstring domainName=ReadAllBytes(parameters,parameterBytes)?ReadParameter(parameterBytes,"appdomain_name"):L"";

	ICLRMetaHost* metaHost=nullptr;
	ICLRRuntimeInfo* runtimeInfo=nullptr;
	ICLRRuntimeHost* runtimeHost=nullptr;
	DWORD result=1;
	HRESULT hr=CLRCreateInstance(CLSID_CLRMetaHost,IID_ICLRMetaHost,reinterpret_cast<void**>(&metaHost));
	if(SUCCEEDED(hr)) hr=metaHost->GetRuntime(L"v4.0.30319",IID_ICLRRuntimeInfo,reinterpret_cast<void**>(&runtimeInfo));
	BOOL loaded=FALSE;
	if(SUCCEEDED(hr)) hr=runtimeInfo->IsLoaded(GetCurrentProcess(),&loaded);
	if(SUCCEEDED(hr) && !loaded) hr=HRESULT_FROM_WIN32(ERROR_NOT_READY);
	if(SUCCEEDED(hr)) {
		if(domainName.empty()) {
			if(SUCCEEDED(hr)) hr=runtimeInfo->GetInterface(CLSID_CLRRuntimeHost,IID_ICLRRuntimeHost,reinterpret_cast<void**>(&runtimeHost));
			if(SUCCEEDED(hr)) hr=runtimeHost->ExecuteInDefaultAppDomain(assembly.c_str(),L"HookLab.Bootstrap.NativeEntry",L"Initialize",parameters.c_str(),&result);
		}
		else hr=EnterApplicationDomain(runtimeInfo,domainName,assembly,parameters,&result);
	}
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
