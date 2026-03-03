# SmartPedido Agent (Windows)

Agente Windows multi-instância para SmartPedido, substituindo a implementação em PowerShell.

## Estrutura

- `src/AgentService`: Worker Service (.NET 8) executado como Windows Service
- `src/AgentConfigApp`: App WinForms para configuração por instância
- `installer/SmartPedidoAgent.iss`: instalador Inno Setup

## Funcionalidades principais

- Multi-instância real:
  - `SmartPedidoAgent-Kitchen`
  - `SmartPedidoAgent-Cashier`
- Configuração segregada por instância:
  - `C:\ProgramData\SmartPedido\kitchen\config.json`
  - `C:\ProgramData\SmartPedido\cashier\config.json`
- Poll de rotas:
  - Kitchen: `GET /api/agent/orders?status=PRINTING`
  - Cashier: `GET /api/agent/print-jobs?status=QUEUED&type=CASHIER_TABLE_SUMMARY`
- Header obrigatório: `x-agent-token`
- Modo de impressão:
  - `pdf` (SumatraPDF silencioso embutido em `sumatrapdf/SumatraPDF.exe`)
  - `escpos`:
    - `usbRaw` (Win32 `WritePrinter`)
    - `tcp9100` (socket)
- Idempotência local por instância (SQLite) com retenção de 7 dias
- Só marca impresso após sucesso real de impressão
- Retries HTTP exponenciais (Polly)
- Logs rolling (Serilog) por instância

## Configuração (`config.json`)

```json
{
  "apiBaseUrl": "https://api.smartpedido.com",
  "agentToken": "token-da-instancia",
  "agentType": "kitchen",
  "printMode": "pdf",
  "printerName": "EPSON TM-T20",
  "escposTransport": "usbRaw",
  "ip": "192.168.1.100",
  "port": 9100,
  "pollIntervalMs": 5000
}
```

## Build

Pré-requisitos:
- Windows 10/11 ou Windows Server
- .NET SDK 8
- Inno Setup 6 (para gerar instalador)

### Compilar solução

```powershell
dotnet restore SmartPedidoAgent.sln
dotnet build SmartPedidoAgent.sln -c Release
```

### Publicar binários

```powershell
dotnet publish .\src\AgentService\AgentService.csproj -c Release -r win-x64 -o .\publish\AgentService --self-contained false
dotnet publish .\src\AgentConfigApp\AgentConfigApp.csproj -c Release -r win-x64 -o .\publish\AgentConfigApp --self-contained false
```

> Copie `SumatraPDF.exe` para `publish\AgentService\sumatrapdf\SumatraPDF.exe`.

## Instalação

1. Gere instalador com Inno Setup usando `installer/SmartPedidoAgent.iss`.
2. Execute o setup como administrador.
3. Escolha: Kitchen, Cashier ou Ambos.
4. O setup cria:
   - pastas em `C:\ProgramData\SmartPedido\{kitchen|cashier}`
   - serviços Windows conforme seleção
   - atalhos de configuração para cada instância

## Uso da GUI (Config App)

A GUI abre com elevação administrativa automaticamente e permite:
- selecionar instância (`kitchen`/`cashier`)
- editar `API Base URL`, `Token`, `Tipo`, `Modo`, `Impressora`, `IP/Porta`, `Intervalo`
- listar impressoras instaladas
- `Salvar`
- `Testar conexão`
- `Reiniciar serviço`
- `Abrir pasta de logs`

## Logs

- Kitchen: `C:\ProgramData\SmartPedido\kitchen\logs\agent-YYYYMMDD.log`
- Cashier: `C:\ProgramData\SmartPedido\cashier\logs\agent-YYYYMMDD.log`

## Troubleshooting

1. **401/403**
   - validar token da instância
   - confirmar header `x-agent-token`
2. **Nada imprime em PDF**
   - validar existência de `sumatrapdf\SumatraPDF.exe`
   - conferir nome exato da impressora
3. **ESC/POS tcp9100 falha**
   - testar conectividade TCP para IP/porta da impressora
4. **Duplicidade**
   - verificar `state.db` da instância e se API não reenvia IDs diferentes para o mesmo pedido
5. **Serviço não sobe**
   - validar permissões de pasta em `ProgramData`
   - checar Event Viewer e logs do agente

## Comandos úteis (admin)

```powershell
sc query SmartPedidoAgent-Kitchen
sc query SmartPedidoAgent-Cashier
sc stop SmartPedidoAgent-Kitchen
sc start SmartPedidoAgent-Kitchen
sc stop SmartPedidoAgent-Cashier
sc start SmartPedidoAgent-Cashier
```
