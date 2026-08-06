# Agent workflow and deployment milestone

Delivered 2026-08-06:

- `dgspy mcp` starts or reuses the authenticated loopback Gateway and proxies MCP over stdio; manual
  lifecycle, diagnostics, and client-configuration commands remain available.
- MCP advertises workflow instructions, resources, annotations, recovery guidance, local/remote
  two-stage deployment, and bounded guided-debugging tools.
- Local deployment is versioned, per-user, rollbackable, and avoids runtime, service, task, and PATH
  installation. Remote packaging remains self-contained and never transfers or executes its archive.

Acceptance: Protocol 30/30, Gateway 255/255, Extension 28/28; complete build; zero-state stdio smoke;
self-contained package launch; and verification of the existing 1,738-file remote-host package.
