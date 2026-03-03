#define MyAppName "SmartPedido Agent"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "SmartPedido"
#define MyAppExeName "AgentService.exe"

[Setup]
AppId={{2CC5A7E6-4607-4615-9775-61DFBA870552}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\SmartPedido\Agent
DisableProgramGroupPage=yes
OutputDir=.
OutputBaseFilename=SmartPedidoAgentSetup
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64

[Types]
Name: "full"; Description: "Kitchen + Cashier"
Name: "kitchen"; Description: "Kitchen only"
Name: "cashier"; Description: "Cashier only"

[Files]
Source: "..\publish\AgentService\*"; DestDir: "{app}\service"; Flags: recursesubdirs createallsubdirs
Source: "..\publish\AgentConfigApp\*"; DestDir: "{app}\config"; Flags: recursesubdirs createallsubdirs

[Dirs]
Name: "{commonappdata}\SmartPedido\kitchen"
Name: "{commonappdata}\SmartPedido\cashier"
Name: "{commonappdata}\SmartPedido\kitchen\logs"
Name: "{commonappdata}\SmartPedido\cashier\logs"

[Icons]
Name: "{group}\Configurar (Cozinha)"; Filename: "{app}\config\AgentConfigApp.exe"; Parameters: "kitchen"
Name: "{group}\Configurar (Caixa)"; Filename: "{app}\config\AgentConfigApp.exe"; Parameters: "cashier"

[Run]
Filename: "{cmd}"; Parameters: "/c sc create SmartPedidoAgent-Kitchen binPath= \"\"{app}\service\AgentService.exe\" --instance=kitchen\" start= auto"; Flags: runhidden; Tasks: ; Check: IsKitchenSelected
Filename: "{cmd}"; Parameters: "/c sc create SmartPedidoAgent-Cashier binPath= \"\"{app}\service\AgentService.exe\" --instance=cashier\" start= auto"; Flags: runhidden; Check: IsCashierSelected
Filename: "{cmd}"; Parameters: "/c sc start SmartPedidoAgent-Kitchen"; Flags: runhidden; Check: IsKitchenSelected
Filename: "{cmd}"; Parameters: "/c sc start SmartPedidoAgent-Cashier"; Flags: runhidden; Check: IsCashierSelected

[Code]
function IsKitchenSelected: Boolean;
begin
  Result := (WizardSetupType(False) = 'full') or (WizardSetupType(False) = 'kitchen');
end;

function IsCashierSelected: Boolean;
begin
  Result := (WizardSetupType(False) = 'full') or (WizardSetupType(False) = 'cashier');
end;
