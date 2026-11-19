; Instalador do Streemu (Inno Setup 6). Quem gera é o scripts/build-release.ps1, que passa:
;   /DVersao=0.2.5  /DOrigem=<pasta dist\Streemu-X.Y.Z pronta>  /DSaida=<pasta dist>
; Instala por usuário (sem administrador) em %LOCALAPPDATA%\Programs\Streemu: a pasta precisa ser gravável para a
; atualização automática (spec 004) trocar o exe.
; O estado (%LOCALAPPDATA%\Streemu) não é tocado.
; O instalador não leva grupo.json (spec 022): o app tem o endereço do servidor embutido e não usa senha.
; No fim da instalação o instalador roda "Streemu.exe --atualizar" (sem janela): o app traz a última versão da main para a pasta instalada
; (spec 004, seção 7). Por isso um instalador antigo também instala a versão mais nova; sem internet isso falha em silêncio e o app
; se atualiza sozinho na primeira abertura.
; Arte do assistente: a Mumu jogando, reduzida por instalador/arte/gerar-arte.ps1 (tamanhos por DPI explicados lá).

#ifndef Versao
  #define Versao "0.0.0"
#endif
#ifndef Origem
  #error Passe /DOrigem=<pasta da release> (o build-release.ps1 faz isso)
#endif
#ifndef Saida
  #define Saida "..\dist"
#endif

#define Nome "Streemu"
#define Exe "Streemu.exe"
; mesmo valor que o app grava em "Iniciar com o Windows" (Util/StartWithWindows.cs): app e instalador veem a mesma escolha
#define RunKey "Software\Microsoft\Windows\CurrentVersion\Run"
#define RunValor "Streemu"
#define DiscordAppId "1568336028157218906"
#define UninstallKey "Software\Microsoft\Windows\CurrentVersion\Uninstall\{4A4DCC89-4B0D-462F-88DB-30FDC59451D3}_is1"

