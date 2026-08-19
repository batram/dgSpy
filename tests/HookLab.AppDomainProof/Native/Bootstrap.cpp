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
#include <vector>

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

static bool ReadAllBytes(const std::wstring& path,std::vector<BYTE>& bytes) {
	HANDLE file=CreateFileW(path.c_str(),GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr);
	if(file==INVALID_HANDLE_VALUE) return false;
	LARGE_INTEGER size{};
	bool ok=GetFileSizeEx(file,&size)!=FALSE&&size.QuadPart>0&&size.QuadPart<64*1024*1024;
	if(ok) {
		bytes.resize(static_cast<size_t>(size.QuadPart));
		DWORD read=0;
		ok=ReadFile(file,bytes.data(),static_cast<DWORD>(bytes.size()),&read,nullptr)!=FALSE&&read==bytes.size();
	}
	CloseHandle(file);
	return ok;
}

// The bytes have to reach the CLR as a SAFEARRAY of unsigned char, which is how a managed byte[]
// crosses COM. This is the difference between "here are the bytes" and CreateInstanceFrom's "here is
// a path, go and bind it yourself".
static SAFEARRAY* ToSafeArray(const std::vector<BYTE>& bytes) {
	SAFEARRAY* array=SafeArrayCreateVector(VT_UI1,0,static_cast<ULONG>(bytes.size()));
	if(array==nullptr) return nullptr;
	void* data=nullptr;
	if(FAILED(SafeArrayAccessData(array,&data))) { SafeArrayDestroy(array); return nullptr; }
	memcpy(data,bytes.data(),bytes.size());
	SafeArrayUnaccessData(array);
	return array;
}

static std::wstring Hex(const wchar_t* label,HRESULT hr) {
	wchar_t buffer[64];
	swprintf_s(buffer,L"%s=0x%08X\r\n",label,static_cast<unsigned int>(hr));
	return buffer;
}

// Byte-load an assembly into an already-chosen domain and invoke a static method on it.
//
// _AppDomain::Load_3 is AppDomain.Load(byte[]) through COM, and _Type::InvokeMember_3 is
// Type.InvokeMember. Together they are the native equivalent of what the managed bootstrap does, which
// is what product HookLab needs: nothing is bound from a path, so there is no disk provenance for the
// CLR to prefer and no file the target has to be able to read at bind time.
static std::wstring EnterInMemory(mscorlib::_AppDomain* domain,const std::wstring& assemblyPath,const std::wstring& directory) {
	std::wstring log;
	std::vector<BYTE> bytes;
	if(!ReadAllBytes(assemblyPath,bytes)) { log+=L"read_payload_bytes=failed\r\n"; return log; }
	wchar_t sizeText[64];
	swprintf_s(sizeText,L"payload_bytes=%zu\r\n",bytes.size());
	log+=sizeText;

	SAFEARRAY* raw=ToSafeArray(bytes);
	if(raw==nullptr) { log+=L"safearray=failed\r\n"; return log; }

	mscorlib::_Assembly* loaded=nullptr;
	HRESULT hr=domain->Load_3(raw,&loaded);
	log+=Hex(L"load_3",hr);
	SafeArrayDestroy(raw);
	if(FAILED(hr)||loaded==nullptr) return log;

	mscorlib::_Type* type=nullptr;
	BSTR typeName=SysAllocString(L"AppDomainProof.Payload.InMemoryEntry");
	hr=loaded->GetType_2(typeName,&type);
	log+=Hex(L"get_type",hr);
	SysFreeString(typeName);

	if(SUCCEEDED(hr)&&type!=nullptr) {
		// InvokeMethod | Public | Static. Spelled numerically because the generated enum names vary
		// between mscorlib.tlh revisions and this prototype should not depend on which one is present.
		const long bindingFlags=0x0100|0x0010|0x0008;
		SAFEARRAY* arguments=SafeArrayCreateVector(VT_VARIANT,0,1);
		if(arguments!=nullptr) {
			VARIANT argument; VariantInit(&argument);
			argument.vt=VT_BSTR;
			argument.bstrVal=SysAllocString(directory.c_str());
			LONG index=0;
			SafeArrayPutElement(arguments,&index,&argument);
			VariantClear(&argument);

			VARIANT target; VariantInit(&target); target.vt=VT_EMPTY;   // static: no instance
			VARIANT result; VariantInit(&result);
			BSTR method=SysAllocString(L"Initialize");
			hr=type->InvokeMember_3(method,static_cast<mscorlib::BindingFlags>(bindingFlags),nullptr,target,arguments,&result);
			log+=Hex(L"invoke_member",hr);
			if(SUCCEEDED(hr)&&result.vt==VT_I4) {
				wchar_t returned[64];
				swprintf_s(returned,L"entry_returned=%d\r\n",result.lVal);
				log+=returned;
			}
			SysFreeString(method);
			VariantClear(&result);
			SafeArrayDestroy(arguments);
		}
		type->Release();
	}
	loaded->Release();
	return log;
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
							// The in-memory route runs FIRST, deliberately. CreateInstanceFrom below binds
							// this same file by path into this same domain, and if it ran first the byte
							// load would be operating on a domain that already had the assembly loaded from
							// disk - a confound that would leave "did Load_3 really load these bytes"
							// arguable. Run against a domain that has never seen the file and it is not.
							log+=EnterInMemory(domain,assembly,directory);

							// The path route, already proven, kept as the control: a run that fails the
							// in-memory route still shows whether the domain itself was reachable at all.
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
