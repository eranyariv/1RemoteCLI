1RemoteCLI — Version 1 Technical Specification


1. System Architecture Overview
1RemoteCLI provides secure remote terminal orchestration across multiple Windows machines via a central relay. It uses Microsoft Identity Platform (Entra ID + Personal Accounts) as the unified identity provider.

                  ┌──────────────────────────────────────────────┐
                  │            Microsoft Identity                │
                  │          (Entra ID / MSA "common")           │
                  └──────────────────────┬───────────────────────┘
                                         │ Token Issuance
                    ┌────────────────────┴────────────────────┐
                    ▼                                         ▼
        ┌───────────────────────┐                 ┌───────────────────────┐
        │  Mobile PWA Frontend  │                 │ Windows Machine Daemon│
        │  (React + xterm.js)   │                 │ (.NET 8 Worker / C#)  │
        └───────────┬───────────┘                 └───────────┬───────────┘
                    │                                         │
                    │ WSS + JWT (oid/sub validation)          │ WSS + JWT
                    │                                         │
                    └────────────► ┌───────────┐ ◄────────────┘
                                   │ Azure Hub │
                                   │ (SignalR) │
                                   └───────────┘
2. Authentication & Identity Design
A single Azure App Registration handles both personal Microsoft accounts (MSA) and work/school accounts (Entra ID).

Azure App Registration Setup
Account Type: Accounts in any organizational directory and personal Microsoft accounts (Tenant: common).

Exposed API Scope: api://1remotecli-api/Session.Access

Redirect URIs:

PWA: [https://1remotecli.azurewebsites.net](https://1remotecli.azurewebsites.net) (SPA Platform)

Daemon: http://localhost:8484/auth-callback or Native Device Code Flow.

Authorization Model
PWA Client: Logs in via @azure/msal-react. Obtains an Access Token with scope api://1remotecli-api/Session.Access.

Windows Daemon: Performs a one-time login via MSAL.NET using Device Code Flow or local browser loopback. Token is cached securely in Windows Credential Manager (ProtectedData / DPAPI).

Hub Session Isolation:

Upon WebSocket handshake, the Hub verifies the JWT token signature and claims.

The Hub extracts the oid (Object ID) or sub (Subject ID) claim.

Hub routing enforces that a PWA client with oid = X can ONLY discover and communicate with Daemons that authenticated with oid = X.

3. Component Architecture
A. Azure Relay Hub (1RemoteCLI.Hub)
Framework: ASP.NET Core 8 Web API + Azure SignalR Service (or self-hosted SignalR Hub).

State Management: In-memory tracking of connected daemons, active terminal sessions, and client connections.

                 Hub State Registry
┌───────────────────────────────────────────────────┐
│ User OID: 8a4f9b21-...                            │
│  ├─ Daemon: "Desktop-Dev-01" (ConnectionId: c1)   │
│  │   ├─ Session "s1": PID 10432 (pwsh.exe)       │
│  │   └─ Session "s2": PID 12890 (claude.exe)      │
│  └─ Daemon: "Laptop-Home"    (ConnectionId: c2)   │
└───────────────────────────────────────────────────┘
B. Windows Machine Daemon (1RemoteCLI.Daemon)
Framework: .NET 8 / C# Windows Background Service.

Terminal Engine: Windows ConPTY API wrapper (e.g., via Pty.Net or native P/Invoke to kernel32.dll CreatePseudoConsole).

Output Ring Buffer: Keeps the last 5,000 lines of stdout/stderr per active process in RAM so late-joining or reconnecting mobile clients immediately catch up.

C. Mobile PWA Frontend (1RemoteCLI.PWA)
Framework: React + Vite + Tailwind CSS + PWA Service Worker.

Terminal Renderer: @xterm/xterm + @xterm/addon-fit + @xterm/addon-web-links.

Mobile Enhancements: Onscreen macro bar providing Ctrl, Alt, Esc, Tab, ↑, ↓, ←, →, and a distinct red Ctrl+C (SIGINT) trigger.

4. SignalR / WebSocket Protocol Protocol Spec
A. Daemon Operations
1. Registration (Daemon -> Hub)
Sent immediately after WebSocket connection is established.

JSON
{
  "target": "RegisterDaemon",
  "arguments": [{
    "machineName": "DESKTOP-MAIN",
    "osVersion": "Windows 11 Pro 23H2",
    "availableExecutables": ["pwsh.exe", "cmd.exe", "claude.exe", "copilot.exe"]
  }]
}
2. Session Output Stream (Daemon -> Hub -> PWA)
Streams terminal VT100 string chunks.

JSON
{
  "target": "TerminalOutput",
  "arguments": [{
    "sessionId": "sess-8821",
    "data": "\u001b[32m\u2713 Task completed\u001b[0m\r\nPS C:\\Users\\Dev>"
  }]
}
B. Client Operations
1. Spawn New CLI Session (PWA -> Hub -> Daemon)
JSON
{
  "target": "SpawnSession",
  "arguments": [{
    "daemonId": "DESKTOP-MAIN",
    "executable": "claude.exe",
    "initialArgs": ["--dangerously-skip-permissions"],
    "workingDir": "C:\\Projects\\1RemoteCLI",
    "initialCols": 80,
    "initialRows": 24
  }]
}
2. Send Terminal Input (PWA -> Hub -> Daemon)
JSON
{
  "target": "SendInput",
  "arguments": [{
    "sessionId": "sess-8821",
    "input": "git status\r"
  }]
}
3. Resize Viewport (PWA -> Hub -> Daemon)
Re-shapes the remote ConPTY layout to match mobile screen orientation changes.

JSON
{
  "target": "ResizeTerminal",
  "arguments": [{
    "sessionId": "sess-8821",
    "cols": 64,
    "rows": 30
  }]
}
4. Interrupt Process (PWA -> Hub -> Daemon)
Sends Ctrl+C / SIGINT signal directly to the underlying Win32 pseudo-console.

JSON
{
  "target": "InterruptSession",
  "arguments": [{
    "sessionId": "sess-8821"
  }]
}
5. First-Time Setup & Flow Workflow
Daemon First Launch
1. Run "1remotecli-daemon.exe login"
2. Terminal displays: "Open https://microsoft.com/devicelogin and enter code: F82A-99B1"
3. User authenticates with Microsoft Account (Work or Personal).
4. Refresh token securely saved to DPAPI.
5. Service registers with Windows Service Manager and launches on boot.
Mobile PWA Usage
1. Open PWA on Mobile Chrome/Safari -> Add to Home Screen.
2. Tap "Login with Microsoft" -> Authenticate with SAME account.
3. Dashboard displays online Windows Daemons ("DESKTOP-MAIN [Online]", "LAPTOP-HOME [Offline]").
4. Tap machine -> Select existing running session or tap "+ New Session" (pwsh / claude / copilot).
5. Full interactive VT100 terminal renders in browser.
6. Resilience & Edge Cases
Challenge	Mitigation
Mobile Wi-Fi to 5G drop	Mobile PWA automatically reconnects using SignalR auto-reconnect logic. Upon reconnect, sends AttachSession(sessionId, lastReceivedChunkId) to flush only missing ring-buffer output.
Windows Reboot / Crash	Daemon auto-starts as a Windows Service (ServiceBase). Re-establishes outbound WSS connection to Azure Hub automatically.
ConPTY Resource Leak	Daemon tracks active Win32 handles; if process exits naturally, it sends SessionTerminated event to Hub and frees PTY resources.
Would you like me to generate the initial .NET C# ConPTY service code or the React xterm.js PWA code?

Yes
create a full spec file in markdown format

Python
spec_content = """# 1RemoteCLI — Technical Specification Document

**Document Version:** 1.0.0  
**Status:** Draft / Initial Architecture  
**Author:** Architecture Engineering Team  
**Target Platform:** Windows 11 / Windows 10, Azure App Service / SignalR, Web / Mobile PWA  

---

## 1. Executive Summary

**1RemoteCLI** is an open, self-hosted/cloud-assisted orchestration framework designed to bridge mobile devices (PWAs) with local interactive CLI developer workflows running across multiple Windows environments. 

As modern AI-assisted engineering shifts heavily toward terminal-centric agents—such as **Claude Code CLI**, **GitHub Copilot CLI**, custom **PowerShell** automation scripts, and **cmd** environments—developers lack a unified, secure mobile "steering wheel" to inspect agent output, approve interactive permission prompts, trigger command inputs, or issue interrupt signals (`SIGINT` / `Ctrl+C`) when away from their workstations.

Existing vendor tools (e.g., Claude Code Remote Control or GitHub Copilot Remote Access) operate in isolated silos, leaving generic Windows shells and multi-machine setups unaddressed. **1RemoteCLI** solves this by establishing an end-to-end, multi-tenant-capable system backed by **Microsoft Identity Platform (Personal & Work/School Accounts)**, combining native **Windows ConPTY API** process handling, an **Azure SignalR Relay Hub**, and a touch-optimized **React/xterm.js PWA**.

---

## 2. High-Level System Architecture

The system operates via three main logical tiers:
1. **Windows Daemon Client (`1RemoteCLI.Daemon`)**: A headless .NET 8 background service running on target Windows host machines. It spawns, manages, and captures VT100 standard output/input streams using the Windows Pseudo Console (ConPTY) API.
2. **Azure Relay Hub (`1RemoteCLI.Hub`)**: An ASP.NET Core 8 Web API / Azure SignalR Service that securely routes WebSocket traffic between authenticated Daemons and Mobile PWA sessions using token claims (`oid` / `sub`).
3. **Mobile PWA (`1RemoteCLI.PWA`)**: A progressive web application built with React, Vite, and `xterm.js` optimized for iOS and Android web viewports, featuring an accessory virtual macro bar for terminal input (`Ctrl+C`, `Esc`, `Tab`, directional arrows).

+-----------------------------------------------------------------------+
|                    Microsoft Identity Platform                        |
|             (Entra ID + Personal MSA - "common" tenant)                |
+-----------------------------------+-----------------------------------+
|
OAuth 2.0 / OIDC | JWT Issuance
|
+-------------------------+-------------------------+
|                                                   |
v                                                   v
+-----------------------+                           +-----------------------+
|   Mobile PWA Client   |                           | Windows Machine Daemon|
|  (React + xterm.js)   |                           | (.NET 8 Win32 Service)|
+-----------+-----------+                           +-----------+-----------+
|                                                   |
| Secure WebSockets (WSS)                           | Secure WebSockets (WSS)
| Bearer JWT (oid match)                            | Bearer JWT (oid match)
|                                                   |
+---------------------> +---------------+ <---------+
|  Azure Hub    |
|   (SignalR)   |
+---------------+


---

## 3. Authentication & Identity Design

1RemoteCLI relies on **Microsoft Identity Platform** as its single source of truth for identity, authentication, and machine-to-session routing.

### 3.1 Azure App Registration Topology
* **Application Type:** Multi-Tenant + Personal Microsoft Accounts (MSA).
* **Supported Account Types:** `Accounts in any organizational directory (Any Microsoft Entra ID tenant - Multitenant) and personal Microsoft accounts (e.g. Skype, Xbox)` (Tenant endpoint: `common`).
* **Exposed API Scope:** `api://1remotecli-app-id/Session.Access`
* **Redirect URIs:**
  * **Mobile PWA:** `https://1remotecli.azurewebsites.net` (Single-Page Application).
  * **Daemon Client:** `http://localhost:8484/auth-callback` (Public Native Client / Device Code Flow).

### 3.2 Authorization & Session Routing Security
1. **Token Acquisition:**
   * **PWA:** Authenticates via `@azure/msal-react` using Interactive PKCE Flow.
   * **Daemon:** Authenticates during first-time setup via Device Code Flow (`MSAL.NET`). The refresh token is encrypted and stored locally using the Windows Data Protection API (`ProtectedData` / DPAPI).
2. **Identity Verification & Routing Rule:**
   * Upon establishing a WebSocket/SignalR connection to `1RemoteCLI.Hub`, both PWA and Daemon present their OAuth Bearer Token.
   * The Hub validates the JWT signature, issuer, and expiration.
   * The Hub extracts the unique **`oid` (Object Identifier)** or **`sub` (Subject Identifier)** claim from the token.
   * **Isolation Principle:** A user connected via PWA with `oid = X` can *only* discover, view, or send commands to Windows Daemons that authenticated with the exact same `oid = X`. Cross-tenant or cross-user stream bleeding is strictly impossible at the Hub routing handler layer.

---

## 4. Component Technical Specifications

### 4.1 Windows Daemon (`1RemoteCLI.Daemon`)

The Windows Daemon is a .NET 8 background worker application engineered to run as a Windows Service (`ServiceBase`) or a standalone CLI console executable.

+-------------------------------------------------------------------------+
|                         1RemoteCLI Daemon Process                       |
|                                                                         |
|  +---------------------+   Outbound WSS    +-------------------------+  |
|  | SignalR Client Host | <---------------> | Azure Relay Hub         |  |
|  +----------+----------+                   +-------------------------+  |
|             |                                                           |
|             v                                                           |
|  +---------------------+   VT100 Bytes     +-------------------------+  |
|  | ConPTY Session Mgr  | <---------------> | Circular Ring Buffer    |  |
|  +----------+----------+                   | (5,000 Lines / RAM)     |  |
|             |                              +-------------------------+  |
|             v Native Win32 API                                          |
|  +-------------------------------------------------------------------+  |
|  | CreatePseudoConsole() -> HPCON Handle                             |  |
|  |   ├─ pwsh.exe (PID 1042)                                          |  |
|  |   ├─ claude.exe (PID 8812)                                        |  |
|  |   └─ cmd.exe (PID 12098)                                          |  |
|  +-------------------------------------------------------------------+  |
+-------------------------------------------------------------------------+


#### Key Architecture & Responsibilities:
1. **ConPTY Native Wrapper:**
   * Interoperates with `kernel32.dll` via P/Invoke (`CreatePseudoConsole`, `ClosePseudoConsole`, `ResizePseudoConsole`).
   * Spawns worker processes attached to anonymized pipe handles (`hPipeIn`, `hPipeOut`).
   * Reads raw VT100 escape sequences from `hPipeOut` and forwards them to the Hub stream while maintaining terminal color formatting, cursor positioning, and TUI layouts.
2. **In-Memory Ring Buffer (Output Cache):**
   * Maintains a ring buffer containing the last 5,000 output frames/lines per session.
   * When a mobile device disconnects (e.g., losing cellular signal) and reconnects, the Daemon flushes the buffered output back to the PWA to restore immediate screen context without interrupting execution.
3. **Process Lifecycle Management:**
   * Automatically monitors process completion or crashes via Windows handle events.
   * Cleans up Win32 PTY resources upon process exit and reports termination codes to the Hub.

---

### 4.2 Azure Relay Hub (`1RemoteCLI.Hub`)

Hosted on Azure App Service / Azure Functions + Azure SignalR Service, the Hub serves as a stateless router between Daemons and clients.

#### Key Functions:
* **Connection Registry:** Holds an in-memory dictionary mapping `UserId (OID) -> List<DaemonInstance> -> List<ActiveSession>`.
* **SignalR Hub Handlers:**
  * `RegisterDaemon(DaemonInfo info)`
  * `SpawnSession(SpawnRequest req)`
  * `SendInput(InputPayload payload)`
  * `ResizeTerminal(ResizePayload payload)`
  * `InterruptSession(InterruptPayload payload)`
  * `TerminalOutput(OutputPayload payload)`
* **Health Check & Keepalive:** Sends ping/pong heartbeats every 15 seconds to prune stale connections.

---

### 4.3 Mobile PWA (`1RemoteCLI.PWA`)

A lightweight, high-performance web interface designed for touchscreens and mobile web browsers.

+-------------------------------------------------------------------------+
|                      1RemoteCLI Mobile PWA                              |
|                                                                         |
|  +-------------------------------------------------------------------+  |
|  | Top Bar: [DESKTOP-MAIN v] | [claude.exe (Active) v] | Status: ON  |  |
|  +-------------------------------------------------------------------+  |
|  | xterm.js Terminal Viewport                                        |  |
|  |                                                                   |  |
|  |  > claude --dangerously-skip-permissions                          |  |
|  |  ? Allow file edit to src/app.ts? (y/n): _                      |  |
|  |                                                                   |  |
|  |                                                                   |  |
|  +-------------------------------------------------------------------+  |
|  | Onscreen Accessory Touch Bar                                      |  |
|  |  [ Ctrl ]  [ Alt ]  [ Tab ]  [ Esc ]  [ ↑ ]  [ ↓ ]  [ Ctrl+C ]   |  |
|  +-------------------------------------------------------------------+  |
+-------------------------------------------------------------------------+


#### Key Components:
1. **`xterm.js` Integration:** Rendered using `@xterm/xterm`, utilizing `@xterm/addon-fit` for dynamic viewport adjustments and `@xterm/addon-web-links` for hyperlink interaction.
2. **Virtual Accessory Keybar:** Addresses touch keyboard limitations on iOS/Android. Renders standard modifier keys and quick action buttons:
   * `Ctrl+C` (Sends `\\x03` / `SIGINT`)
   * `Tab` (Autocomplete trigger)
   * `Esc` (Exit modes / interrupt TUI)
   * Directional Arrows (`↑`, `↓`, `←`, `→` for navigating history and selection menus)
3. **Viewport Auto-Sync:** Monitors viewport resizing on mobile orientation changes and calculates columns/rows, sending a `ResizeTerminal` payload to the Daemon.

---

## 5. Protocol & API Specification

Communication between client, hub, and daemon is conducted over WebSocket channels managed by Microsoft SignalR.

### 5.1 Message Schemas

#### 1. Daemon Registration (`Daemon -> Hub`)
```json
{
  "target": "RegisterDaemon",
  "arguments": [
    {
      "machineId": "DESKTOP-9X21A",
      "machineName": "Primary Dev Workstation",
      "osVersion": "Microsoft Windows 11 Pro 10.0.22631",
      "executables": [
        "pwsh.exe",
        "cmd.exe",
        "claude.exe",
        "copilot.exe"
      ]
    }
  ]
}
2. Session Initialization (PWA -> Hub -> Daemon)
JSON
{
  "target": "SpawnSession",
  "arguments": [
    {
      "machineId": "DESKTOP-9X21A",
      "executable": "claude.exe",
      "args": ["--dangerously-skip-permissions"],
      "workingDirectory": "C:\\Projects\\1RemoteCLI",
      "cols": 80,
      "rows": 24
    }
  ]
}
3. Terminal Data Output Stream (Daemon -> Hub -> PWA)
JSON
{
  "target": "TerminalOutput",
  "arguments": [
    {
      "sessionId": "sess-a94f2-9901",
      "sequenceNumber": 4821,
      "data": "\u001b[32m✔ File updated successfully.\u001b[0m\r\n> "
    }
  ]
}
4. Terminal Input Payload (PWA -> Hub -> Daemon)
JSON
{
  "target": "SendInput",
  "arguments": [
    {
      "sessionId": "sess-a94f2-9901",
      "input": "y\r"
    }
  ]
}
5. Viewport Resize Payload (PWA -> Hub -> Daemon)
JSON
{
  "target": "ResizeTerminal",
  "arguments": [
    {
      "sessionId": "sess-a94f2-9901",
      "cols": 64,
      "rows": 32
    }
  ]
}
6. Process Interrupt (PWA -> Hub -> Daemon)
JSON
{
  "target": "InterruptSession",
  "arguments": [
    {
      "sessionId": "sess-a94f2-9901",
      "signal": "SIGINT"
    }
  ]
}
6. Security, Resilience, & Reliability
Scenario / Edge Case	System Behavior & Protection
Mobile Cellular Drop (3G/4G/5G)	SignalR client enters automatic retry mode. Upon reconnection, PWA transmits its last received sequenceNumber. Daemon re-streams missing frame buffers from memory.
Windows System Reboot	Daemon registers as an automatic Windows Service (ServiceBase). Upon boot, it reads DPAPI-encrypted OAuth credentials, authenticates silently, and connects to the Azure Hub.
Malformed Terminal VT100 Sequences	xterm.js handles ANSI escape parsing safely inside a web sandbox without executing raw scripts.
Unauthorized Connection Attempt	The Azure Hub validates the oid claim on every WebSocket frame. Requests missing valid Bearer tokens or targeting unowned machineIds are rejected instantly with HTTP 403 Forbidden.
7. Implementation Roadmap
Phase 1: Core Foundation (MVP)
Build .NET 8 ConPTY process engine and standalone Windows console daemon.

Implement Azure SignalR Hub with Microsoft Identity OIDC JWT validation.

Develop basic React + xterm.js PWA supporting static single-machine terminal sessions.

Phase 2: Session Management & Touch UX
Add local Ring Buffer output catching to the Daemon.

Implement mobile Touch Bar (Ctrl+C, arrow keys, macro inputs).

Support dynamic process spawning (pwsh, cmd, claude, copilot).

Phase 3: Daemon Windows Service & Resilience
Wrap Daemon as a native Windows Service with boot launch capability.

Implement Windows DPAPI security storage for refresh tokens.

Add auto-reconnect handling for network transitions.

Phase 4: Extended Multi-Machine Control
Add real-time CPU/RAM resource telemetry metrics per machine on the PWA dashboard.

Support native system notifications on PWA when long-running CLI tasks require user input or complete.

End of Specification Document.
"""

with open("1RemoteCLI_Specification.md", "w", encoding="utf-8") as f:
f.write(spec_content)

print("File written successfully.")


```text?code_stdout&code_event_index=1
File written successfully.

Your Markdown specification document for 1RemoteCLI is ready.

MD icon
1RemoteCLI_Specification
 MD 
Open
Specification Highlights Included in the Document
Executive Summary & Scope: Defines the core problem statement, multi-agent CLI fragmentation (Claude Code, GitHub Copilot CLI, PowerShell, cmd), and the solution vision.

High-Level System Architecture: Visual ASCII block diagrams detailing the interactions between the Windows Daemon, Azure SignalR Relay Hub, and Mobile React PWA.

Microsoft Identity Integration: Full setup for single Microsoft Identity Platform authentication (common tenant supporting Entra ID work/school and personal accounts) with oid claims validation for session routing.

Daemon Architecture & ConPTY Engine: Deep dive into the .NET 8 Win32 background service, native CreatePseudoConsole handling, DPAPI credential caching, and local 5,000-line ring buffers.

Mobile PWA & Touch UX: Terminal view powered by xterm.js, automatic responsive viewport auto-resizing, and the onscreen accessory macro bar (Ctrl+C, Tab, Esc, arrow keys).

SignalR/WebSocket Payload Schemas: Structured JSON message definitions for registration, spawning, streaming, inputs, viewport resizes, and interrupt signals.

Security & Edge-Case Resilience Matrix: Handling network drops, unexpected reboots, and multi-tenant connection isolation.

Implementation Roadmap: Phased breakdown from MVP to full production deployment.




Gemini is AI and can make mistakes.

# 1RemoteCLI — Technical Specification Document

**Document Version:** 1.0.0  
**Status:** Draft / Initial Architecture  
**Author:** Architecture Engineering Team  
**Target Platform:** Windows 11 / Windows 10, Azure App Service / SignalR, Web / Mobile PWA  

---

## 1. Executive Summary

**1RemoteCLI** is an open, self-hosted/cloud-assisted orchestration framework designed to bridge mobile devices (PWAs) with local interactive CLI developer workflows running across multiple Windows environments. 

As modern AI-assisted engineering shifts heavily toward terminal-centric agents—such as **Claude Code CLI**, **GitHub Copilot CLI**, custom **PowerShell** automation scripts, and **cmd** environments—developers lack a unified, secure mobile "steering wheel" to inspect agent output, approve interactive permission prompts, trigger command inputs, or issue interrupt signals (`SIGINT` / `Ctrl+C`) when away from their workstations.

Existing vendor tools (e.g., Claude Code Remote Control or GitHub Copilot Remote Access) operate in isolated silos, leaving generic Windows shells and multi-machine setups unaddressed. **1RemoteCLI** solves this by establishing an end-to-end, multi-tenant-capable system backed by **Microsoft Identity Platform (Personal & Work/School Accounts)**, combining native **Windows ConPTY API** process handling, an **Azure SignalR Relay Hub**, and a touch-optimized **React/xterm.js PWA**.

---

## 2. High-Level System Architecture

The system operates via three main logical tiers:
1. **Windows Daemon Client (`1RemoteCLI.Daemon`)**: A headless .NET 8 background service running on target Windows host machines. It spawns, manages, and captures VT100 standard output/input streams using the Windows Pseudo Console (ConPTY) API.
2. **Azure Relay Hub (`1RemoteCLI.Hub`)**: An ASP.NET Core 8 Web API / Azure SignalR Service that securely routes WebSocket traffic between authenticated Daemons and Mobile PWA sessions using token claims (`oid` / `sub`).
3. **Mobile PWA (`1RemoteCLI.PWA`)**: A progressive web application built with React, Vite, and `xterm.js` optimized for iOS and Android web viewports, featuring an accessory virtual macro bar for terminal input (`Ctrl+C`, `Esc`, `Tab`, directional arrows).

```
 +-----------------------------------------------------------------------+
 |                    Microsoft Identity Platform                        |
 |             (Entra ID + Personal MSA - "common" tenant)                |
 +-----------------------------------+-----------------------------------+
                                     |
                    OAuth 2.0 / OIDC | JWT Issuance
                                     |
           +-------------------------+-------------------------+
           |                                                   |
           v                                                   v
+-----------------------+                           +-----------------------+
|   Mobile PWA Client   |                           | Windows Machine Daemon|
|  (React + xterm.js)   |                           | (.NET 8 Win32 Service)|
+-----------+-----------+                           +-----------+-----------+
            |                                                   |
            | Secure WebSockets (WSS)                           | Secure WebSockets (WSS)
            | Bearer JWT (oid match)                            | Bearer JWT (oid match)
            |                                                   |
            +---------------------> +---------------+ <---------+
                                    |  Azure Hub    |
                                    |   (SignalR)   |
                                    +---------------+
```

---

## 3. Authentication & Identity Design

1RemoteCLI relies on **Microsoft Identity Platform** as its single source of truth for identity, authentication, and machine-to-session routing.

### 3.1 Azure App Registration Topology
* **Application Type:** Multi-Tenant + Personal Microsoft Accounts (MSA).
* **Supported Account Types:** `Accounts in any organizational directory (Any Microsoft Entra ID tenant - Multitenant) and personal Microsoft accounts (e.g. Skype, Xbox)` (Tenant endpoint: `common`).
* **Exposed API Scope:** `api://1remotecli-app-id/Session.Access`
* **Redirect URIs:**
  * **Mobile PWA:** `https://1remotecli.azurewebsites.net` (Single-Page Application).
  * **Daemon Client:** `http://localhost:8484/auth-callback` (Public Native Client / Device Code Flow).

### 3.2 Authorization & Session Routing Security
1. **Token Acquisition:**
   * **PWA:** Authenticates via `@azure/msal-react` using Interactive PKCE Flow.
   * **Daemon:** Authenticates during first-time setup via Device Code Flow (`MSAL.NET`). The refresh token is encrypted and stored locally using the Windows Data Protection API (`ProtectedData` / DPAPI).
2. **Identity Verification & Routing Rule:**
   * Upon establishing a WebSocket/SignalR connection to `1RemoteCLI.Hub`, both PWA and Daemon present their OAuth Bearer Token.
   * The Hub validates the JWT signature, issuer, and expiration.
   * The Hub extracts the unique **`oid` (Object Identifier)** or **`sub` (Subject Identifier)** claim from the token.
   * **Isolation Principle:** A user connected via PWA with `oid = X` can *only* discover, view, or send commands to Windows Daemons that authenticated with the exact same `oid = X`. Cross-tenant or cross-user stream bleeding is strictly impossible at the Hub routing handler layer.

---

## 4. Component Technical Specifications

### 4.1 Windows Daemon (`1RemoteCLI.Daemon`)

The Windows Daemon is a .NET 8 background worker application engineered to run as a Windows Service (`ServiceBase`) or a standalone CLI console executable.

```
+-------------------------------------------------------------------------+
|                         1RemoteCLI Daemon Process                       |
|                                                                         |
|  +---------------------+   Outbound WSS    +-------------------------+  |
|  | SignalR Client Host | <---------------> | Azure Relay Hub         |  |
|  +----------+----------+                   +-------------------------+  |
|             |                                                           |
|             v                                                           |
|  +---------------------+   VT100 Bytes     +-------------------------+  |
|  | ConPTY Session Mgr  | <---------------> | Circular Ring Buffer    |  |
|  +----------+----------+                   | (5,000 Lines / RAM)     |  |
|             |                              +-------------------------+  |
|             v Native Win32 API                                          |
|  +-------------------------------------------------------------------+  |
|  | CreatePseudoConsole() -> HPCON Handle                             |  |
|  |   ├─ pwsh.exe (PID 1042)                                          |  |
|  |   ├─ claude.exe (PID 8812)                                        |  |
|  |   └─ cmd.exe (PID 12098)                                          |  |
|  +-------------------------------------------------------------------+  |
+-------------------------------------------------------------------------+
```

#### Key Architecture & Responsibilities:
1. **ConPTY Native Wrapper:**
   * Interoperates with `kernel32.dll` via P/Invoke (`CreatePseudoConsole`, `ClosePseudoConsole`, `ResizePseudoConsole`).
   * Spawns worker processes attached to anonymized pipe handles (`hPipeIn`, `hPipeOut`).
   * Reads raw VT100 escape sequences from `hPipeOut` and forwards them to the Hub stream while maintaining terminal color formatting, cursor positioning, and TUI layouts.
2. **In-Memory Ring Buffer (Output Cache):**
   * Maintains a ring buffer containing the last 5,000 output frames/lines per session.
   * When a mobile device disconnects (e.g., losing cellular signal) and reconnects, the Daemon flushes the buffered output back to the PWA to restore immediate screen context without interrupting execution.
3. **Process Lifecycle Management:**
   * Automatically monitors process completion or crashes via Windows handle events.
   * Cleans up Win32 PTY resources upon process exit and reports termination codes to the Hub.

---

### 4.2 Azure Relay Hub (`1RemoteCLI.Hub`)

Hosted on Azure App Service / Azure Functions + Azure SignalR Service, the Hub serves as a stateless router between Daemons and clients.

#### Key Functions:
* **Connection Registry:** Holds an in-memory dictionary mapping `UserId (OID) -> List<DaemonInstance> -> List<ActiveSession>`.
* **SignalR Hub Handlers:**
  * `RegisterDaemon(DaemonInfo info)`
  * `SpawnSession(SpawnRequest req)`
  * `SendInput(InputPayload payload)`
  * `ResizeTerminal(ResizePayload payload)`
  * `InterruptSession(InterruptPayload payload)`
  * `TerminalOutput(OutputPayload payload)`
* **Health Check & Keepalive:** Sends ping/pong heartbeats every 15 seconds to prune stale connections.

---

### 4.3 Mobile PWA (`1RemoteCLI.PWA`)

A lightweight, high-performance web interface designed for touchscreens and mobile web browsers.

```
+-------------------------------------------------------------------------+
|                      1RemoteCLI Mobile PWA                              |
|                                                                         |
|  +-------------------------------------------------------------------+  |
|  | Top Bar: [DESKTOP-MAIN v] | [claude.exe (Active) v] | Status: ON  |  |
|  +-------------------------------------------------------------------+  |
|  | xterm.js Terminal Viewport                                        |  |
|  |                                                                   |  |
|  |  > claude --dangerously-skip-permissions                          |  |
|  |  ? Allow file edit to src/app.ts? (y/n): _                      |  |
|  |                                                                   |  |
|  |                                                                   |  |
|  +-------------------------------------------------------------------+  |
|  | Onscreen Accessory Touch Bar                                      |  |
|  |  [ Ctrl ]  [ Alt ]  [ Tab ]  [ Esc ]  [ ↑ ]  [ ↓ ]  [ Ctrl+C ]   |  |
|  +-------------------------------------------------------------------+  |
+-------------------------------------------------------------------------+
```

#### Key Components:
1. **`xterm.js` Integration:** Rendered using `@xterm/xterm`, utilizing `@xterm/addon-fit` for dynamic viewport adjustments and `@xterm/addon-web-links` for hyperlink interaction.
2. **Virtual Accessory Keybar:** Addresses touch keyboard limitations on iOS/Android. Renders standard modifier keys and quick action buttons:
   * `Ctrl+C` (Sends `\x03` / `SIGINT`)
   * `Tab` (Autocomplete trigger)
   * `Esc` (Exit modes / interrupt TUI)
   * Directional Arrows (`↑`, `↓`, `←`, `→` for navigating history and selection menus)
3. **Viewport Auto-Sync:** Monitors viewport resizing on mobile orientation changes and calculates columns/rows, sending a `ResizeTerminal` payload to the Daemon.

---

## 5. Protocol & API Specification

Communication between client, hub, and daemon is conducted over WebSocket channels managed by Microsoft SignalR.

### 5.1 Message Schemas

#### 1. Daemon Registration (`Daemon -> Hub`)
```json
{
  "target": "RegisterDaemon",
  "arguments": [
    {
      "machineId": "DESKTOP-9X21A",
      "machineName": "Primary Dev Workstation",
      "osVersion": "Microsoft Windows 11 Pro 10.0.22631",
      "executables": [
        "pwsh.exe",
        "cmd.exe",
        "claude.exe",
        "copilot.exe"
      ]
    }
  ]
}
```

#### 2. Session Initialization (`PWA -> Hub -> Daemon`)
```json
{
  "target": "SpawnSession",
  "arguments": [
    {
      "machineId": "DESKTOP-9X21A",
      "executable": "claude.exe",
      "args": ["--dangerously-skip-permissions"],
      "workingDirectory": "C:\Projects\1RemoteCLI",
      "cols": 80,
      "rows": 24
    }
  ]
}
```

#### 3. Terminal Data Output Stream (`Daemon -> Hub -> PWA`)
```json
{
  "target": "TerminalOutput",
  "arguments": [
    {
      "sessionId": "sess-a94f2-9901",
      "sequenceNumber": 4821,
      "data": " [32m✔ File updated successfully. [0m
> "
    }
  ]
}
```

#### 4. Terminal Input Payload (`PWA -> Hub -> Daemon`)
```json
{
  "target": "SendInput",
  "arguments": [
    {
      "sessionId": "sess-a94f2-9901",
      "input": "y
"
    }
  ]
}
```

#### 5. Viewport Resize Payload (`PWA -> Hub -> Daemon`)
```json
{
  "target": "ResizeTerminal",
  "arguments": [
    {
      "sessionId": "sess-a94f2-9901",
      "cols": 64,
      "rows": 32
    }
  ]
}
```

#### 6. Process Interrupt (`PWA -> Hub -> Daemon`)
```json
{
  "target": "InterruptSession",
  "arguments": [
    {
      "sessionId": "sess-a94f2-9901",
      "signal": "SIGINT"
    }
  ]
}
```

---

## 6. Security, Resilience, & Reliability

| Scenario / Edge Case | System Behavior & Protection |
| :--- | :--- |
| **Mobile Cellular Drop (3G/4G/5G)** | SignalR client enters automatic retry mode. Upon reconnection, PWA transmits its last received `sequenceNumber`. Daemon re-streams missing frame buffers from memory. |
| **Windows System Reboot** | Daemon registers as an automatic Windows Service (`ServiceBase`). Upon boot, it reads DPAPI-encrypted OAuth credentials, authenticates silently, and connects to the Azure Hub. |
| **Malformed Terminal VT100 Sequences** | `xterm.js` handles ANSI escape parsing safely inside a web sandbox without executing raw scripts. |
| **Unauthorized Connection Attempt** | The Azure Hub validates the `oid` claim on every WebSocket frame. Requests missing valid Bearer tokens or targeting unowned `machineId`s are rejected instantly with HTTP 403 Forbidden. |

---

## 7. Implementation Roadmap

### Phase 1: Core Foundation (MVP)
* Build `.NET 8 ConPTY` process engine and standalone Windows console daemon.
* Implement Azure SignalR Hub with Microsoft Identity OIDC JWT validation.
* Develop basic React + `xterm.js` PWA supporting static single-machine terminal sessions.

### Phase 2: Session Management & Touch UX
* Add local Ring Buffer output catching to the Daemon.
* Implement mobile Touch Bar (`Ctrl+C`, arrow keys, macro inputs).
* Support dynamic process spawning (`pwsh`, `cmd`, `claude`, `copilot`).

### Phase 3: Daemon Windows Service & Resilience
* Wrap Daemon as a native Windows Service with boot launch capability.
* Implement Windows DPAPI security storage for refresh tokens.
* Add auto-reconnect handling for network transitions.

### Phase 4: Extended Multi-Machine Control
* Add real-time CPU/RAM resource telemetry metrics per machine on the PWA dashboard.
* Support native system notifications on PWA when long-running CLI tasks require user input or complete.

---
*End of Specification Document.*
1RemoteCLI_Specification.md
Displaying 1RemoteCLI_Specification.md.