[Setup]
; AppId fixo do Streemu: é ele que faz um instalador novo atualizar a instalação existente em vez de criar outra
AppId={{4A4DCC89-4B0D-462F-88DB-30FDC59451D3}
AppName={#Nome}
AppVersion={#Versao}
AppVerName={#Nome} {#Versao}
AppPublisher={#Nome} (grupo de amigos)
DefaultDirName={localappdata}\Programs\{#Nome}
; ao atualizar, o Inno reaproveita a pasta da instalação anterior
UsePreviousAppDir=yes
DisableDirPage=auto
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir={#Saida}
OutputBaseFilename={#Nome}-{#Versao}-instalador
SetupIconFile=..\app\Streemu\streemu.ico
UninstallDisplayIcon={app}\{#Exe}
UninstallDisplayName={#Nome}
Compression=lzma2/ultra64
SolidCompression=yes
#if Ver >= EncodeVer(6, 6, 0)
; claro ou escuro conforme o Windows (o app é escuro; quem usa o Windows escuro vê o instalador escuro também)
WizardStyle=modern dynamic
#else
WizardStyle=modern
#endif
#if Ver >= EncodeVer(6, 5, 2)
; a Mumu jogando nas páginas de boas-vindas e de fim, e o rosto dela no canto das outras páginas. PNG com transparência
; (Inno Setup 6.5.2 ou mais novo); o Setup escolhe o tamanho que combina com o DPI do PC.
WizardImageFile=arte\mumu-lateral-202.png,arte\mumu-lateral-269.png,arte\mumu-lateral-336.png,arte\mumu-lateral-430.png,arte\mumu-lateral-534.png
WizardSmallImageFile=arte\mumu-canto-58.png,arte\mumu-canto-77.png,arte\mumu-canto-97.png,arte\mumu-canto-124.png,arte\mumu-canto-159.png
; lilás bem claro atrás da Mumu (o AccentSoft do app, clareado); no escuro, o fundo da janela do app (Theme.Surface)
WizardImageBackColor=#EEE9FC
#if Ver >= EncodeVer(6, 6, 0)
WizardImageBackColorDynamicDark=#2A2640
#endif
#else
  #pragma message "Inno Setup anterior ao 6.5.2: o instalador sai sem a Mumu (PNG). Atualize com: winget upgrade JRSoftware.InnoSetup"
#endif
; fecha o app (e o ffmpeg/ffplay dele) antes de trocar os arquivos; o app sai de verdade no fim de sessão
CloseApplications=yes
CloseApplicationsFilter={#Exe},ffmpeg.exe,ffplay.exe
RestartApplications=no

[Languages]
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Messages]
WelcomeLabel2=Isto instala o [name/ver] neste PC, só para o seu usuário (não precisa de administrador).%n%nA Mumu já pegou o controle. Clique em Avançar.

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na área de trabalho"; GroupDescription: "Atalhos:"
Name: "inicio"; Description: "Iniciar o {#Nome} com o Windows (fica na bandeja, perto do relógio)"; GroupDescription: "Ao ligar o PC:"
Name: "firewall"; Description: "Liberar o {#Nome} no Firewall do Windows (o Windows pede permissão de administrador uma vez)"; GroupDescription: "Rede:"

[Files]
Source: "{#Origem}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#Nome}"; Filename: "{app}\{#Exe}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#Nome}"; Filename: "{app}\{#Exe}"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "{#RunKey}"; ValueType: string; ValueName: "{#RunValor}"; ValueData: """{app}\{#Exe}"" --inicio"; Tasks: inicio; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#Exe}"; Parameters: "--atualizar"; StatusMsg: "Trazendo a versão mais nova do {#Nome} (sem internet, ele se atualiza sozinho ao abrir)..."; Flags: runhidden waituntilterminated
Filename: "{app}\{#Exe}"; Description: "Abrir o {#Nome} agora"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; o que o app cria ao lado do exe (relay-check.txt, discord-check.txt, exe antigo da atualização)
Type: filesandordirs; Name: "{app}"

[Code]
{ Na desinstalação, apaga do registro o que o próprio app grava (Run, streemu://, discord-<id>://), mas só quando
  aponta para ESTA pasta: um Streemu de zip em outra pasta continua funcionando. }
function PointsHere(const Value: String): Boolean;
begin
  Result := Pos(Lowercase(ExpandConstant('{app}')), Lowercase(Value)) > 0;
end;

procedure CleanKey(const Key: String);
var
  Cmd: String;
begin
  if RegQueryStringValue(HKCU, Key + '\shell\open\command', '', Cmd) and PointsHere(Cmd) then
    RegDeleteKeyIncludingSubkeys(HKCU, Key);
end;

{ Mesmo comando do app (FirewallCheck.AllowThisApp): apaga as regras antigas deste exe (bloqueio vence permissão) e cria
  a de entrada. Sem isso, a primeira abertura da pasta instalada mostrava o aviso do Windows e o app acusava bloqueio.
  Recusar o UAC não é erro: o app oferece liberar depois pelo botão "Firewall do Windows...".
  A regra se chama Streemu, o mesmo nome que o app cria e procura (`FirewallCheck`). }
procedure AllowFirewall();
var
  Exe, Params: String;
  Code: Integer;
begin
  Exe := ExpandConstant('{app}\{#Exe}');
  Params := '/s /c "netsh advfirewall firewall delete rule name=all program="' + Exe + '" & ' +
            'netsh advfirewall firewall add rule name="Streemu" dir=in action=allow program="' + Exe + '" enable=yes profile=any"';
  if ShellExec('runas', ExpandConstant('{sys}\cmd.exe'), Params, '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Log('Firewall: liberado (codigo ' + IntToStr(Code) + ')')
  else
    Log('Firewall: nao liberado (' + SysErrorMessage(Code) + ')');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('firewall') then
    AllowFirewall();
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Cmd: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    if RegQueryStringValue(HKCU, '{#RunKey}', '{#RunValor}', Cmd) and PointsHere(Cmd) then
      RegDeleteValue(HKCU, '{#RunKey}', '{#RunValor}');
    CleanKey('Software\Classes\Streemu');
    CleanKey('Software\Classes\discord-{#DiscordAppId}');
  end;
end;
