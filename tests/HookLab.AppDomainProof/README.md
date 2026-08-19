# AppDomain entry prototype

Road 1 subslice 6 rests on one question the task sheet leaves open:

> Whether native initialization can be taught to enter a chosen domain is an open question -
> `ExecuteInDefaultAppDomain` cannot, and the alternatives (a domain-manager, or entering through a
> thread already in the domain) need a prototype before design.

This directory is that prototype. It is a spike, not product: it exists to answer the question with
evidence, and its result decides the shape of subslice 6 and part of subslice 7.

## What it reproduces

The live failure was not an identity or dependency problem. The resident landed in the default
AppDomain, enumerated it, and reported *"No loaded assembly matches hook_assembly 'App_Web_jyghqjf5'
... Same-named assemblies loaded: (none)"*, while the assembly was plainly loaded in domain 2.

So the fixture is built to fail the same way:

- `Fixture` creates a second AppDomain and loads `AppDomainProof.App` **only** into it.
- `AppDomainProof.App` is never loaded into the default domain. Its presence is therefore proof of
  which domain the injected code is executing in, not merely a number it reports about itself.
- `DomainEntry` records the domain id, friendly name, and whether it can see that assembly.

A payload that lands in the default domain reports `can_see_app_assembly=False`, exactly as the
production resident did. One that reaches the application domain reports `True`.

## The two routes

| Route | Interface | Can it choose a domain? |
| --- | --- | --- |
| current product | `ICLRRuntimeHost::ExecuteInDefaultAppDomain` | No, by construction |
| prototype | `ICorRuntimeHost::EnumDomains` + `_AppDomain::CreateInstanceFrom` | the question |

`ICorRuntimeHost` is the older hosting interface, still available on CLR v4 through
`CLSID_CorRuntimeHost`. It can enumerate live AppDomains and hand back each one as an `_AppDomain`,
which is the managed `System.AppDomain` seen through COM - so anything `AppDomain` can do from managed
code is reachable from native code, including creating an object inside it.

`CreateInstanceFrom` is used rather than a method invoke because the constructor runs in the target
domain, which is all the prototype needs to prove. Product code would want a real entry point.

## Running it

    powershell -NoProfile -File tests\HookLab.AppDomainProof\run-proof.ps1

The script builds the fixture, the app marker, the managed payload and the native prototype, starts
the fixture, injects, and prints both proof files. Results land in `artifacts\`.
